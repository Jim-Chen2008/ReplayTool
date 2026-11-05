using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>
/// 设置持久化管理器。读写 %APPDATA%\ReplayTool\settings.json。
/// </summary>
public class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath;
    private readonly object _lock = new();

    public AppSettings Settings { get; private set; }

    public SettingsManager(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReplayTool");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            // 设置文件损坏时回退默认值，保留损坏文件便于排查
            System.Diagnostics.Debug.WriteLine($"Settings load failed: {ex.Message}");
            try { File.Copy(_settingsPath, _settingsPath + ".corrupt", overwrite: true); }
            catch { /* ignore */ }
        }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
    }

    public void Save(AppSettings settings)
    {
        Settings = settings;
        Save();
    }

    /// <summary>读取配置目录路径（供日志/缓存存放）。</summary>
    public string ConfigDir => Path.GetDirectoryName(_settingsPath) ?? "";
}
