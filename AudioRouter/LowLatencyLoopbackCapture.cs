using NAudio.CoreAudioApi;

namespace AudioRouter;

internal sealed class LowLatencyLoopbackCapture : WasapiCapture
{
    public const int PreferredBufferMilliseconds = 20;

    public LowLatencyLoopbackCapture(
        MMDevice device,
        int bufferMilliseconds =
            PreferredBufferMilliseconds)
        : base(
            device ??
            throw new ArgumentNullException(nameof(device)),
            useEventSync: false,
            audioBufferMillisecondsLength:
            ValidateBufferMilliseconds(bufferMilliseconds))
    {
        RequestedBufferMilliseconds = bufferMilliseconds;
    }

    public int RequestedBufferMilliseconds { get; }

    protected override AudioClientStreamFlags
        GetAudioClientStreamFlags()
    {
        return AudioClientStreamFlags.Loopback |
               base.GetAudioClientStreamFlags();
    }

    private static int ValidateBufferMilliseconds(int value)
    {
        return value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }
}
