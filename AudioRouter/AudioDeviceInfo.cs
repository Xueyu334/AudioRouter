namespace AudioRouter;

public sealed class AudioDeviceInfo
{
    public AudioDeviceInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }
}
