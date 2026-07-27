using NAudio.Wave;

namespace AudioRouter;

internal readonly record struct PrebufferingSnapshot(
    bool IsBuffering,
    long UnderrunCount,
    long RebufferCount,
    long MissingBytes,
    int LargestReadRequestBytes);

internal sealed class PrebufferingWaveProvider : IWaveProvider
{
    private const int BufferingState = 1;
    private const int PlayingState = 0;
    private readonly RealtimeWaveBuffer _source;
    private readonly int _lowWatermarkBytes;
    private readonly int _maximumWatermarkBytes;
    private int _startWatermarkBytes;
    private int _recoveryWatermarkBytes;
    private int _state = BufferingState;
    private int _hasStarted;
    private int _largestReadRequestBytes;
    private long _underrunCount;
    private long _rebufferCount;
    private long _missingBytes;

    public PrebufferingWaveProvider(
        RealtimeWaveBuffer source,
        TimeSpan lowWatermark,
        TimeSpan startWatermark,
        TimeSpan recoveryWatermark,
        TimeSpan maximumWatermark)
    {
        _source = source ??
                  throw new ArgumentNullException(nameof(source));
        _lowWatermarkBytes = DurationToBytes(lowWatermark);
        _startWatermarkBytes = DurationToBytes(startWatermark);
        _recoveryWatermarkBytes =
            DurationToBytes(recoveryWatermark);
        _maximumWatermarkBytes =
            DurationToBytes(maximumWatermark);
        if (!(_lowWatermarkBytes < _startWatermarkBytes &&
              _startWatermarkBytes <= _recoveryWatermarkBytes &&
              _recoveryWatermarkBytes <
              _maximumWatermarkBytes))
        {
            throw new ArgumentException(
                "预缓冲水位配置无效。",
                nameof(lowWatermark));
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        TrackLargestReadRequest(count);

        if (Volatile.Read(ref _state) == BufferingState)
        {
            var baseWatermark =
                Volatile.Read(ref _hasStarted) == 0
                    ? Volatile.Read(ref _startWatermarkBytes)
                    : Volatile.Read(
                        ref _recoveryWatermarkBytes);
            var requestWatermark = SaturatingAdd(
                count,
                _lowWatermarkBytes);
            var requiredBytes = Math.Min(
                Math.Max(baseWatermark, requestWatermark),
                _maximumWatermarkBytes);
            if (_source.BufferedBytes < requiredBytes)
            {
                return 0;
            }

            Volatile.Write(ref _state, PlayingState);
            Volatile.Write(ref _hasStarted, 1);
        }

        var availableBytes = _source.BufferedBytes;
        if (availableBytes < count &&
            availableBytes <= _lowWatermarkBytes)
        {
            RecordUnderrun(count);
            EnterBuffering();
            return 0;
        }

        var bytesRead = _source.Read(buffer, offset, count);
        if (bytesRead < count)
        {
            RecordUnderrun(count - bytesRead);
            EnterBuffering();
        }

        return bytesRead;
    }

    public void RaiseWatermarks(
        TimeSpan startWatermark,
        TimeSpan recoveryWatermark)
    {
        var startBytes = Math.Min(
            DurationToBytes(startWatermark),
            _maximumWatermarkBytes - _source.WaveFormat.BlockAlign);
        var recoveryBytes = Math.Min(
            DurationToBytes(recoveryWatermark),
            _maximumWatermarkBytes - _source.WaveFormat.BlockAlign);
        recoveryBytes = Math.Max(startBytes, recoveryBytes);

        SetMaximum(ref _startWatermarkBytes, startBytes);
        SetMaximum(
            ref _recoveryWatermarkBytes,
            recoveryBytes);
    }

    public PrebufferingSnapshot GetSnapshot()
    {
        return new PrebufferingSnapshot(
            Volatile.Read(ref _state) == BufferingState,
            Interlocked.Read(ref _underrunCount),
            Interlocked.Read(ref _rebufferCount),
            Interlocked.Read(ref _missingBytes),
            Volatile.Read(ref _largestReadRequestBytes));
    }

    private void EnterBuffering()
    {
        if (Interlocked.Exchange(
                ref _state,
                BufferingState) == BufferingState)
        {
            return;
        }

        if (Volatile.Read(ref _hasStarted) != 0)
        {
            Interlocked.Increment(ref _rebufferCount);
        }
    }

    private void RecordUnderrun(int missingBytes)
    {
        if (Volatile.Read(ref _hasStarted) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _underrunCount);
        Interlocked.Add(ref _missingBytes, missingBytes);
    }

    private void TrackLargestReadRequest(int count)
    {
        var current = Volatile.Read(
            ref _largestReadRequestBytes);
        while (count > current)
        {
            var observed = Interlocked.CompareExchange(
                ref _largestReadRequestBytes,
                count,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static int SaturatingAdd(int left, int right)
    {
        return left > int.MaxValue - right
            ? int.MaxValue
            : left + right;
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

    private int DurationToBytes(TimeSpan duration)
    {
        var blockAlign = Math.Max(1, WaveFormat.BlockAlign);
        var bytes = checked((long)Math.Ceiling(
            WaveFormat.AverageBytesPerSecond *
            duration.TotalSeconds));
        return checked((int)(
            (bytes + blockAlign - 1) /
            blockAlign *
            blockAlign));
    }
}
