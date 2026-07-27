using NAudio.CoreAudioApi;

namespace AudioRouter;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetActiveRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render,
            DeviceState.Active);
        var result = new List<AudioDeviceInfo>(devices.Count);

        foreach (var device in devices)
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName));
            }
        }

        return result
            .OrderBy(device => device.Name)
            .ToList();
    }

    public string? GetDefaultRenderDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    public float GetMasterVolume(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        return device.AudioEndpointVolume.MasterVolumeLevelScalar;
    }

    public void SetMasterVolume(string deviceId, float volume)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        device.AudioEndpointVolume.MasterVolumeLevelScalar =
            Math.Clamp(volume, 0.0f, 1.0f);
    }

    public bool AreActive(string firstDeviceId, string secondDeviceId)
    {
        var activeDeviceIds = GetActiveRenderDevices()
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        return activeDeviceIds.Contains(firstDeviceId) &&
               activeDeviceIds.Contains(secondDeviceId);
    }
}
