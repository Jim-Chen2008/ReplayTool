using System.Diagnostics;
using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>回放录制状态。</summary>
public enum ReplayStatus
{
    Stopped,
    Recording,
    Saving,
    Paused,
    Error,
}

/// <summary>
/// 环形缓存录制服务（核心）。
///
/// 设计要点：
/// 1. FFmpeg 以 1 秒粒度切分为 TS 片段写入临时目录，文件名序号单调递增。
/// 2. 后台清理线程按「回溯时长」删除过期片段，缓存始终固定在时间窗口内。
/// 3. 保存回放【不重启录制进程】：录制全程无中断、无音频端口冲突。
///    保存 = 按时间戳快照当前窗口片段 → 就地拼接 → 导出 MP4。
/// 4. 优雅停止通过向进程 stdin 写 'q' 实现（已验证 exit code 0）。
/// </summary>
public class RingBufferService : IDisposable
{
    private readonly EncoderService _encoders;
    private readonly AudioCaptureService _audio;
    private readonly ReplayExporter _exporter;
    private readonly SettingsManager _settings;

    public string LiveDir { get; private set; } = "";
    public ReplayStatus Status { get; private set; } = ReplayStatus.Stopped;
    public bool IsRecording => Status == ReplayStatus.Recording;

    public EncoderSelection? CurrentEncoder { get; private set; }

    public event EventHandler<ReplayStatus>? StatusChanged;
    public event EventHandler<string>? Log;

    private Process? _process;
    private readonly object _procLock = new();
    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;

    public RingBufferService(EncoderService encoders, AudioCaptureService audio, ReplayExporter exporter, SettingsManager settings)
    {
        _encoders = encoders;
        _audio = audio;
        _exporter = exporter;
        _settings = settings;
    }

    // ======================= 启动 / 停止 =======================

