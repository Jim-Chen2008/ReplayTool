using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>
/// 中枢协调器：串联 环形缓存 / 编码器 / 音频 / 热键 / 磁盘监控 / 崩溃恢复，
/// 并提供手动录制、截图、开机自启等高层操作。App 层只依赖此门面。
/// </summary>
public class ReplayEngine : IDisposable
{
    public SettingsManager Settings { get; }
    public EncoderService Encoders { get; }
    public AudioCaptureService Audio { get; }
    public ReplayExporter Exporter { get; }
    public RingBufferService Ring { get; }
    public DiskMonitor Disk { get; }
    public RecoveryService Recovery { get; }

    // 手动录制（Alt+F9 正向录制）
    private Process? _manualProcess;
    private readonly object _manualLock = new();
    public bool IsManualRecording => _manualProcess is { HasExited: false };

    // 事件（UI 层订阅）
    public event EventHandler<string>? Toast;                 // 轻量提示
    public event EventHandler<string>? Log;                   // 详细日志
    public event EventHandler<ReplayStatus>? StatusChanged;
    public event EventHandler<string>? ReplaySaved;           // 参数=导出路径
    public event EventHandler<string>? ScreenshotSaved;       // 参数=截图路径
    public event EventHandler<bool>? ManualRecordingChanged;

    public ReplayEngine(string? ffmpegPath = null, string? settingsDir = null)
    {
        Settings = new SettingsManager(settingsDir);
        Encoders = new EncoderService(ffmpegPath);
        Audio = new AudioCaptureService(Settings);
        Exporter = new ReplayExporter(Encoders);
        Ring = new RingBufferService(Encoders, Audio, Exporter, Settings);
        Disk = new DiskMonitor(Settings);
        Recovery = new RecoveryService(Encoders, Settings);

        WireLogs();
    }

    private void WireLogs()
    {
        void Fwd(object? s, string msg) => Log?.Invoke(this, msg);
        Encoders.Log += Fwd;
        Audio.Log += Fwd;
        Ring.Log += Fwd;
        Disk.Log += Fwd;
        Recovery.Log += Fwd;
        Ring.StatusChanged += (_, st) => StatusChanged?.Invoke(this, st);
    }

    /// <summary>处理热键事件（由 App 层热键服务调用）。</summary>
    public void HandleHotkey(HotkeyKind kind)
    {
        switch (kind)
        {
            case HotkeyKind.SaveReplay:
                _ = SaveReplayAsync();
                break;
            case HotkeyKind.ManualRecord:
                ToggleManualRecording();
                break;
            case HotkeyKind.Screenshot:
                _ = TakeScreenshotAsync();
                break;
            case HotkeyKind.OpenPanel:
                Toast?.Invoke(this, "__OPEN_PANEL__");
                break;
        }
    }

    // ======================= 生命周期 =======================

    public void Start()
    {
        Ring.Start();
        SyncRunAtStartup();
    }

    public void Stop()
    {
        if (IsManualRecording) ToggleManualRecording();
        Ring.Stop();
    }

    public void RestartRecording()
    {
        Ring.Stop();
        Ring.Start();
    }

    public async Task<string?> SaveReplayAsync()
    {
        var path = await Ring.SaveReplayAsync();
        if (path != null)
            ReplaySaved?.Invoke(this, path);
        return path;
    }

    // ======================= 手动录制（Alt+F9） =======================

    public void ToggleManualRecording()
    {
        lock (_manualLock)
        {
            if (IsManualRecording)
            {
                StopManualRecording();
            }
            else
            {
                StartManualRecording();
            }
        }
    }

