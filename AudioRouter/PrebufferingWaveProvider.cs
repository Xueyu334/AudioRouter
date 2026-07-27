using System.Buffers;
using NAudio.Wave;

namespace AudioRouter;

public sealed class PrebufferingWaveProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _source;
    private readonly int _prebufferBytes;
    private readonly int _maximumBufferedBytes;
    private readonly int _headroomBytes;
    private readonly int _trimHysteresisBytes;
    private volatile bool _isBuffering = true;
    private int _largestReadRequestBytes;

    public PrebufferingWaveProvider(
        BufferedWaveProvider source,
        TimeSpan prebufferDuration)
    {
        _source = source;

        var bytes = source.WaveFormat.AverageBytesPerSecond *
                    prebufferDuration.TotalSeconds;
        _prebufferBytes = Math.Max(
            source.WaveFormat.BlockAlign,
            (int)bytes / source.WaveFormat.BlockAlign *
            source.WaveFormat.BlockAlign);

        var maximumDuration = prebufferDuration + TimeSpan.FromMilliseconds(20);
        _maximumBufferedBytes = Math.Max(
            _prebufferBytes,
            (int)(source.WaveFormat.AverageBytesPerSecond *
                  maximumDuration.TotalSeconds) /
            source.WaveFormat.BlockAlign *
            source.WaveFormat.BlockAlign);
        _headroomBytes =
            source.WaveFormat.AverageBytesPerSecond * 20 / 1000;
        _trimHysteresisBytes =
            source.WaveFormat.AverageBytesPerSecond * 100 / 1000;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count > Volatile.Read(ref _largestReadRequestBytes))
        {
            Volatile.Write(ref _largestReadRequestBytes, count);
        }

        if (_isBuffering)
        {
            var requiredBytes = Math.Max(_prebufferBytes, count);
            if (_source.BufferedBytes < requiredBytes)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            _isBuffering = false;
        }

        return _source.Read(buffer, offset, count);
    }

    public void TrimExcessBuffer()
    {
        if (_isBuffering)
        {
            return;
        }

        var readRequestBytes = Volatile.Read(ref _largestReadRequestBytes);
        var trimLimitBytes = Math.Max(
            _maximumBufferedBytes,
            readRequestBytes + _headroomBytes);
        var bufferedBytes = _source.BufferedBytes;
        if (bufferedBytes <= trimLimitBytes + _trimHysteresisBytes)
        {
            return;
        }

        var excessBytes = bufferedBytes - trimLimitBytes;
        excessBytes -= excessBytes % WaveFormat.BlockAlign;
        if (excessBytes <= 0)
        {
            return;
        }

        var discardBuffer = ArrayPool<byte>.Shared.Rent(excessBytes);
        try
        {
            _source.Read(discardBuffer, 0, excessBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(discardBuffer);
        }
    }
}
