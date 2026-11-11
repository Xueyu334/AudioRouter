using NAudio.CoreAudioApi;

namespace AudioRouter;

internal sealed class LowLatencyLoopbackCapture(
    MMDevice device,
    int bufferMilliseconds = LowLatencyLoopbackCapture.PreferredBufferMilliseconds)
    : WasapiCapture(device ??
                    throw new ArgumentNullException(nameof(device)),
        useEventSync: false,
        audioBufferMillisecondsLength:
        ValidateBufferMilliseconds(bufferMilliseconds))
{
    public const int PreferredBufferMilliseconds = 20;

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