using System.Runtime;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace AudioRouter;

public sealed class AudioForwarder : IDisposable
{
    private enum ForwarderState
    {
        Stopped,
        Starting,
        Running,
        StartingFailed,
        Stopping
    }

    private enum OutputFallbackStage
    {
        LowLatency,
        Stable,
        MediaFoundation
    }

    private readonly record struct OutputConfiguration(
        AudioClientShareMode ShareMode,
        int LatencyMilliseconds);

    private const int StableOutputLatencyMilliseconds = 40;
    private const int CompatibilityCaptureBufferMilliseconds = 100;
    private const int OutputHealthCheckMilliseconds = 1000;
    private const int MaximumStalledOutputChecks = 2;
    private readonly object _syncRoot = new();
    private readonly AudioDiagnosticsService _diagnostics = new();
    private MMDevice? _sourceDevice;
    private MMDevice? _outputDevice;
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private RealtimeWaveBuffer? _buffer;
    private PrebufferingWaveProvider? _prebuffer;
    private AdaptiveResamplingSampleProvider? _adaptiveResampler;
    private ReadFullyWaveProvider? _outputProvider;
    private MediaFoundationResampler? _resampler;
    private Timer? _outputHealthTimer;
    private Exception? _startupFailure;
    private int _failureSignaled;
    private int _state = (int)ForwarderState.Stopped;
    private int _activeOutputLatencyMilliseconds =
        StableOutputLatencyMilliseconds;
    private int _activeCaptureBufferMilliseconds =
        CompatibilityCaptureBufferMilliseconds;
    private int _pipelineMode = (int)AudioPipelineMode.None;
    private OutputConfiguration[] _outputConfigurations = [];
    private int _activeOutputConfigurationIndex = -1;
    private GCLatencyMode? _previousGcLatencyMode;
    private bool _isExclusiveOutput;
    private int _targetLatencyMilliseconds;
    private int _disposed;
    private int _compatibilityCaptureStartsRemaining;
    private int _nextOutputFallbackStage;
    private int _deviceAvailabilityFailureObserved;
    private long _nextSessionId;
    private long _activeSessionId;
    private long _lastHealthOutputReadCount;
    private int _stalledOutputChecks;

    public event EventHandler<Exception>? ForwardingFailed;

    public bool IsRunning =>
        (ForwarderState)Volatile.Read(ref _state) ==
        ForwarderState.Running;

    public AudioForwarderDiagnostics GetDiagnostics()
    {
        var buffer = Volatile.Read(ref _buffer);
        var prebuffer = Volatile.Read(ref _prebuffer);
        var outputProvider = Volatile.Read(ref _outputProvider);
        var adaptiveResampler =
            Volatile.Read(ref _adaptiveResampler);

        return _diagnostics.GetSnapshot(
            IsRunning,
            Volatile.Read(ref _activeSessionId),
            buffer?.GetSnapshot() ?? default,
            prebuffer?.GetSnapshot() ?? default,
            outputProvider?.GetSnapshot() ?? default,
            Volatile.Read(
                ref _activeCaptureBufferMilliseconds),
            Volatile.Read(
                ref _activeOutputLatencyMilliseconds),
            Volatile.Read(ref _isExclusiveOutput),
            Volatile.Read(ref _targetLatencyMilliseconds),
            (AudioPipelineMode)Volatile.Read(
                ref _pipelineMode),
            adaptiveResampler?.CurrentRateCorrectionPpm ??
            0,
            adaptiveResampler?.IsRecoveryMode ?? false);
    }

