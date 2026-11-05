namespace ReplayTool.Core.Models;

/// <summary>
/// 应用全局设置模型。序列化为 %APPDATA%\ReplayTool\settings.json。
/// </summary>
public class AppSettings
{
    // ===== 回放（环形缓存） =====
    /// <summary>回溯时长（秒）。对应 N 卡 Instant Replay 的缓冲时长。</summary>
    public int ReplayDurationSeconds { get; set; } = 120;

    /// <summary>临时缓存目录。为空则使用默认：%TEMP%\ReplayTool。</summary>
    public string TempCacheDir { get; set; } = "";

    /// <summary>缓存磁盘占用上限（GB）。默认 20GB。</summary>
    public long MaxCacheGb { get; set; } = 20;

    /// <summary>磁盘剩余空间低于该值（GB）时发出预警并自动降档。默认 5GB。</summary>
    public long DiskWarningThresholdGb { get; set; } = 5;

    // ===== 视频画质 =====
    /// <summary>捕获分辨率宽度，0 表示跟随屏幕原始分辨率。</summary>
    public int CaptureWidth { get; set; } = 0;
    /// <summary>捕获分辨率高度，0 表示跟随屏幕原始分辨率。</summary>
    public int CaptureHeight { get; set; } = 0;

    /// <summary>帧率：0=跟随显示器刷新率，否则为指定帧率。</summary>
    public int Framerate { get; set; } = 60;

    /// <summary>码率档位：low / medium / high / custom。</summary>
    public string BitratePreset { get; set; } = "high";

    /// <summary>自定义码率（Mbps），仅当 BitratePreset=custom 时使用。</summary>
    public int CustomBitrateMbps { get; set; } = 50;

    /// <summary>编码格式：auto / h264 / hevc。</summary>
    public string EncoderFormat { get; set; } = "auto";

    /// <summary>HDR 录制开关。</summary>
    public bool HdrEnabled { get; set; } = false;

    // ===== 画面增强 =====
    /// <summary>锐化强度 0-100。</summary>
    public int Sharpness { get; set; } = 0;
    /// <summary>对比度调整 -50 ~ +50。</summary>
    public int Contrast { get; set; } = 0;
    /// <summary>游戏色彩增强开关。</summary>
    public bool ColorEnhancement { get; set; } = false;
    /// <summary>暗部提亮开关。</summary>
    public bool BrightnessBoost { get; set; } = false;

    // ===== 音频 =====
    /// <summary>系统主声道录制开关。</summary>
    public bool CaptureSystemAudio { get; set; } = true;
    /// <summary>麦克风录制独立开关。</summary>
    public bool CaptureMic { get; set; } = false;
    /// <summary>麦克风增益百分比 0-300。</summary>
    public int MicGainPercent { get; set; } = 100;
    /// <summary>麦克风降噪开关。</summary>
    public bool MicNoiseSuppression { get; set; } = false;
    /// <summary>true=合并单音轨，false=系统/麦克风双音轨分离。</summary>
    public bool MergeAudioTracks { get; set; } = true;

    // ===== 捕获源 =====
    /// <summary>捕获模式：fullscreen / monitor / window。</summary>
    public string CaptureMode { get; set; } = "fullscreen";
    /// <summary>指定窗口捕获时的窗口标题（支持 * 通配）。</summary>
    public string TargetWindowTitle { get; set; } = "";

    // ===== 导出 =====
    /// <summary>成品导出目录。为空则默认：{Videos}\ReplayTool。</summary>
    public string ExportDir { get; set; } = "";
    /// <summary>文件名模板，支持 {timestamp} {process} {resolution} 占位符。</summary>
    public string FilenameTemplate { get; set; } = "Replay_{timestamp}_{process}_{resolution}";

    // ===== 快捷键 =====
    public HotkeyConfig SaveReplayHotkey { get; set; } = HotkeyConfig.Default(HotkeyKind.SaveReplay);
    public HotkeyConfig ManualRecordHotkey { get; set; } = HotkeyConfig.Default(HotkeyKind.ManualRecord);
    public HotkeyConfig ScreenshotHotkey { get; set; } = HotkeyConfig.Default(HotkeyKind.Screenshot);
    public HotkeyConfig OpenPanelHotkey { get; set; } = HotkeyConfig.Default(HotkeyKind.OpenPanel);

    // ===== 行为 =====
    /// <summary>开机自启动。</summary>
    public bool RunAtStartup { get; set; } = true;
    /// <summary>启动后立即开始环形缓存录制。</summary>
    public bool StartRecordingOnLaunch { get; set; } = true;
    /// <summary>显示覆盖指示器。</summary>
    public bool ShowOverlayIndicator { get; set; } = true;
    /// <summary>笔记本电池模式下自动暂停回放。</summary>
    public bool BatterySaveMode { get; set; } = false;

    /// <summary>
    /// 应用命名默认规则前的文件路径。返回临时缓存目录（自动创建）。
    /// </summary>
    public string ResolveTempDir()
    {
        var dir = string.IsNullOrWhiteSpace(TempCacheDir)
            ? Path.Combine(Path.GetTempPath(), "ReplayTool")
            : TempCacheDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 解析导出目录（自动创建）。默认 {Videos}\ReplayTool。
    /// </summary>
    public string ResolveExportDir()
    {
        if (!string.IsNullOrWhiteSpace(ExportDir))
        {
            Directory.CreateDirectory(ExportDir);
            return ExportDir;
        }
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var dir = Path.Combine(videos, "ReplayTool");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
