namespace AudioRouter;

internal readonly record struct AudioBufferSettings(
    TimeSpan LowWatermark,
    TimeSpan StartWatermark,
    TimeSpan RecoveryWatermark,
    TimeSpan SoftLimit,
    TimeSpan HardLimit,
    TimeSpan Capacity)
{
    public static AudioBufferSettings CreateLowLatency(
        int latencyMilliseconds)
    {
        if (latencyMilliseconds <= 20)
        {
            return FromMilliseconds(
                low: 10,
                start: 30,
                recovery: 40,
                soft: 80,
                hard: 150,
                capacity: 250);
        }

        if (latencyMilliseconds <= 50)
        {
            return FromMilliseconds(
                low: 10,
                start: 40,
                recovery: 60,
                soft: 120,
                hard: 250,
                capacity: 500);
        }

        var start = Math.Max(40, latencyMilliseconds - 40);
        var low = Math.Max(10, start / 4);
        var recovery = start + 20;
        var soft = Math.Max(recovery + 20, start * 2);
        var hard = Math.Max(soft + 80, latencyMilliseconds * 2 + 50);
        var capacity = hard + 250;

        return FromMilliseconds(
            low,
            start,
            recovery,
            soft,
            hard,
            capacity);
    }

    public static AudioBufferSettings CreateMediaFoundationCompatibility()
    {
        // NAudio 的 MediaFoundationTransform 每次会从上游读取约 1 秒数据。
        // 兼容回退必须允许累积完整输入块；只保留少量调度余量，
        // 避免在这条高延迟兜底路径上再额外堆积数据。
        return FromMilliseconds(
            low: 50,
            start: 1000,
            recovery: 1100,
            soft: 1250,
            hard: 1500,
            capacity: 1800);
    }

    private static AudioBufferSettings FromMilliseconds(
        int low,
        int start,
        int recovery,
        int soft,
        int hard,
        int capacity)
    {
        if (!(0 < low &&
              low < start &&
              start <= recovery &&
              recovery < soft &&
              soft < hard &&
              hard < capacity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "音频缓冲水位配置无效。");
        }

        return new AudioBufferSettings(
            TimeSpan.FromMilliseconds(low),
            TimeSpan.FromMilliseconds(start),
            TimeSpan.FromMilliseconds(recovery),
            TimeSpan.FromMilliseconds(soft),
            TimeSpan.FromMilliseconds(hard),
            TimeSpan.FromMilliseconds(capacity));
    }
}
