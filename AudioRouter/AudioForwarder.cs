using System.Runtime;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioRouter;

public sealed class AudioForwarder : IDisposable
{
    private enum ForwarderState
    {
        Stopped,
        Starting,
        Running,
        Stopping
    }

    private readonly record struct OutputConfiguration(
        AudioClientShareMode ShareMode,
        int LatencyMilliseconds);

    private const int StableOutputLatencyMilliseconds = 40;
    private const int MinimumPrebufferMilliseconds = 20;
    private readonly object _syncRoot = new();
    private readonly AudioDiagnosticsService _diagnostics = new();
    private MMDevice? _sourceDevice;
    private MMDevice? _outputDevice;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private PrebufferingWaveProvider? _prebuffer;
    private AdaptiveResamplingSampleProvider? _adaptiveResampler;
    private MediaFoundationResampler? _resampler;
    private int _failureSignaled;
    private int _state = (int)ForwarderState.Stopped;
    private int _activeOutputLatencyMilliseconds =
        StableOutputLatencyMilliseconds;
    private OutputConfiguration[] _outputConfigurations = [];
    private int _activeOutputConfigurationIndex = -1;
    private GCLatencyMode? _previousGcLatencyMode;
    private bool _isExclusiveOutput;

    public event EventHandler<Exception>? ForwardingFailed;

    public bool IsRunning =>
        (ForwarderState)Volatile.Read(ref _state) ==
        ForwarderState.Running;

    public AudioForwarderDiagnostics GetDiagnostics()
    {
        var bufferedDuration = _buffer?.BufferedDuration ?? TimeSpan.Zero;
        return _diagnostics.GetSnapshot(
            IsRunning,
            bufferedDuration,
            _activeOutputLatencyMilliseconds,
            _isExclusiveOutput);
    }

    public void Start(string sourceDeviceId, string outputDeviceId, int latencyMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDeviceId);
        if (string.Equals(
                sourceDeviceId,
                outputDeviceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "监听设备和输出设备不能相同。",
                nameof(outputDeviceId));
        }

