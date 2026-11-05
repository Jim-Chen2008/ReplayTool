using System.Diagnostics;
using System.IO;
using ReplayTool.Core.Models;
using ReplayTool.Core.Services;

namespace ReplayTool.SmokeTest;

/// <summary>
/// 命令行冒烟测试：验证 环形缓存录制 → 回放导出 全流程。
/// 用法：ReplayTool.SmokeTest [--no-audio] [--duration 15] [--segments]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        bool noAudio = args.Contains("--no-audio");
        int duration = ReadDuration(args);
        Console.WriteLine($"=== ReplayTool 冒烟测试 === 回溯时长={duration}s, 音频={(noAudio ? "关" : "开")}");
        Console.WriteLine($"FFmpeg: {EncoderService.LocateFfmpeg()}");

        // 用独立测试目录避免污染正式设置
        var testBase = Path.Combine(Path.GetTempPath(), "ReplayToolTest");
        if (Directory.Exists(testBase)) Directory.Delete(testBase, recursive: true);
        var settingsManager = new SettingsManager(Path.Combine(testBase, "cfg"));

        var s = settingsManager.Settings;
        s.ReplayDurationSeconds = duration;
        s.CaptureSystemAudio = !noAudio;
        s.CaptureMic = false;
        s.TempCacheDir = Path.Combine(testBase, "cache");
        s.ExportDir = Path.Combine(testBase, "export");
        settingsManager.Save(s);

        var engine = new ReplayEngine(settingsDir: Path.Combine(testBase, "cfg"));
        engine.Log += (_, m) => Console.WriteLine($"[LOG] {m}");

        try
        {
            // 1. 启动环形录制
            Console.WriteLine(">>> 启动环形缓存录制…");
            engine.Start();
            Thread.Sleep(TimeSpan.FromSeconds(duration + 4));

            // 2. 检查缓存片段
            var liveDir = engine.Ring.LiveDir;
            var segments = Directory.GetFiles(liveDir, "seg_*.ts");
            Console.WriteLine($">>> 缓存片段数: {segments.Length}");
            long cacheBytes = segments.Sum(f => new FileInfo(f).Length);
            Console.WriteLine($">>> 缓存大小: {cacheBytes / 1024 / 1024} MB");
            Console.WriteLine($">>> 编码器: {engine.Ring.CurrentEncoder?.EncoderName} (硬件: {engine.Ring.CurrentEncoder?.IsHardware})");

            // 3. 保存回放
            Console.WriteLine(">>> 触发保存回放…");
            var path = engine.SaveReplayAsync().GetAwaiter().GetResult();
            if (path == null)
            {
                Console.WriteLine("FAIL: 保存回放返回 null");
                return 1;
            }
            Console.WriteLine($">>> 回放已导出: {path}");
            var fi = new FileInfo(path);
            Console.WriteLine($">>> 文件大小: {fi.Length / 1024} KB");

            // 4. ffprobe 验证
            var info = Probe(path, engine.Encoders.FfprobePath);
            Console.WriteLine($">>> ffprobe: {info}");
            bool hasVideo = info.Contains("video");
            bool hasAudio = info.Contains("audio");

            bool pass = fi.Length > 100_000
                        && hasVideo
                        && !info.Contains("Unknown");
            Console.WriteLine($">>> 视频流: {(hasVideo ? "有" : "无")}, 音频流: {(hasAudio ? "有" : "无")}");
            Console.WriteLine(pass ? "=== SMOKE TEST PASS ===" : "=== SMOKE TEST FAIL ===");

            // 5. 二次保存（验证环形持续）
            Console.WriteLine(">>> 等待 6 秒后二次保存（验证环形持续）…");
            Thread.Sleep(6000);
            var path2 = engine.SaveReplayAsync().GetAwaiter().GetResult();
            Console.WriteLine(path2 != null ? $">>> 二次保存成功: {path2}" : ">>> 二次保存失败");

            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex}");
            return 1;
        }
        finally
        {
            engine.Dispose();
            Console.WriteLine(">>> 已停止。缓存目录: " + Path.Combine(testBase, "cache"));
        }
    }

    private static int ReadDuration(string[] args)
    {
        int idx = Array.IndexOf(args, "--duration");
        return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var d)
            ? Math.Clamp(d, 5, 120)
            : 15;
    }

    private static string Probe(string file, string ffprobePath)
    {
        if (string.IsNullOrEmpty(ffprobePath) || !File.Exists(ffprobePath))
            return "(ffprobe 不可用)";
        var psi = new ProcessStartInfo(ffprobePath,
            $"-v error -show_entries format=duration,size:stream=codec_name,codec_type,width,height,r_frame_rate -of csv \"{file}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        if (proc == null) return "(无法运行 ffprobe)";
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(8000);
        return stdout.Replace("\n", " | ").Trim();
    }
}