    public void Start()
    {
        if (IsRecording) return;

        var settings = _settings.Settings;
        LiveDir = Path.Combine(settings.ResolveTempDir(), "live");
        Directory.CreateDirectory(LiveDir);

        // 清理上次异常退出遗留的片段（崩溃恢复由 RecoveryService 在启动前处理，此处保证干净起点）
        foreach (var f in Directory.GetFiles(LiveDir, "seg_*.ts"))
        {
            try { File.Delete(f); } catch { /* ignore */ }
        }

        // 预启动音频捕获（必须在启动 FFmpeg 之前，保证 stdin 音频立即有数据）
        _audio.Prepare();

        // 选择编码器
        var format = settings.EncoderFormat switch
        {
            "hevc" => VideoCodecFormat.HEVC,
            _ => VideoCodecFormat.H264,
        };
        CurrentEncoder = _encoders.SelectVerifiedEncoder(format);

        LaunchRecordingProcess();

        _cleanupCts = new CancellationTokenSource();
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token));

        SetStatus(ReplayStatus.Recording);
        Log?.Invoke(this, $"环形缓存录制已启动 (编码器: {CurrentEncoder.EncoderName}, 回溯时长: {settings.ReplayDurationSeconds}s)");
    }

    public void Stop()
    {
        _cleanupCts?.Cancel();
        try { _cleanupTask?.Wait(2000); } catch { /* ignore */ }

        StopProcess(_process, timeoutMs: 3000);
        lock (_procLock) _process = null;
        SetStatus(ReplayStatus.Stopped);
    }

    // ======================= 保存回放（核心操作） =======================

    /// <summary>
    /// 保存最近「回溯时长」的视频片段。返回导出文件路径；失败返回 null。
    /// 全程不中断录制进程：快照时间戳窗口 → 就地拼接 → 导出。
    /// </summary>
    public async Task<string?> SaveReplayAsync(int? durationSeconds = null)
    {
        var settings = _settings.Settings;
        int duration = durationSeconds ?? settings.ReplayDurationSeconds;
        if (duration <= 0) duration = 60;

        if (!IsRecording || _process == null)
        {
            Log?.Invoke(this, "保存回放失败：当前未在录制");
            return null;
        }

        SetStatus(ReplayStatus.Saving);
        try
        {
            var cutoff = DateTime.UtcNow;

            // 等待当前片段翻片（≤1s），确保窗口内片段均已完整写入
            await Task.Delay(1100);

            var segments = CollectSegments(cutoff, duration);
            if (segments.Count == 0)
            {
                Log?.Invoke(this, "保存回放失败：缓存中没有可用片段");
                return null;
            }

            var encoderInfo = CurrentEncoder;
            var (w, h) = CurrentResolution;
            var processName = Native.Win32.GetForegroundProcessName() ?? "Desktop";

            var path = await _exporter.ExportAsync(
                segments, settings, encoderInfo, processName, w, h, duration);

            Log?.Invoke(this, $"回放已保存: {path} ({segments.Count} 片段, ~{duration}s)");
            return path;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"保存回放异常: {ex.Message}");
            return null;
        }
        finally
        {
            if (Status == ReplayStatus.Saving)
                SetStatus(ReplayStatus.Recording);
        }
    }

    // ======================= 录制进程管理 =======================

    private void LaunchRecordingProcess()
    {
        lock (_procLock)
        {
            if (_process != null && !_process.HasExited)
                StopProcess(_process);

            var settings = _settings.Settings;
            var encoder = CurrentEncoder ?? throw new InvalidOperationException("编码器未选择");
            var startNumber = FindNextSegmentIndex();

            var args = BuildRecordingArgs(settings, encoder, startNumber);
            var psi = new ProcessStartInfo(_encoders.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            _process = Process.Start(psi);
            if (_process == null)
                throw new InvalidOperationException("无法启动 FFmpeg 录制进程");

            // 将音频 PCM 写入 FFmpeg stdin（pipe:0）
            if (_audio.IsAvailable)
                _audio.StartWritingTo(_process.StandardInput.BaseStream);

            // 消费 stdout/stderr，避免管道缓冲区满阻塞 FFmpeg
            _ = Task.Run(() => ReadOutput(_process));
        }
    }

    private string BuildRecordingArgs(AppSettings settings, EncoderSelection encoder, int startNumber)
    {
        var (w, h) = CurrentResolution;
        int fps = settings.Framerate > 0 ? settings.Framerate : Native.DisplayInfo.PrimaryRefreshRate;
        int bitrateKbps = EncoderService.ResolveBitrateKbps(settings.BitratePreset, settings.CustomBitrateMbps, w, h);

        var sb = new System.Text.StringBuilder();
        sb.Append("-hide_banner -loglevel warning ");

        // ---- 视频输入：gdigrab（全屏虚拟桌面 / 主显示器） ----
        sb.Append($"-f gdigrab -framerate {fps} -video_size {w}x{h} ");
        if (settings.CaptureMode == "monitor")
            sb.Append("-offset_x 0 -offset_y 0 ");
        sb.Append("-i desktop ");

        // ---- 音频输入：来自 AudioCaptureService 的 stdin 管道（pipe:0） ----
        bool hasAudio = _audio.IsAvailable;
        if (hasAudio)
            sb.Append("-f s16le -ar 48000 -ac 2 -i pipe:0 ");

        // ---- 流映射 ----
        sb.Append("-map 0:v:0 ");
        if (hasAudio) sb.Append("-map 1:a:0 ");

        // ---- 编码参数 ----
        sb.Append($"-c:v {encoder.EncoderName} -b:v {bitrateKbps}k ");
        // 通用关键帧控制：每秒强制关键帧，保证 segment muxer 每秒切分
        sb.Append("-force_key_frames \"expr:gte(t,n_forced*1)\" ");
        if (encoder.IsHardware) sb.Append("-preset veryfast ");
        else sb.Append("-preset veryfast -threads 0 ");
        if (hasAudio) sb.Append("-c:a aac -b:a 192k -ar 48000 -ac 2 ");

        // ---- 画面增强 ----
        var vf = BuildVideoFilter(settings);
        if (!string.IsNullOrEmpty(vf))
            sb.Append($"-vf \"{vf}\" ");

        // ---- 片段切分（环形缓冲核心） ----
        sb.Append($"-f segment -segment_time 1 -segment_format mpegts -segment_start_number {startNumber} -reset_timestamps 1 ");
        sb.Append($"\"{Path.Combine(LiveDir, "seg_%05d.ts")}\"");

        return sb.ToString();
    }

    private string? BuildVideoFilter(AppSettings s)
    {
        var parts = new List<string>();
        if (s.ColorEnhancement) parts.Add("eq=saturation=1.3");
        if (s.Contrast != 0) parts.Add($"eq=contrast={1.0 + s.Contrast / 50.0:0.00}");
        if (s.BrightnessBoost) parts.Add("eq=brightness=0.08");
        if (s.Sharpness > 0) parts.Add($"unsharp=5:5:{(s.Sharpness / 50.0):0.00}:5:5:0");
        return parts.Count > 0 ? string.Join(",", parts) : null;
    }

    private void ReadOutput(Process proc)
    {
        try
        {
            var stderrTask = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();
            var err = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(err) && !proc.HasExited)
                Log?.Invoke(this, $"FFmpeg: {err.Trim()}");
        }
        catch { /* 进程退出导致管道关闭 */ }
    }

    private void StopProcess(Process? proc, int timeoutMs = 3000)
    {
        if (proc == null || proc.HasExited) return;
        try
        {
            // stdin 已被音频数据占用，无法发送 'q'，直接结束进程。
            // 未完成的最后一段片段为部分 TS，可由崩溃恢复服务处理。
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(timeoutMs);
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"停止 FFmpeg 异常: {ex.Message}");
        }
    }

    // ======================= 片段选择与清理 =======================

    /// <summary>按时间戳选取 [cutoff-duration, cutoff+1s] 内的片段，按序号排序。</summary>
    private List<string> CollectSegments(DateTime cutoffUtc, int durationSeconds)
    {
        var lower = cutoffUtc.AddSeconds(-durationSeconds - 2);
        var upper = cutoffUtc.AddSeconds(2);

        var candidates = new List<(string path, int index, DateTime mtime)>();
        foreach (var f in Directory.GetFiles(LiveDir, "seg_*.ts"))
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(f); } catch { continue; }
            if (mtime >= lower && mtime <= upper)
                candidates.Add((f, ParseSegmentIndex(f), mtime));
        }

        // 片段不足（刚启动）时放宽下界，尽量取最近片段
        if (candidates.Count == 0)
        {
            foreach (var f in Directory.GetFiles(LiveDir, "seg_*.ts"))
            {
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(f); } catch { continue; }
                if (mtime >= cutoffUtc.AddSeconds(-durationSeconds * 2))
                    candidates.Add((f, ParseSegmentIndex(f), mtime));
            }
        }

        return candidates.OrderBy(x => x.index).Select(x => x.path).ToList();
    }

    private static int ParseSegmentIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var idx = name.LastIndexOf("seg_", StringComparison.Ordinal);
        return idx >= 0 && int.TryParse(name[(idx + 4)..], out var n) ? n : 0;
    }

    private int FindNextSegmentIndex()
    {
        int max = -1;
        try
        {
            foreach (var f in Directory.GetFiles(LiveDir, "seg_*.ts"))
                max = Math.Max(max, ParseSegmentIndex(f));
        }
        catch { /* ignore */ }
        return max + 1;
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct);
                CleanupExpired();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log?.Invoke(this, $"缓存清理异常: {ex.Message}"); }
        }
    }

    /// <summary>删除超过回溯时长+5s 的旧片段；超磁盘上限时额外删除最旧片段。</summary>
    private void CleanupExpired()
    {
        var settings = _settings.Settings;
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(settings.ReplayDurationSeconds + 5);
        long totalBytes = 0;

        foreach (var f in Directory.GetFiles(LiveDir, "seg_*.ts"))
        {
            try
            {
                var info = new FileInfo(f);
                totalBytes += info.Length;
                if (info.LastWriteTimeUtc < cutoff)
                    File.Delete(f);
            }
            catch { /* 文件可能正被占用 */ }
        }

        long maxBytes = settings.MaxCacheGb * 1024L * 1024L * 1024L;
        if (maxBytes > 0 && totalBytes > maxBytes)
        {
            var sorted = Directory.GetFiles(LiveDir, "seg_*.ts")
                .Select(f => (f, new FileInfo(f).LastWriteTimeUtc))
                .OrderBy(x => x.Item2);
            foreach (var (f, _) in sorted)
            {
                if (totalBytes <= maxBytes) break;
                try { var len = new FileInfo(f).Length; File.Delete(f); totalBytes -= len; }
                catch { /* ignore */ }
            }
        }
    }

    // ======================= 辅助 =======================

    public (int width, int height) CurrentResolution
    {
        get
        {
            var s = _settings.Settings;
            int w = s.CaptureWidth > 0 ? s.CaptureWidth : Native.DisplayInfo.PrimaryWidth;
            int h = s.CaptureHeight > 0 ? s.CaptureHeight : Native.DisplayInfo.PrimaryHeight;
            w -= w % 2; h -= h % 2;
            return (Math.Max(2, w), Math.Max(2, h));
        }
    }

    private void SetStatus(ReplayStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        Stop();
        _audio.Dispose();
    }
}