    private void StartManualRecording()
    {
        try
        {
            var s = Settings.Settings;
            var (w, h) = Ring.CurrentResolution;
            int fps = s.Framerate > 0 ? s.Framerate : Native.DisplayInfo.PrimaryRefreshRate;
            int bitrate = EncoderService.ResolveBitrateKbps(s.BitratePreset, s.CustomBitrateMbps, w, h);
            var format = s.EncoderFormat == "hevc" ? VideoCodecFormat.HEVC : VideoCodecFormat.H264;
            var encoder = Encoders.SelectVerifiedEncoder(format);

            var exportDir = s.ResolveExportDir();
            var fileName = ReplayExporter.BuildFileName($"Record_{DateTime.Now:yyyyMMdd_HHmmss}", "manual", $"{w}x{h}", "mp4");
            var output = Path.Combine(exportDir, fileName);

            var vf = BuildManualFilter(s);
            // 注意：手动录制不能加 -nostdin，否则无法通过 stdin 发送 'q' 优雅结束（保证 MP4 完整）
            var args = $"-hide_banner -loglevel warning " +
                       $"-f gdigrab -framerate {fps} -video_size {w}x{h} -i desktop " +
                       $"-map 0:v:0 -c:v {encoder.EncoderName} -b:v {bitrate}k " +
                       (encoder.IsHardware ? "-preset veryfast " : "-preset veryfast -threads 0 ") +
                       (vf.Length > 0 ? $"-vf \"{vf}\" " : "") +
                       $"-movflags +faststart \"{output}\"";

            var psi = new ProcessStartInfo(Encoders.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            _manualProcess = Process.Start(psi);
            _ = Task.Run(() => { _manualProcess?.StandardError.ReadToEnd(); });
            ManualRecordingChanged?.Invoke(this, true);
            Toast?.Invoke(this, "手动录制已开始");
            Log?.Invoke(this, $"手动录制开始: {output}");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"手动录制启动失败: {ex.Message}");
        }
    }

    private void StopManualRecording()
    {
        try
        {
            var proc = _manualProcess;
            _manualProcess = null;
            if (proc is { HasExited: false })
            {
                proc.StandardInput.WriteLine("q");
                proc.StandardInput.Flush();
                if (!proc.WaitForExit(4000))
                    proc.Kill(entireProcessTree: true);
            }
            ManualRecordingChanged?.Invoke(this, false);
            Toast?.Invoke(this, "手动录制已保存");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"停止手动录制异常: {ex.Message}");
            ManualRecordingChanged?.Invoke(this, false);
        }
    }

    private static string BuildManualFilter(AppSettings s)
    {
        var parts = new List<string>();
        if (s.ColorEnhancement) parts.Add("eq=saturation=1.3");
        if (s.Contrast != 0) parts.Add($"eq=contrast={1.0 + s.Contrast / 50.0:0.00}");
        if (s.BrightnessBoost) parts.Add("eq=brightness=0.08");
        if (s.Sharpness > 0) parts.Add($"unsharp=5:5:{(s.Sharpness / 50.0):0.00}:5:5:0");
        return string.Join(",", parts);
    }

    // ======================= 截图（Alt+F1） =======================

    public async Task<string?> TakeScreenshotAsync()
    {
        try
        {
            var s = Settings.Settings;
            var (w, h) = Ring.CurrentResolution;
            var exportDir = s.ResolveExportDir();
            var fileName = ReplayExporter.BuildFileName($"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", "screen", $"{w}x{h}", "png");
            var output = Path.Combine(exportDir, fileName);

            var args = $"-hide_banner -loglevel error -y -f gdigrab -framerate 1 -video_size {w}x{h} -i desktop -frames:v 1 \"{output}\"";

            var psi = new ProcessStartInfo(Encoders.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0 || !File.Exists(output))
                return null;

            ScreenshotSaved?.Invoke(this, output);
            Toast?.Invoke(this, $"截图已保存: {Path.GetFileName(output)}");
            return output;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"截图失败: {ex.Message}");
            return null;
        }
    }

    // ======================= 开机自启 =======================

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ReplayTool";

    public void SyncRunAtStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            var exePath = "\"" + (Environment.ProcessPath ?? "ReplayTool.exe") + "\" --autostart";
            if (Settings.Settings.RunAtStartup)
                key.SetValue(RunValueName, exePath, RegistryValueKind.String);
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"设置开机自启失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        Disk.Dispose();
        Ring.Dispose();
    }
}
