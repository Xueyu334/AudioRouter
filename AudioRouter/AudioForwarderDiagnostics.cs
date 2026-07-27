namespace AudioRouter;

public readonly record struct AudioForwarderDiagnostics(
    bool IsRunning,
    long CaptureCallbackCount,
    long CapturedBytes,
    TimeSpan BufferedDuration,
    TimeSpan PeakBufferedDuration,
    TimeSpan EstimatedLatency,
    bool IsExclusiveOutput,
    TimeSpan TargetLatency);
