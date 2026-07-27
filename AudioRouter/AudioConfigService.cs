using System.IO;
using System.Text.Json;
using Serilog;

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
                Log.Debug(
                    "配置文件不存在，使用默认配置：{ConfigPath}",
                    _configPath);
                return new AudioRouterConfig();
            }

            var json = File.ReadAllText(_configPath);
            var config =
                JsonSerializer.Deserialize<AudioRouterConfig>(
                    json,
                    SerializerOptions) ??
                new AudioRouterConfig();
            Log.Debug(
                "已加载音频配置：延迟 {LatencyMilliseconds} ms",
                config.LatencyMilliseconds);
            return config;
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "加载配置失败，使用默认配置：{ConfigPath}",
                _configPath);
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
            Log.Debug(
                "已保存音频配置：延迟 {LatencyMilliseconds} ms",
                config.LatencyMilliseconds);
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "保存配置失败：{ConfigPath}",
                _configPath);
            // 配置保存失败不应中断音频转发。
        }
    }
}

public sealed record AudioRouterConfig(
    string? SourceDeviceId = null,
    string? OutputDeviceId = null,
    int LatencyMilliseconds = 50);
