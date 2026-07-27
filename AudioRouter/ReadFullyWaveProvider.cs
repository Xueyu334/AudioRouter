using System.Diagnostics;
using NAudio.Wave;

namespace AudioRouter;

internal readonly record struct OutputReadSnapshot(
    long ReadCount,
    long RequestedBytes,
    long SourceBytes,
    TimeSpan ZeroFilledDuration,
    int LargestReadRequestBytes,
    long LastReadTimestamp);

internal sealed class ReadFullyWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private long _readCount;
    private long _requestedBytes;
    private long _sourceBytes;
    private long _zeroFilledBytes;
    private long _lastReadTimestamp;
    private int _largestReadRequestBytes;

    public ReadFullyWaveProvider(IWaveProvider source)
    {
        _source = source ??
                  throw new ArgumentNullException(nameof(source));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = Math.Clamp(
            _source.Read(buffer, offset, count),
            0,
            count);
        var missingBytes = count - bytesRead;
        if (missingBytes > 0)
        {
            Array.Clear(
                buffer,
                offset + bytesRead,
                missingBytes);
            Interlocked.Add(
                ref _zeroFilledBytes,
                missingBytes);
        }

        Interlocked.Increment(ref _readCount);
        Interlocked.Add(ref _requestedBytes, count);
        Interlocked.Add(ref _sourceBytes, bytesRead);
        Interlocked.Exchange(
            ref _lastReadTimestamp,
            Stopwatch.GetTimestamp());
        TrackLargestReadRequest(count);
        return count;
    }

    public OutputReadSnapshot GetSnapshot()
    {
        return new OutputReadSnapshot(
            Interlocked.Read(ref _readCount),
            Interlocked.Read(ref _requestedBytes),
            Interlocked.Read(ref _sourceBytes),
            BytesToDuration(
                Interlocked.Read(ref _zeroFilledBytes)),
            Volatile.Read(ref _largestReadRequestBytes),
            Interlocked.Read(ref _lastReadTimestamp));
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

    private TimeSpan BytesToDuration(long bytes)
    {
        return TimeSpan.FromSeconds(
            bytes /
            (double)WaveFormat.AverageBytesPerSecond);
    }
}
