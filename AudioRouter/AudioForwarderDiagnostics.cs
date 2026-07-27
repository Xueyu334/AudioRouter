namespace AudioRouter;

public readonly record struct AudioForwarderDiagnostics(
    bool IsRunning,
    long CaptureCallbackCount,
    long CapturedBytes,
    TimeSpan BufferedDuration,
    TimeSpan PeakBufferedDuration,
    TimeSpan EstimatedLatency,
    bool IsExclusiveOutput,
    TimeSpan TargetLatency)
{
    public long SessionId { get; init; }

    public AudioPipelineMode PipelineMode { get; init; }

    public TimeSpan CaptureBuffer { get; init; }

    public TimeSpan OutputLatency { get; init; }

    public long OutputReadCount { get; init; }

    public long OutputRequestedBytes { get; init; }

    public long OutputSourceBytes { get; init; }

    public TimeSpan ZeroFilledDuration { get; init; }

    public long UnderrunCount { get; init; }

    public long RebufferCount { get; init; }

    public TimeSpan DroppedDuration { get; init; }

    public long BufferRecoveryCount { get; init; }

    public bool IsBuffering { get; init; }

    public double ResamplingCorrectionPpm { get; init; }

    public bool IsRateRecoveryMode { get; init; }

    public TimeSpan MaximumCaptureCallbackGap { get; init; }
}
