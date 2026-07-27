using NAudio.Wave;

namespace AudioRouter;

internal sealed class ReadFullyWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _source;

    public ReadFullyWaveProvider(IWaveProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _source.Read(buffer, offset, count);
        if (bytesRead < count)
        {
            Array.Clear(
                buffer,
                offset + bytesRead,
                count - bytesRead);
        }

        return count;
    }
}
