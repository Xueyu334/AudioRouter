namespace AudioRouter;

internal static class AudioThreadPriority
{
    [ThreadStatic]
    private static bool _isPromoted;

    public static void PromoteCurrentThread()
    {
        if (_isPromoted)
        {
            return;
        }

        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch
        {
            // 某些受限运行环境不允许修改线程优先级，保持默认即可。
        }

        _isPromoted = true;
    }
}