        if (latencyMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latencyMilliseconds),
                "延迟必须大于 0 毫秒。");
        }

        lock (_syncRoot)
        {
            if ((ForwarderState)_state != ForwarderState.Stopped)
            {
                return;
            }

            Volatile.Write(ref _state, (int)ForwarderState.Starting);
            try
            {
                _diagnostics.Reset();
                Interlocked.Exchange(ref _failureSignaled, 0);
                EnableLowLatencyGc();
                LoadDevices(sourceDeviceId, outputDeviceId);
                InitializeCapture(latencyMilliseconds);
                InitializeProcessingPipeline(latencyMilliseconds);
                InitializeOutput();
                SubscribeEvents();
                StartComponents();
                Volatile.Write(ref _state, (int)ForwarderState.Running);
            }
            catch
            {
                Interlocked.Exchange(ref _failureSignaled, 1);
                Volatile.Write(ref _state, (int)ForwarderState.Stopping);
                StopAndReleaseResources();
                Volatile.Write(ref _state, (int)ForwarderState.Stopped);
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            var state = (ForwarderState)_state;
            if (state is ForwarderState.Stopped or ForwarderState.Stopping)
            {
                return;
            }

            Volatile.Write(ref _state, (int)ForwarderState.Stopping);
            try
            {
                StopAndReleaseResources();
            }
            finally
            {
                Volatile.Write(ref _state, (int)ForwarderState.Stopped);
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!AudioThreadPriority.IsCurrentThreadPromoted)
        {
            AudioThreadPriority.PromoteCurrentThread();
        }

        _diagnostics.RecordCapture(e.BytesRecorded);

        if (e.BytesRecorded > 0)
        {
            _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void LoadDevices(string sourceDeviceId, string outputDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        _sourceDevice = enumerator.GetDevice(sourceDeviceId);
        _outputDevice = enumerator.GetDevice(outputDeviceId);
    }

    private void InitializeCapture(int latencyMilliseconds)
    {
        _capture = new WasapiLoopbackCapture(_sourceDevice!);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(
                Math.Max(latencyMilliseconds * 10, 2000)),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
    }

    private void InitializeProcessingPipeline(int latencyMilliseconds)
    {
        _outputConfigurations =
            GetOutputConfigurations(latencyMilliseconds);
        var prebufferMilliseconds =
            GetPrebufferMilliseconds(latencyMilliseconds);
        var prebufferDuration = TimeSpan.FromMilliseconds(
            prebufferMilliseconds);
        _prebuffer = new PrebufferingWaveProvider(
            _buffer!,
            prebufferDuration);
        _adaptiveResampler = new AdaptiveResamplingSampleProvider(
            _prebuffer.ToSampleProvider(),
            _outputDevice!.AudioClient.MixFormat.SampleRate,
            () => _buffer?.BufferedDuration ?? TimeSpan.Zero,
            prebufferDuration);

        // WDL 负责校正两张独立声卡的时钟漂移；Media Foundation
        // 负责将声道、位深和封装格式转换为目标设备的混音格式。
        _resampler = new MediaFoundationResampler(
            _adaptiveResampler.ToWaveProvider(),
            _outputDevice.AudioClient.MixFormat)
        {
            ResamplerQuality = 60
        };
    }

    private void InitializeOutput()
    {
        Exception? lastException = null;
        for (var index = 0;
             index < _outputConfigurations.Length;
             index++)
        {
            try
            {
                _output = CreateOutput(
                    _resampler!,
                    _outputConfigurations[index]);
                SetActiveOutputConfiguration(index);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
        }

        throw new InvalidOperationException(
            "无法使用任何可用的 WASAPI 输出模式。",
            lastException);
    }

    private void SubscribeEvents()
    {
        _capture!.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += Capture_RecordingStopped;
        _output!.PlaybackStopped += Output_PlaybackStopped;
    }

    private void StartComponents()
    {
        _capture!.StartRecording();
        PlayOutputWithFallback(_resampler!);
    }

    private static OutputConfiguration[] GetOutputConfigurations(
        int latencyMilliseconds)
    {
        return latencyMilliseconds switch
        {
            <= 20 =>
            [
                new OutputConfiguration(
                    AudioClientShareMode.Exclusive,
                    5),
                new OutputConfiguration(
                    AudioClientShareMode.Shared,
                    10),
                new OutputConfiguration(
                    AudioClientShareMode.Shared,
                    StableOutputLatencyMilliseconds)
            ],
            <= 50 =>
            [
                new OutputConfiguration(
                    AudioClientShareMode.Shared,
                    10),
                new OutputConfiguration(
                    AudioClientShareMode.Shared,
                    StableOutputLatencyMilliseconds)
            ],
            _ =>
            [
                new OutputConfiguration(
                    AudioClientShareMode.Shared,
                    StableOutputLatencyMilliseconds)
            ]
        };
    }

    private static int GetPrebufferMilliseconds(int latencyMilliseconds)
    {
        return latencyMilliseconds switch
        {
            <= 20 => 5,
            <= 50 => 10,
            _ => Math.Max(
                latencyMilliseconds -
                StableOutputLatencyMilliseconds,
                MinimumPrebufferMilliseconds)
        };
    }

    private WasapiOut CreateOutput(
        IWaveProvider provider,
        OutputConfiguration configuration)
    {
        var output = new WasapiOut(
            _outputDevice!,
            configuration.ShareMode,
            true,
            configuration.LatencyMilliseconds);
        try
        {
            output.Init(provider);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void PlayOutputWithFallback(IWaveProvider provider)
    {
        Exception? lastException;
        try
        {
            _output!.Play();
            return;
        }
        catch (Exception exception)
        {
            lastException = exception;
            _output!.PlaybackStopped -= Output_PlaybackStopped;
            DisposeSafely(_output);
            _output = null;
        }

        for (var index = _activeOutputConfigurationIndex + 1;
             index < _outputConfigurations.Length;
             index++)
        {
            WasapiOut? candidate = null;
            try
            {
                candidate = CreateOutput(
                    provider,
                    _outputConfigurations[index]);
                candidate.PlaybackStopped += Output_PlaybackStopped;
                candidate.Play();
                _output = candidate;
                SetActiveOutputConfiguration(index);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                if (candidate != null)
                {
                    candidate.PlaybackStopped -= Output_PlaybackStopped;
                    DisposeSafely(candidate);
                }
            }
        }

        throw new InvalidOperationException(
            "WASAPI 输出启动失败，且所有回退模式均不可用。",
            lastException);
    }

    private void SetActiveOutputConfiguration(int index)
    {
        _activeOutputConfigurationIndex = index;
        _activeOutputLatencyMilliseconds =
            _outputConfigurations[index].LatencyMilliseconds;
        _isExclusiveOutput =
            _outputConfigurations[index].ShareMode ==
            AudioClientShareMode.Exclusive;
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
        if ((ForwarderState)Volatile.Read(ref _state) !=
            ForwarderState.Running ||
            Interlocked.Exchange(ref _failureSignaled, 1) != 0)
        {
            return;
        }

        var failure = exception ??
                      new InvalidOperationException(
                          "音频设备意外停止。");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Stop();
            ForwardingFailed?.Invoke(this, failure);
        });
    }

    private void ReleaseResources()
    {
        _activeOutputLatencyMilliseconds =
            StableOutputLatencyMilliseconds;
        _activeOutputConfigurationIndex = -1;
        _outputConfigurations = [];
        _isExclusiveOutput = false;

        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            DisposeSafely(capture);
        }

        var output = _output;
        _output = null;
        if (output != null)
        {
            output.PlaybackStopped -= Output_PlaybackStopped;
            DisposeSafely(output);
        }

        DisposeSafely(_resampler);
        _resampler = null;
        _adaptiveResampler = null;
        _prebuffer = null;
        _buffer = null;

        DisposeSafely(_sourceDevice);
        _sourceDevice = null;
        DisposeSafely(_outputDevice);
        _outputDevice = null;
        RestoreGcLatencyMode();
    }

    private void StopAndReleaseResources()
    {
        TryStopCapture();
        TryStopOutput();
        TryClearBuffer();
        ReleaseResources();
    }

    private void TryStopCapture()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // 设备失效时停止操作也可能失败，继续释放其余资源。
        }
    }

    private void TryStopOutput()
    {
        try
        {
            _output?.Stop();
        }
        catch
        {
            // 设备失效时停止操作也可能失败，继续释放其余资源。
        }
    }

    private void TryClearBuffer()
    {
        try
        {
            _buffer?.ClearBuffer();
        }
        catch
        {
            // 缓冲已随设备异常失效时无需阻止后续资源释放。
        }
    }

    private static void DisposeSafely(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch
        {
            // 清理路径必须尽量释放所有资源，不能被单个 COM 对象中断。
        }
    }

    private void EnableLowLatencyGc()
    {
        try
        {
            var currentMode = GCSettings.LatencyMode;
            if (currentMode == GCLatencyMode.NoGCRegion)
            {
                return;
            }

            _previousGcLatencyMode = currentMode;
            GCSettings.LatencyMode =
                GCLatencyMode.SustainedLowLatency;
        }
        catch
        {
            _previousGcLatencyMode = null;
        }
    }

    private void RestoreGcLatencyMode()
    {
        if (_previousGcLatencyMode == null)
        {
            return;
        }

        try
        {
            GCSettings.LatencyMode =
                _previousGcLatencyMode.Value;
        }
        catch
        {
            // GC 模式恢复失败不应影响音频资源清理。
        }
        finally
        {
            _previousGcLatencyMode = null;
        }
    }
}
