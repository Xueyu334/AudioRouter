using NAudio.Wave;

namespace AudioRouter;

public sealed class PrebufferingWaveProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _source;
    private readonly int _prebufferBytes;
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
}
