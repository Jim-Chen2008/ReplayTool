using System.Diagnostics;
using System.Text.RegularExpressions;
using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>
/// 编码引擎管理：定位 FFmpeg、检测可用硬件编码器、择优选择。
/// 完全不依赖 NVIDIA NVENC —— 优先 Intel QSV / AMD VCN / Windows Media Foundation / D3D12VA，软编码兜底。
/// </summary>
public class EncoderService
{
    /// <summary>FFmpeg 可执行文件路径。</summary>
    public string FfmpegPath { get; private set; }

    /// <summary>FFprobe 可执行文件路径（可为空）。</summary>
    public string FfprobePath { get; private set; }

    public event EventHandler<string>? Log;

    private List<string> _availableEncoders = new();
    private readonly object _lock = new();

    /// <summary>硬件编码器优先级（H.264）。</summary>
    private static readonly string[] H264Priority = { "h264_qsv", "h264_amf", "h264_mf", "h264_d3d12va", "libx264" };
    /// <summary>硬件编码器优先级（HEVC/H.265）。</summary>
    private static readonly string[] HEVCPriority = { "hevc_qsv", "hevc_amf", "hevc_mf", "hevc_d3d12va", "libx265" };

    public EncoderService(string? ffmpegPath = null)
    {
        FfmpegPath = LocateFfmpeg(ffmpegPath);
        FfprobePath = LocateFfprobe();
    }

    public static string LocateFfmpeg(string? configured = null)
    {
        // 1) 显式配置
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // 2) 应用目录下捆绑的 ffmpeg
        var appDir = AppContext.BaseDirectory;
        foreach (var sub in new[] { "ffmpeg", "" })
        {
            var candidate = Path.Combine(appDir, sub, "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // 3) 环境变量
        var env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        // 4) PATH 中的 ffmpeg
        var which = FindInPath("ffmpeg.exe");
        if (which != null) return which;

        // 5) 常见安装位置
        foreach (var candidate in new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return "ffmpeg.exe"; // 交给系统 PATH，若不存在则运行时给出清晰报错
    }

    private static string? FindInPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 跳过无效路径 */ }
        }
        return null;
    }

    private static string LocateFfprobe()
    {
        var ffmpegDir = Path.GetDirectoryName(LocateFfmpeg());
        if (ffmpegDir != null)
        {
            var p = Path.Combine(ffmpegDir, "ffprobe.exe");
            if (File.Exists(p)) return p;
        }
        return FindInPath("ffprobe.exe") ?? "";
    }

