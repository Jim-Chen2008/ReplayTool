using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>
/// 崩溃恢复服务：应用异常退出后，将临时目录中遗留的最近片段导出为可播放视频，
/// 避免「精彩瞬间已录但未保存」的损失。恢复文件放入 {导出目录}/Recovered。
/// </summary>
public class RecoveryService
{
    private readonly EncoderService _encoders;
    private readonly SettingsManager _settings;

    public event EventHandler<string>? Log;

    public RecoveryService(EncoderService encoders, SettingsManager settings)
    {
        _encoders = encoders;
        _settings = settings;
    }

    /// <summary>
    /// 扫描临时目录中的遗留片段，若有则导出最近一段到 Recovered 文件夹。
    /// 返回恢复出的文件路径列表。
    /// </summary>
    public async Task<List<string>> RecoverFromCrashAsync()
    {
        var recovered = new List<string>();
        try
        {
            var s = _settings.Settings;
            var liveDir = Path.Combine(s.ResolveTempDir(), "live");
            if (!Directory.Exists(liveDir)) return recovered;

            var segments = Directory.GetFiles(liveDir, "seg_*.ts")
                .Where(f => new FileInfo(f).Length > 0)
                .Select(f => (path: f, mtime: new FileInfo(f).LastWriteTimeUtc))
                .OrderBy(x => x.mtime)
                .ToList();

            if (segments.Count == 0) return recovered;

            // 只保留最近的回溯时长窗口（防止恢复出超长/超久视频）
            var cutoff = DateTime.UtcNow.AddSeconds(-s.ReplayDurationSeconds - 10);
            var window = segments.Where(x => x.mtime >= cutoff).Select(x => x.path).ToList();
            if (window.Count == 0) return recovered;

            var exportDir = Path.Combine(s.ResolveExportDir(), "Recovered");
            Directory.CreateDirectory(exportDir);
            var outputPath = Path.Combine(exportDir,
                ReplayExporter.BuildFileName($"Recovered_{DateTime.Now:yyyyMMdd_HHmmss}", "crash", s.CaptureWidth > 0 ? $"{s.CaptureWidth}x{s.CaptureHeight}" : "Unknown", "mp4"));

            var exporter = new ReplayExporter(_encoders);
            var path = await exporter.ExportAsync(window, s, null, "crash", s.CaptureWidth, s.CaptureHeight, s.ReplayDurationSeconds);
            recovered.Add(path);
            Log?.Invoke(this, $"已从崩溃缓存恢复回放: {path}");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"崩溃恢复失败: {ex.Message}");
        }
        return recovered;
    }
}
