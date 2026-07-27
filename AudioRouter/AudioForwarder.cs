using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioRouter;

public sealed class AudioForwarder : IDisposable
{
    private const int OutputLatencyMilliseconds = 40;
    private readonly object _syncRoot = new();
    private MMDevice? _sourceDevice;
    private MMDevice? _outputDevice;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private PrebufferingWaveProvider? _prebuffer;
    private AdaptiveResamplingSampleProvider? _adaptiveResampler;
    private MediaFoundationResampler? _resampler;
    private bool _isStopping;
    private long _captureCallbackCount;
    private long _capturedBytes;
    private long _peakBufferedDurationTicks;

    public event EventHandler<Exception>? ForwardingFailed;

    public bool IsRunning { get; private set; }

    public AudioForwarderDiagnostics GetDiagnostics()
    {
        var bufferedDuration = _buffer?.BufferedDuration ?? TimeSpan.Zero;
        UpdatePeakBufferedDuration(bufferedDuration.Ticks);
        return new AudioForwarderDiagnostics(
            IsRunning,
            Interlocked.Read(ref _captureCallbackCount),
            Interlocked.Read(ref _capturedBytes),
            bufferedDuration,
            TimeSpan.FromTicks(
                Interlocked.Read(ref _peakBufferedDurationTicks)),
            bufferedDuration +
            TimeSpan.FromMilliseconds(OutputLatencyMilliseconds));
    }

    public void Start(string sourceDeviceId, string outputDeviceId, int latencyMilliseconds)
    {
        lock (_syncRoot)
        {
            if (IsRunning)
            {
                return;
            }

            try
            {
                ResetDiagnostics();
                using var enumerator = new MMDeviceEnumerator();
                _sourceDevice = enumerator.GetDevice(sourceDeviceId);
                _outputDevice = enumerator.GetDevice(outputDeviceId);

                _capture = new WasapiLoopbackCapture(_sourceDevice);
                _buffer = new BufferedWaveProvider(_capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(
                        Math.Max(latencyMilliseconds * 10, 2000)),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };

                var prebufferDuration = TimeSpan.FromMilliseconds(
                    Math.Max(
                        latencyMilliseconds - OutputLatencyMilliseconds,
                        20));
                _prebuffer = new PrebufferingWaveProvider(
                    _buffer,
                    prebufferDuration);
                _adaptiveResampler = new AdaptiveResamplingSampleProvider(
                    _prebuffer.ToSampleProvider(),
                    _outputDevice.AudioClient.MixFormat.SampleRate,
                    () => _buffer?.BufferedDuration ?? TimeSpan.Zero,
                    prebufferDuration);

                _resampler = new MediaFoundationResampler(
                    _adaptiveResampler.ToWaveProvider(),
                    _outputDevice.AudioClient.MixFormat)
                {
                    ResamplerQuality = 60
                };

                _output = new WasapiOut(
                    _outputDevice,
                    AudioClientShareMode.Shared,
                    true,
                    OutputLatencyMilliseconds);

                _capture.DataAvailable += Capture_DataAvailable;
                _capture.RecordingStopped += Capture_RecordingStopped;
                _output.PlaybackStopped += Output_PlaybackStopped;

                _output.Init(_resampler);
                _capture.StartRecording();
                _output.Play();
                IsRunning = true;
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (_isStopping)
            {
                return;
            }

            _isStopping = true;
            try
            {
                _capture?.StopRecording();
                _output?.Stop();
                _buffer?.ClearBuffer();
                ReleaseResources();
            }
            finally
            {
                _isStopping = false;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        AudioThreadPriority.PromoteCurrentThread();
        Interlocked.Increment(ref _captureCallbackCount);

        if (e.BytesRecorded > 0)
        {
            _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref _capturedBytes, e.BytesRecorded);
        }
    }

    private void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _captureCallbackCount, 0);
        Interlocked.Exchange(ref _capturedBytes, 0);
        Interlocked.Exchange(ref _peakBufferedDurationTicks, 0);
    }

    private void UpdatePeakBufferedDuration(long bufferedDurationTicks)
    {
        var currentPeak = Interlocked.Read(ref _peakBufferedDurationTicks);

        while (bufferedDurationTicks > currentPeak)
        {
            var observedPeak = Interlocked.CompareExchange(
                ref _peakBufferedDurationTicks,
                bufferedDurationTicks,
                currentPeak);
            if (observedPeak == currentPeak)
            {
                return;
            }

            currentPeak = observedPeak;
        }
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        HandleUnexpectedStop(e.Exception);
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        HandleUnexpectedStop(e.Exception);
    }

    private void HandleUnexpectedStop(Exception? exception)
    {
        if (_isStopping || exception == null)
        {
            return;
        }

        ForwardingFailed?.Invoke(this, exception);
    }

    private void ReleaseResources()
    {
        IsRunning = false;

        if (_capture != null)
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.RecordingStopped -= Capture_RecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        if (_output != null)
        {
            _output.PlaybackStopped -= Output_PlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        _resampler?.Dispose();
        _resampler = null;
        _adaptiveResampler = null;
        _prebuffer = null;
        _buffer = null;

        _sourceDevice?.Dispose();
        _sourceDevice = null;
        _outputDevice?.Dispose();
        _outputDevice = null;
    }
}