    /// <summary>
    /// 从 `ffmpeg -encoders` 输出中解析可用编码器列表。
    /// 注意：列表实际输出在 stdout（部分构建在 stderr），须异步读双管道防死锁。
    /// </summary>
    public List<string> DetectAvailableEncoders()
    {
        lock (_lock)
        {
            _availableEncoders = new List<string>();
            try
            {
                var psi = new ProcessStartInfo(FfmpegPath, "-hide_banner -nostdin -encoders")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return _availableEncoders;

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!Task.WaitAll(new[] { stdoutTask, stderrTask }, 8000))
                {
                    try { proc.Kill(); } catch { /* ignore */ }
                    return _availableEncoders;
                }
                proc.WaitForExit(1000);

                foreach (var line in stdoutTask.Result.Split('\n'))
                    AddEncoderFromLine(line);
                foreach (var line in stderrTask.Result.Split('\n'))
                    AddEncoderFromLine(line);
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"编码器检测失败: {ex.Message}");
            }
            return _availableEncoders;
        }
    }

    private void AddEncoderFromLine(string line)
    {
        var m = Regex.Match(line, @"^\s*[VAS\.]{6}\s+(\S+)");
        if (m.Success && !_availableEncoders.Contains(m.Groups[1].Value))
            _availableEncoders.Add(m.Groups[1].Value);
    }

    public bool EncoderAvailable(string name) =>
        _availableEncoders.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 选择最佳可用编码器。按 硬件→软件 优先级，跳过不可用的。
    /// </summary>
    public EncoderSelection SelectEncoder(VideoCodecFormat format)
    {
        if (_availableEncoders.Count == 0)
            DetectAvailableEncoders();

        var priority = format == VideoCodecFormat.HEVC ? HEVCPriority : H264Priority;
        foreach (var name in priority)
        {
            if (EncoderAvailable(name))
            {
                var isHardware = name.Contains("qsv") || name.Contains("amf") || name.Contains("mf")
                                 || name.Contains("d3d12va") || name.Contains("nvenc") || name.Contains("vaapi");
                return new EncoderSelection(name, format, isHardware,
                    isHardware ? $"硬件编码 {name}" : $"软件编码 {name}（无可用硬件编码器）");
            }
        }
        // 理论不可达：libx264/libx265 总会存在
        return new EncoderSelection(format == VideoCodecFormat.HEVC ? "libx265" : "libx264",
            format, false, "软件编码（默认兜底）");
    }

    /// <summary>
    /// 用一个 1 帧测试视频验证编码器能否在真实硬件上初始化。
    /// 硬件编码器可能出现在列表但驱动不完整，导致初始化挂起或失败。
    /// 使用硬超时 + 强杀，避免损坏的硬件编码器阻塞整个启动流程。
    /// </summary>
    public bool VerifyEncoder(string encoderName)
    {
        try
        {
            var args = $"-hide_banner -loglevel error -nostdin -f lavfi -i color=black:s=320x240:r=1 -frames:v 1 -c:v {encoderName} -f null -";
            var psi = new ProcessStartInfo(FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!Task.WaitAll(new[] { stdoutTask, stderrTask }, 8000))
            {
                try { proc.Kill(); } catch { /* ignore */ }
                Log?.Invoke(this, $"编码器 {encoderName} 验证超时，已跳过");
                return false;
            }
            proc.WaitForExit(1000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 依次验证候选硬件编码器，返回第一个可用的；全部失败则回退软件编码。
    /// </summary>
    public EncoderSelection SelectVerifiedEncoder(VideoCodecFormat format)
    {
        if (_availableEncoders.Count == 0)
            DetectAvailableEncoders();

        var priority = format == VideoCodecFormat.HEVC ? HEVCPriority : H264Priority;
        foreach (var name in priority)
        {
            if (!EncoderAvailable(name)) continue;
            var isHardware = !(name.StartsWith("libx"));
            if (isHardware)
            {
                if (VerifyEncoder(name))
                {
                    Log?.Invoke(this, $"编码器 {name} 验证通过");
                    return new EncoderSelection(name, format, true, $"硬件编码 {name}");
                }
                Log?.Invoke(this, $"编码器 {name} 初始化失败，尝试下一个");
            }
            else
            {
                return new EncoderSelection(name, format, false, $"软件编码 {name}");
            }
        }
        return new EncoderSelection(format == VideoCodecFormat.HEVC ? "libx265" : "libx264",
            format, false, "软件编码（兜底）");
    }

    /// <summary>根据码率档位返回目标码率（kbps）。</summary>
    public static int ResolveBitrateKbps(string preset, int customMbps, int width, int height)
    {
        // 依据分辨率与档位估算合理码率
        int pixels = Math.Max(1, width * height);
        int baseRate = preset switch
        {
            "low" => 2500,
            "medium" => 6000,
            "high" => 12000,
            "custom" => customMbps * 1000,
            _ => 12000,
        };
        if (preset == "custom")
            return customMbps * 1000;

        // 1080p=约2M像素，按像素比例缩放
        double scale = Math.Sqrt((double)pixels / (1920.0 * 1080.0));
        var kbps = (int)(baseRate * scale);
        return Math.Clamp(kbps, 800, 60000);
    }
}
