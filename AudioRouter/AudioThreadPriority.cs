using System.Runtime.InteropServices;

namespace AudioRouter;

internal static class AudioThreadPriority
{
    private enum AvrtPriority
    {
        High = 1
    }

    [ThreadStatic]
    private static bool _isPromoted;

    [ThreadStatic]
    private static nint _mmcssHandle;

    public static bool IsCurrentThreadPromoted => _isPromoted;

    public static void PromoteCurrentThread()
    {
        if (_isPromoted)
        {
            return;
        }

        try
        {
            _mmcssHandle = AvSetMmThreadCharacteristics(
                "Pro Audio",
                out _);
            if (_mmcssHandle != 0)
            {
                AvSetMmThreadPriority(
                    _mmcssHandle,
                    AvrtPriority.High);
            }
            else
            {
                Thread.CurrentThread.Priority =
                    ThreadPriority.AboveNormal;
            }
        }
        catch
        {
            // 某些受限运行环境不允许修改线程优先级，保持默认即可。
        }

        _isPromoted = true;
    }

    [DllImport(
        "avrt.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern nint AvSetMmThreadCharacteristics(
        string taskName,
        out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(
        nint avrtHandle,
        AvrtPriority priority);
}
