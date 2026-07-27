using System.Runtime.CompilerServices;

namespace AudioRouter;

internal sealed class AudioDiagnosticsService
{
    private long _captureCallbackCount;
    private long _capturedBytes;
    private long _peakBufferedDurationTicks;

    public AudioForwarderDiagnostics GetSnapshot(
        bool isRunning,
        TimeSpan bufferedDuration,
        int outputLatencyMilliseconds)
    {
        UpdatePeakBufferedDuration(bufferedDuration.Ticks);
        return new AudioForwarderDiagnostics(
            isRunning,
            Interlocked.Read(ref _captureCallbackCount),
            Interlocked.Read(ref _capturedBytes),
            bufferedDuration,
            TimeSpan.FromTicks(
                Interlocked.Read(ref _peakBufferedDurationTicks)),
            bufferedDuration +
            TimeSpan.FromMilliseconds(outputLatencyMilliseconds));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordCapture(int bytesRecorded)
    {
        Interlocked.Increment(ref _captureCallbackCount);
        if (bytesRecorded > 0)
        {
            Interlocked.Add(ref _capturedBytes, bytesRecorded);
        }
    }

    public void Reset()
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
}
