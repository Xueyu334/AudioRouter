using NAudio.Wave;

namespace AudioRouter;

public sealed class PrebufferingWaveProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _source;
    private readonly int _prebufferBytes;
    private bool _isBuffering = true;

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
        if (_isBuffering)
        {
            var requiredBytes = Math.Max(_prebufferBytes, count * 2);
            if (_source.BufferedBytes < requiredBytes)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            _isBuffering = false;
        }

        if (_source.BufferedBytes < count)
        {
            _isBuffering = true;
            Array.Clear(buffer, offset, count);
            return count;
        }

        return _source.Read(buffer, offset, count);
    }
}