    public void Start(
        string sourceDeviceId,
        string outputDeviceId,
        int latencyMilliseconds)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sourceDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            outputDeviceId);
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
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if ((ForwarderState)_state !=
                ForwarderState.Stopped)
            {
                return;
            }

            var sessionId = Interlocked.Increment(
                ref _nextSessionId);
            Volatile.Write(ref _activeSessionId, sessionId);
            Volatile.Write(
                ref _state,
                (int)ForwarderState.Starting);
            Interlocked.Exchange(ref _startupFailure, null);

            try
            {
                _diagnostics.Reset();
                Interlocked.Exchange(ref _failureSignaled, 0);
                Interlocked.Exchange(
                    ref _deviceAvailabilityFailureObserved,
                    0);
                Volatile.Write(
                    ref _targetLatencyMilliseconds,
                    latencyMilliseconds);
                EnableLowLatencyGc();
                LoadDevices(sourceDeviceId, outputDeviceId);
                InitializeCapture();
                InitializeLowLatencyProcessingPipeline(
                    latencyMilliseconds);
                InitializeOutputWithFormatFallback();
                SubscribeEvents();
                StartComponents();

                var previousState =
                    (ForwarderState)Interlocked.CompareExchange(
                        ref _state,
                        (int)ForwarderState.Running,
                        (int)ForwarderState.Starting);
                if (previousState != ForwarderState.Starting)
                {
                    throw Volatile.Read(ref _startupFailure) ??
                          new InvalidOperationException(
                              "音频组件在启动期间意外停止。");
                }

                StartOutputHealthMonitor(sessionId);
            }
            catch
            {
                Volatile.Write(ref _activeSessionId, 0);
                Interlocked.Exchange(ref _failureSignaled, 1);
                Volatile.Write(
                    ref _state,
                    (int)ForwarderState.Stopping);
                StopAndReleaseResources();
                Volatile.Write(
                    ref _state,
                    (int)ForwarderState.Stopped);
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            var state = (ForwarderState)_state;
            if (state is ForwarderState.Stopped or
                ForwarderState.Stopping)
            {
                return;
            }

            Volatile.Write(ref _activeSessionId, 0);
            Volatile.Write(
                ref _state,
                (int)ForwarderState.Stopping);
            try
            {
                StopAndReleaseResources();
            }
            finally
            {
                Volatile.Write(
                    ref _state,
                    (int)ForwarderState.Stopped);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
    }

    private void Capture_DataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        var sessionId = Volatile.Read(
            ref _activeSessionId);
        var capture = Volatile.Read(ref _capture);
        var buffer = Volatile.Read(ref _buffer);
        if (sessionId == 0 ||
            buffer == null ||
            !ReferenceEquals(sender, capture))
        {
            return;
        }

        if (!AudioThreadPriority.IsCurrentThreadPromoted)
        {
            AudioThreadPriority.PromoteCurrentThread();
        }

        if (Volatile.Read(ref _activeSessionId) != sessionId ||
            !ReferenceEquals(
                capture,
                Volatile.Read(ref _capture)))
        {
            return;
        }

        _diagnostics.RecordCapture(e.BytesRecorded);
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        buffer.AddSamples(
            e.Buffer,
            0,
            e.BytesRecorded);
    }

    private void LoadDevices(
        string sourceDeviceId,
        string outputDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        _sourceDevice = enumerator.GetDevice(sourceDeviceId);
        _outputDevice = enumerator.GetDevice(outputDeviceId);
    }

    private void InitializeCapture()
    {
        var useCompatibilityCapture =
            Interlocked.Exchange(
                ref _compatibilityCaptureStartsRemaining,
                0) > 0;
        if (useCompatibilityCapture)
        {
            _capture = new WasapiLoopbackCapture(
                _sourceDevice!);
            Volatile.Write(
                ref _activeCaptureBufferMilliseconds,
                CompatibilityCaptureBufferMilliseconds);
            return;
        }

        _capture = new LowLatencyLoopbackCapture(
            _sourceDevice!);
        Volatile.Write(
            ref _activeCaptureBufferMilliseconds,
            LowLatencyLoopbackCapture
                .PreferredBufferMilliseconds);
    }

    private void InitializeLowLatencyProcessingPipeline(
        int latencyMilliseconds)
    {
        var fallbackStage =
            (OutputFallbackStage)Interlocked.Exchange(
                ref _nextOutputFallbackStage,
                (int)OutputFallbackStage.LowLatency);
        if (fallbackStage ==
            OutputFallbackStage.MediaFoundation)
        {
            InitializeMediaFoundationFallback();
            return;
        }

        InitializeProcessingPipeline(
            AudioBufferSettings.CreateLowLatency(
                latencyMilliseconds));
        _outputConfigurations =
            GetDirectOutputConfigurations(fallbackStage);
        Volatile.Write(
            ref _pipelineMode,
            (int)AudioPipelineMode.DirectWasapi);
    }

    private void InitializeProcessingPipeline(
        AudioBufferSettings settings)
    {
        _buffer = new RealtimeWaveBuffer(
            _capture!.WaveFormat,
            settings);
        var maximumPrebufferWatermark = settings.HardLimit;
        var captureBufferMilliseconds = Volatile.Read(
            ref _activeCaptureBufferMilliseconds);
        if (captureBufferMilliseconds >
            LowLatencyLoopbackCapture
                .PreferredBufferMilliseconds)
        {
            var compatibilityHardLimit =
                TimeSpan.FromMilliseconds(
                    captureBufferMilliseconds * 2 + 20);
            _buffer.RaiseHardLimit(
                compatibilityHardLimit);
            if (compatibilityHardLimit >
                maximumPrebufferWatermark)
            {
                maximumPrebufferWatermark =
                    compatibilityHardLimit;
            }
        }

        _prebuffer = new PrebufferingWaveProvider(
            _buffer,
            settings.LowWatermark,
            settings.StartWatermark,
            settings.RecoveryWatermark,
            maximumPrebufferWatermark);
        var resamplerTarget = settings.StartWatermark;
        var resamplerSoftLimit = settings.SoftLimit;
        if (captureBufferMilliseconds >
            LowLatencyLoopbackCapture
                .PreferredBufferMilliseconds)
        {
            var compatibilityTarget =
                TimeSpan.FromMilliseconds(
                    captureBufferMilliseconds);
            var compatibilitySoftLimit =
                TimeSpan.FromMilliseconds(
                    captureBufferMilliseconds * 2);
            _prebuffer.RaiseWatermarks(
                compatibilityTarget,
                TimeSpan.FromMilliseconds(
                    captureBufferMilliseconds + 20));
            if (compatibilityTarget > resamplerTarget)
            {
                resamplerTarget = compatibilityTarget;
            }

            if (compatibilitySoftLimit > resamplerSoftLimit)
            {
                resamplerSoftLimit = compatibilitySoftLimit;
            }
        }

        _adaptiveResampler =
            new AdaptiveResamplingSampleProvider(
                _prebuffer.ToSampleProvider(),
                _outputDevice!.AudioClient
                    .MixFormat.SampleRate,
                () => _buffer?.BufferedDuration ??
                      TimeSpan.Zero,
                resamplerTarget,
                resamplerSoftLimit);
        _outputProvider = new ReadFullyWaveProvider(
            _adaptiveResampler.ToWaveProvider());
    }

    private void InitializeOutputWithFormatFallback()
    {
        if ((AudioPipelineMode)Volatile.Read(
                ref _pipelineMode) ==
            AudioPipelineMode.MediaFoundationCompatibility)
        {
            InitializeOutput(_outputProvider!);
            return;
        }

        try
        {
            InitializeOutput(_outputProvider!);
        }
        catch (Exception directException)
        {
            try
            {
                InitializeMediaFoundationFallback();
                InitializeOutput(_outputProvider!);
            }
            catch (Exception fallbackException)
            {
                throw new InvalidOperationException(
                    "Direct WASAPI 与 Media Foundation " +
                    "兼容输出均初始化失败。",
                    new AggregateException(
                        directException,
                        fallbackException));
            }
        }
    }

    private void InitializeMediaFoundationFallback()
    {
        DisposeSafely(_resampler);
        _resampler = null;

        InitializeProcessingPipeline(
            AudioBufferSettings
                .CreateMediaFoundationCompatibility());
        _resampler = new MediaFoundationResampler(
            _adaptiveResampler!.ToWaveProvider(),
            _outputDevice!.AudioClient.MixFormat)
        {
            ResamplerQuality = 60
        };
        _outputProvider = new ReadFullyWaveProvider(
            _resampler);
        _outputConfigurations =
        [
            new OutputConfiguration(
                AudioClientShareMode.Shared,
                StableOutputLatencyMilliseconds)
        ];
        Volatile.Write(
            ref _pipelineMode,
            (int)AudioPipelineMode
                .MediaFoundationCompatibility);
    }

    private void InitializeOutput(IWaveProvider provider)
    {
        Exception? lastException = null;
        for (var index = 0;
             index < _outputConfigurations.Length;
             index++)
        {
            try
            {
                _output = CreateOutput(
                    provider,
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
        _capture.RecordingStopped +=
            Capture_RecordingStopped;
        _output!.PlaybackStopped +=
            Output_PlaybackStopped;
    }

    private void StartComponents()
    {
        PlayOutputWithFallback();
        StartCaptureWithFallback();
    }

    private OutputConfiguration[]
        GetDirectOutputConfigurations(
            OutputFallbackStage fallbackStage)
    {
        if (fallbackStage == OutputFallbackStage.Stable)
        {
            return
            [
                new OutputConfiguration(
                    AudioClientShareMode.Shared,
                    StableOutputLatencyMilliseconds)
            ];
        }

        return
        [
            new OutputConfiguration(
                AudioClientShareMode.Shared,
                10),
            new OutputConfiguration(
                AudioClientShareMode.Shared,
                StableOutputLatencyMilliseconds)
        ];
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

    private void PlayOutputWithFallback()
    {
        var failures = new List<Exception>();
        try
        {
            _output!.Play();
            return;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            ReleaseCurrentOutput();
        }

        for (var index =
                 _activeOutputConfigurationIndex + 1;
             index < _outputConfigurations.Length;
             index++)
        {
            WasapiOut? candidate = null;
            try
            {
                candidate = CreateOutput(
                    _outputProvider!,
                    _outputConfigurations[index]);
                candidate.PlaybackStopped +=
                    Output_PlaybackStopped;
                Volatile.Write(ref _output, candidate);
                SetActiveOutputConfiguration(index);
                candidate.Play();
                return;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                if (candidate != null)
                {
                    Interlocked.CompareExchange(
                        ref _output,
                        null,
                        candidate);
                    candidate.PlaybackStopped -=
                        Output_PlaybackStopped;
                    DisposeSafely(candidate);
                }
            }
        }

        if ((AudioPipelineMode)Volatile.Read(
                ref _pipelineMode) ==
            AudioPipelineMode.DirectWasapi)
        {
            try
            {
                InitializeMediaFoundationFallback();
                InitializeOutput(_outputProvider!);
                _output!.PlaybackStopped +=
                    Output_PlaybackStopped;
                _output.Play();
                return;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                ReleaseCurrentOutput();
            }
        }

        throw new InvalidOperationException(
            "WASAPI 输出启动失败，且所有回退模式均不可用。",
            new AggregateException(failures));
    }

    private void StartCaptureWithFallback()
    {
        try
        {
            _capture!.StartRecording();
            return;
        }
        catch (Exception lowLatencyException)
        {
            if (Volatile.Read(
                    ref _activeCaptureBufferMilliseconds) >=
                CompatibilityCaptureBufferMilliseconds)
            {
                throw;
            }

            try
            {
                ReplaceCaptureWithCompatibilityMode();
                _capture!.StartRecording();
            }
            catch (Exception compatibilityException)
            {
                throw new InvalidOperationException(
                    "低延迟与兼容 Loopback 捕获均启动失败。",
                    new AggregateException(
                        lowLatencyException,
                        compatibilityException));
            }
        }
    }

    private void ReplaceCaptureWithCompatibilityMode()
    {
        var oldCapture = _capture;
        _capture = null;
        if (oldCapture != null)
        {
            oldCapture.DataAvailable -=
                Capture_DataAvailable;
            oldCapture.RecordingStopped -=
                Capture_RecordingStopped;
            DisposeSafely(oldCapture);
        }

        var compatibilityCapture =
            new WasapiLoopbackCapture(_sourceDevice!);
        if (!AreWaveFormatsCompatible(
                compatibilityCapture.WaveFormat,
                _buffer!.WaveFormat))
        {
            compatibilityCapture.Dispose();
            throw new InvalidOperationException(
                "兼容捕获模式的音频格式发生变化，" +
                "无法安全复用当前输出管线。");
        }

        _buffer.ClearBuffer();
        _buffer.RaiseHardLimit(
            TimeSpan.FromMilliseconds(
                CompatibilityCaptureBufferMilliseconds * 2 + 20));
        _prebuffer!.RaiseWatermarks(
            TimeSpan.FromMilliseconds(
                CompatibilityCaptureBufferMilliseconds),
            TimeSpan.FromMilliseconds(
                CompatibilityCaptureBufferMilliseconds + 20));
        _adaptiveResampler!.RaiseBufferTarget(
            TimeSpan.FromMilliseconds(
                CompatibilityCaptureBufferMilliseconds),
            TimeSpan.FromMilliseconds(
                CompatibilityCaptureBufferMilliseconds * 2));
        _capture = compatibilityCapture;
        Volatile.Write(
            ref _activeCaptureBufferMilliseconds,
            CompatibilityCaptureBufferMilliseconds);
        compatibilityCapture.DataAvailable +=
            Capture_DataAvailable;
        compatibilityCapture.RecordingStopped +=
            Capture_RecordingStopped;
    }

    private static bool AreWaveFormatsCompatible(
        WaveFormat left,
        WaveFormat right)
    {
        return left.SampleRate == right.SampleRate &&
               left.Channels == right.Channels &&
               left.BitsPerSample == right.BitsPerSample &&
               left.BlockAlign == right.BlockAlign;
    }

    private void SetActiveOutputConfiguration(int index)
    {
        _activeOutputConfigurationIndex = index;
        Volatile.Write(
            ref _activeOutputLatencyMilliseconds,
            _outputConfigurations[index]
                .LatencyMilliseconds);
        Volatile.Write(
            ref _isExclusiveOutput,
            _outputConfigurations[index].ShareMode ==
            AudioClientShareMode.Exclusive);
    }

    private void Capture_RecordingStopped(
        object? sender,
        StoppedEventArgs e)
    {
        if (!ReferenceEquals(
                sender,
                Volatile.Read(ref _capture)))
        {
            return;
        }

        var state = (ForwarderState)Volatile.Read(
            ref _state);
        if (state is (ForwarderState.Starting or
                ForwarderState.Running) &&
            Volatile.Read(
                ref _activeCaptureBufferMilliseconds) <
            CompatibilityCaptureBufferMilliseconds)
        {
            var isDeviceAvailabilityFailure =
                IsDeviceAvailabilityFailure(e.Exception);
            if (isDeviceAvailabilityFailure)
            {
                Interlocked.Exchange(
                    ref _deviceAvailabilityFailureObserved,
                    1);
                Interlocked.Exchange(
                    ref _nextOutputFallbackStage,
                    (int)OutputFallbackStage.LowLatency);
            }

            Interlocked.Exchange(
                ref _compatibilityCaptureStartsRemaining,
                !isDeviceAvailabilityFailure &&
                Volatile.Read(
                    ref _deviceAvailabilityFailureObserved) == 0
                    ? 1
                    : 0);
        }

        HandleUnexpectedStop(e.Exception);
    }

    private void Output_PlaybackStopped(
        object? sender,
        StoppedEventArgs e)
    {
        if (!ReferenceEquals(
                sender,
                Volatile.Read(ref _output)))
        {
            return;
        }

        var state = (ForwarderState)Volatile.Read(
            ref _state);
        if (state is ForwarderState.Starting or
            ForwarderState.Running)
        {
            AdvanceOutputFallback(e.Exception);
        }

        HandleUnexpectedStop(e.Exception);
    }

    private void HandleUnexpectedStop(
        Exception? exception,
        long expectedSessionId = 0)
    {
        if (expectedSessionId != 0 &&
            Volatile.Read(ref _activeSessionId) !=
            expectedSessionId)
        {
            return;
        }

        var failure = exception ??
                      new InvalidOperationException(
                          "音频设备意外停止。");

        while (true)
        {
            if (expectedSessionId != 0 &&
                Volatile.Read(ref _activeSessionId) !=
                expectedSessionId)
            {
                return;
            }

            var state = (ForwarderState)Volatile.Read(
                ref _state);
            if (state == ForwarderState.Starting)
            {
                Interlocked.CompareExchange(
                    ref _startupFailure,
                    failure,
                    null);
                if ((ForwarderState)
                    Interlocked.CompareExchange(
                        ref _state,
                        (int)ForwarderState.StartingFailed,
                        (int)ForwarderState.Starting) ==
                    ForwarderState.Starting)
                {
                    return;
                }

                continue;
            }

            if (state != ForwarderState.Running)
            {
                return;
            }

            var sessionId = Volatile.Read(
                ref _activeSessionId);
            if (sessionId == 0 ||
                expectedSessionId != 0 &&
                sessionId != expectedSessionId ||
                Interlocked.Exchange(
                    ref _failureSignaled,
                    1) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
                StopFailedSession(sessionId, failure));
            return;
        }
    }

    private void StopFailedSession(
        long sessionId,
        Exception failure)
    {
        lock (_syncRoot)
        {
            if (Volatile.Read(ref _activeSessionId) !=
                sessionId)
            {
                return;
            }

            Volatile.Write(ref _activeSessionId, 0);
            Volatile.Write(
                ref _state,
                (int)ForwarderState.Stopping);
            try
            {
                StopAndReleaseResources();
            }
            finally
            {
                Volatile.Write(
                    ref _state,
                    (int)ForwarderState.Stopped);
            }
        }

        RaiseForwardingFailed(failure);
    }

    private void RaiseForwardingFailed(Exception failure)
    {
        var handlers = ForwardingFailed;
        if (handlers == null)
        {
            return;
        }

        foreach (var callback in
                 handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<Exception>)callback)(
                    this,
                    failure);
            }
            catch
            {
                // 订阅者异常不能终止后台音频恢复流程。
            }
        }
    }

    private void StartOutputHealthMonitor(long sessionId)
    {
        var outputProvider = Volatile.Read(
            ref _outputProvider);
        Interlocked.Exchange(
            ref _lastHealthOutputReadCount,
            outputProvider?.GetSnapshot().ReadCount ?? 0);
        Interlocked.Exchange(ref _stalledOutputChecks, 0);

        var timer = new Timer(
            _ => CheckOutputHealth(sessionId),
            null,
            OutputHealthCheckMilliseconds,
            OutputHealthCheckMilliseconds);
        Interlocked.Exchange(
                ref _outputHealthTimer,
                timer)
            ?.Dispose();
    }

    private void CheckOutputHealth(long sessionId)
    {
        if (Volatile.Read(ref _activeSessionId) !=
                sessionId ||
            (ForwarderState)Volatile.Read(ref _state) !=
                ForwarderState.Running)
        {
            return;
        }

        var outputProvider = Volatile.Read(
            ref _outputProvider);
        if (outputProvider == null)
        {
            HandleUnexpectedStop(
                new InvalidOperationException(
                    "音频输出管线已丢失。"),
                sessionId);
            return;
        }

        var readCount =
            outputProvider.GetSnapshot().ReadCount;
        var previous = Interlocked.Exchange(
            ref _lastHealthOutputReadCount,
            readCount);
        if (readCount != previous)
        {
            Interlocked.Exchange(
                ref _stalledOutputChecks,
                0);
            return;
        }

        if (Interlocked.Increment(
                ref _stalledOutputChecks) <
            MaximumStalledOutputChecks)
        {
            return;
        }

        var failure = new InvalidOperationException(
            "音频输出线程长时间没有读取数据。");
        lock (_syncRoot)
        {
            if (Volatile.Read(ref _activeSessionId) !=
                    sessionId ||
                (ForwarderState)Volatile.Read(ref _state) !=
                    ForwarderState.Running)
            {
                return;
            }

            AdvanceOutputFallback(failure);
        }

        HandleUnexpectedStop(failure, sessionId);
    }

    private void AdvanceOutputFallback(Exception? failure)
    {
        if (IsDeviceAvailabilityFailure(failure))
        {
            Interlocked.Exchange(
                ref _deviceAvailabilityFailureObserved,
                1);
        }

        if (Volatile.Read(
                ref _deviceAvailabilityFailureObserved) != 0)
        {
            Interlocked.Exchange(
                ref _nextOutputFallbackStage,
                (int)OutputFallbackStage.LowLatency);
            Interlocked.Exchange(
                ref _compatibilityCaptureStartsRemaining,
                0);
            return;
        }

        var nextStage =
            (AudioPipelineMode)Volatile.Read(
                ref _pipelineMode) switch
            {
                AudioPipelineMode.DirectWasapi
                    when Volatile.Read(
                        ref _activeOutputLatencyMilliseconds) <= 10 =>
                    OutputFallbackStage.Stable,
                AudioPipelineMode.DirectWasapi =>
                    OutputFallbackStage.MediaFoundation,
                _ => OutputFallbackStage.LowLatency
            };

        if (nextStage == OutputFallbackStage.LowLatency)
        {
            Interlocked.Exchange(
                ref _nextOutputFallbackStage,
                (int)nextStage);
            return;
        }

        SetMaximum(
            ref _nextOutputFallbackStage,
            (int)nextStage);
    }

    private static bool IsDeviceAvailabilityFailure(
        Exception? exception)
    {
        if (exception == null)
        {
            return false;
        }

        var hResult = exception.HResult;
        if (hResult ==
                (int)AudioClientErrorCode.DeviceInvalidated ||
            hResult ==
                (int)AudioClientErrorCode.ResourcesInvalidated ||
            hResult ==
                (int)AudioClientErrorCode.EndpointCreateFailed ||
            hResult ==
                (int)AudioClientErrorCode.ServiceNotRunning)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (IsDeviceAvailabilityFailure(inner))
                {
                    return true;
                }
            }

            return false;
        }

        return IsDeviceAvailabilityFailure(
            exception.InnerException);
    }

    private static void SetMaximum(
        ref int target,
        int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(
                ref target,
                value,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private void ReleaseResources()
    {
        Interlocked.Exchange(
                ref _outputHealthTimer,
                null)
            ?.Dispose();

        Volatile.Write(
            ref _activeOutputLatencyMilliseconds,
            StableOutputLatencyMilliseconds);
        Volatile.Write(
            ref _activeCaptureBufferMilliseconds,
            CompatibilityCaptureBufferMilliseconds);
        _activeOutputConfigurationIndex = -1;
        _outputConfigurations = [];
        Volatile.Write(ref _isExclusiveOutput, false);
        Volatile.Write(ref _targetLatencyMilliseconds, 0);
        Volatile.Write(
            ref _pipelineMode,
            (int)AudioPipelineMode.None);
        Interlocked.Exchange(ref _startupFailure, null);

        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -=
                Capture_RecordingStopped;
            DisposeSafely(capture);
        }

        ReleaseCurrentOutput();

        DisposeSafely(_resampler);
        _resampler = null;
        _outputProvider = null;
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

    private void ReleaseCurrentOutput()
    {
        var output = _output;
        _output = null;
        if (output == null)
        {
            return;
        }

        output.PlaybackStopped -= Output_PlaybackStopped;
        DisposeSafely(output);
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
