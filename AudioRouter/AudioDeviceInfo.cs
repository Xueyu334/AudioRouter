namespace AudioRouter;

public sealed class AudioDeviceInfo
{
    /// <summary>
    /// 表示一个音频设备，包括其唯一标识符和显示名称。
    /// </summary>
    public AudioDeviceInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }
}