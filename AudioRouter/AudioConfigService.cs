using System.IO;
using System.Text.Json;

namespace AudioRouter;

public sealed class AudioConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true
        };

    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "AudioRouter",
        "settings.json");

    public AudioRouterConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new AudioRouterConfig();
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AudioRouterConfig>(
                       json,
                       SerializerOptions) ??
                   new AudioRouterConfig();
        }
        catch
        {
            return new AudioRouterConfig();
        }
    }

    public void Save(AudioRouterConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _configPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(config, SerializerOptions));
            File.Move(temporaryPath, _configPath, true);
        }
        catch
        {
            // 配置保存失败不应中断音频转发。
        }
    }
}

public sealed record AudioRouterConfig(
    string? SourceDeviceId = null,
    string? OutputDeviceId = null,
    int LatencyMilliseconds = 50);
