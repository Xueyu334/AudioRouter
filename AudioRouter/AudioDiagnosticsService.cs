using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AudioRouter;

internal sealed class AudioDiagnosticsService
{
    private long _captureCallbackCount;
    private long _capturedBytes;
    private long _lastCaptureTimestamp;
    private long _maximumCaptureCallbackGapTicks;

    public AudioForwarderDiagnostics GetSnapshot(
        bool isRunning,
        long sessionId,
        RealtimeWaveBufferSnapshot buffer,
        PrebufferingSnapshot prebuffer,
        OutputReadSnapshot output,
        int captureBufferMilliseconds,
        int outputLatencyMilliseconds,
        bool isExclusiveOutput,
        int targetLatencyMilliseconds,
        AudioPipelineMode pipelineMode,
        double resamplingCorrectionPpm,
        bool isRateRecoveryMode)
    {
        var pipelineQueue = buffer.BufferedDuration;
        if (pipelineMode ==
                AudioPipelineMode.MediaFoundationCompatibility &&
            pipelineQueue < TimeSpan.FromSeconds(1))
        {
            // NAudio 2.3 的 MF 转换器以约 1 秒块搬运数据。
            // 环形缓冲与 MF 内部输出缓冲是此消彼长的同一队列，
            // 因此取下限而不是再固定累加 1 秒，避免重复计算。
            pipelineQueue = TimeSpan.FromSeconds(1);
        }

        var estimatedLatency =
            pipelineQueue +
            TimeSpan.FromMilliseconds(
                captureBufferMilliseconds +
                outputLatencyMilliseconds);

        return new AudioForwarderDiagnostics(
            isRunning,
            Interlocked.Read(ref _captureCallbackCount),
            Interlocked.Read(ref _capturedBytes),
            buffer.BufferedDuration,
            buffer.PeakBufferedDuration,
            estimatedLatency,
            isExclusiveOutput,
            TimeSpan.FromMilliseconds(
                targetLatencyMilliseconds))
        {
            SessionId = sessionId,
            PipelineMode = pipelineMode,
            CaptureBuffer = TimeSpan.FromMilliseconds(
                captureBufferMilliseconds),
            OutputLatency = TimeSpan.FromMilliseconds(
                outputLatencyMilliseconds),
            OutputReadCount = output.ReadCount,
            OutputRequestedBytes = output.RequestedBytes,
            OutputSourceBytes = output.SourceBytes,
            ZeroFilledDuration = output.ZeroFilledDuration,
            UnderrunCount = prebuffer.UnderrunCount,
            RebufferCount = prebuffer.RebufferCount,
            DroppedDuration = buffer.DroppedDuration,
            BufferRecoveryCount = buffer.RecoveryCount,
            IsBuffering = prebuffer.IsBuffering,
            ResamplingCorrectionPpm =
                resamplingCorrectionPpm,
            IsRateRecoveryMode = isRateRecoveryMode,
            MaximumCaptureCallbackGap =
                StopwatchTicksToTimeSpan(
                    Interlocked.Read(
                        ref _maximumCaptureCallbackGapTicks))
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordCapture(int bytesRecorded)
    {
        Interlocked.Increment(ref _captureCallbackCount);
        if (bytesRecorded > 0)
        {
            Interlocked.Add(ref _capturedBytes, bytesRecorded);
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(
            ref _lastCaptureTimestamp,
            now);
        if (previous > 0)
        {
            UpdateMaximum(
                ref _maximumCaptureCallbackGapTicks,
                now - previous);
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _captureCallbackCount, 0);
        Interlocked.Exchange(ref _capturedBytes, 0);
        Interlocked.Exchange(ref _lastCaptureTimestamp, 0);
        Interlocked.Exchange(
            ref _maximumCaptureCallbackGapTicks,
            0);
    }

    private static void UpdateMaximum(
        ref long destination,
        long value)
    {
        var current = Interlocked.Read(ref destination);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(
                ref destination,
                value,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static TimeSpan StopwatchTicksToTimeSpan(
        long ticks)
    {
        return ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(
                ticks / (double)Stopwatch.Frequency);
    }
}
