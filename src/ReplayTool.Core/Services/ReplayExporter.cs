using System.Diagnostics;
using System.Text;
using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>
/// 回放导出器：将选定的 TS 片段拼接重封装为独立 MP4 成品。
/// 使用 concat demuxer + 流复制（-c copy），秒级完成，无画质损失。
/// </summary>
public class ReplayExporter
{
    private readonly EncoderService _encoders;

    public ReplayExporter(EncoderService encoders)
    {
        _encoders = encoders;
    }

    /// <summary>
    /// 拼接导出。返回成品文件路径；失败抛出异常。
    /// </summary>
    public async Task<string> ExportAsync(
        List<string> segments,
        AppSettings settings,
        EncoderSelection? encoder,
        string processName,
        int width,
        int height,
        int durationSeconds)
    {
        if (segments.Count == 0)
            throw new InvalidOperationException("无可用片段");

        var exportDir = settings.ResolveExportDir();
        var resolution = $"{width}x{height}";
        var fileName = BuildFileName(settings.FilenameTemplate, processName, resolution, "mp4");
        var outputPath = Path.Combine(exportDir, fileName);

        // 拼接列表文件（concat demuxer，绝对路径需 -safe 0）
        var listPath = Path.Combine(settings.ResolveTempDir(), $"concat_{Guid.NewGuid():N}.txt");
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            sb.AppendLine($"file '{seg.Replace("\\", "/")}'");
        }
        await File.WriteAllTextAsync(listPath, sb.ToString());

        try
        {
            // 视频编码器信息用于保证参数一致（可选）
            var args = $"-hide_banner -loglevel warning -y -f concat -safe 0 -i \"{listPath}\" " +
                       $"-c copy -movflags +faststart \"{outputPath}\"";

            var psi = new ProcessStartInfo(_encoders.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("无法启动导出进程");
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"导出失败: {err.Trim()}");

            return outputPath;
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 按文件名模板生成文件名。支持占位符：
    /// {timestamp}  → 20260805_1530
    /// {process}    → 前台进程名
    /// {resolution} → 1920x1080
    /// {duration}   → 120s
    /// </summary>
    public static string BuildFileName(string template, string processName, string resolution, string extension)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var name = template
            .Replace("{timestamp}", ts)
            .Replace("{process}", SanitizeFileName(processName))
            .Replace("{resolution}", resolution)
            .Replace("{duration}", "");
        name = string.IsNullOrWhiteSpace(name) ? $"Replay_{ts}" : name;
        return SanitizeFileName(name) + "." + extension;
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in input)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return result.Length == 0 ? "Replay" : result;
    }
}
