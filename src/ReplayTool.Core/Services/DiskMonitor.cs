using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>磁盘空间告警级别。</summary>
public enum DiskAlertLevel
{
    Ok,
    Warning,
    Critical,
}

/// <summary>
/// 磁盘空间监控：周期检查缓存盘与导出盘剩余空间。
/// 低于预警阈值 → Warning（提示用户）；低于 1GB → Critical（建议暂停录制保护磁盘）。
/// </summary>
public class DiskMonitor : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();

    public DiskAlertLevel Level { get; private set; } = DiskAlertLevel.Ok;

    public event EventHandler<DiskAlertLevel>? AlertChanged;
    public event EventHandler<string>? Log;

    public DiskMonitor(SettingsManager settings)
    {
        _settings = settings;
        _timer = new System.Threading.Timer(_ => Check(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    public void Check()
    {
        try
        {
            var s = _settings.Settings;
            long freeBytes = long.MaxValue;
            foreach (var dir in new[] { s.ResolveTempDir(), s.ResolveExportDir() })
            {
                var drive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(dir) ?? "");
                if (drive.IsReady)
                    freeBytes = Math.Min(freeBytes, drive.AvailableFreeSpace);
            }

            long threshold = s.DiskWarningThresholdGb * 1024L * 1024L * 1024L;
            var newLevel = freeBytes < 1024L * 1024L * 1024L ? DiskAlertLevel.Critical
                : freeBytes < threshold ? DiskAlertLevel.Warning
                : DiskAlertLevel.Ok;

            lock (_lock)
            {
                if (newLevel != Level)
                {
                    Level = newLevel;
                    AlertChanged?.Invoke(this, newLevel);
                    if (newLevel == DiskAlertLevel.Warning)
                        Log?.Invoke(this, $"磁盘空间不足（剩余 {freeBytes / 1024 / 1024 / 1024}GB），已触发预警");
                    else if (newLevel == DiskAlertLevel.Critical)
                        Log?.Invoke(this, "磁盘空间严重不足，录制将自动暂停以保护数据");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"磁盘监控异常: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
