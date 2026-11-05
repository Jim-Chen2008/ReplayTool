using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Microsoft.Win32;
using ReplayTool.Core.Models;
using ReplayTool.Core.Services;

namespace ReplayTool.App.Views;

/// <summary>下拉框条目包装（带显示标签与隐藏值，WPF 绑定安全）。</summary>
public class CItem
{
    public string Label { get; }
    public object? Value { get; }
    public CItem(string label, object? value) { Label = label; Value = value; }
    public override string ToString() => Label;
}

/// <summary>参数设置面板：对标 N 卡 GE 面板的可视化参数调节。</summary>
public partial class SettingsWindow : Window
{
    private readonly ReplayEngine _engine;
    private readonly App _app;
    private HotkeyKind? _capturingKind;

    public SettingsWindow(ReplayEngine engine, App app)
    {
        InitializeComponent();
        _engine = engine;
        _app = app;

        BuildCombos();
        LoadFromSettings();
        WireHotkeyButtons();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static CItem Item(string label, object? value) => new(label, value);

    private void BuildCombos()
    {
        DurationCombo.ItemsSource = new[]
        {
            Item("15 秒", 15), Item("30 秒", 30), Item("45 秒", 45), Item("1 分钟", 60),
            Item("2 分钟", 120), Item("3 分钟", 180), Item("5 分钟", 300),
        };

        ResolutionCombo.ItemsSource = new[]
        {
            Item("原始分辨率", "0x0"), Item("720P", "1280x720"), Item("1080P", "1920x1080"),
            Item("2K", "2560x1440"), Item("4K", "3840x2160"),
        };

        FramerateCombo.ItemsSource = new[]
        {
            Item("跟随显示器刷新率", 0), Item("30 FPS", 30), Item("60 FPS", 60),
            Item("120 FPS", 120), Item("144 FPS", 144),
        };

        BitrateCombo.ItemsSource = new[]
        {
            Item("低", "low"), Item("中", "medium"), Item("高", "high"), Item("自定义", "custom"),
        };

        EncoderCombo.ItemsSource = new[]
        {
            Item("自动（硬件优先）", "auto"), Item("H.264", "h264"), Item("H.265 / HEVC", "hevc"),
        };

        AudioTrackCombo.ItemsSource = new[]
        {
            Item("合并为单音轨", true), Item("双音轨分离（系统/麦克风）", false),
        };

        BitrateCombo.SelectionChanged += (_, _) =>
            CustomBitrateBox.IsEnabled = BitrateCombo.SelectedItem is CItem { Value: "custom" };
    }

    private void LoadFromSettings()
    {
        var s = _engine.Settings.Settings;

        SelectValue(DurationCombo, s.ReplayDurationSeconds, 2);
        TempDirBox.Text = s.TempCacheDir;
        MaxCacheBox.Text = s.MaxCacheGb.ToString();
        DiskWarnBox.Text = s.DiskWarningThresholdGb.ToString();

        SelectValue(ResolutionCombo, $"{s.CaptureWidth}x{s.CaptureHeight}", 0);
        SelectValue(FramerateCombo, s.Framerate, 0);
        SelectValue(BitrateCombo, s.BitratePreset, 0);
        SelectValue(EncoderCombo, s.EncoderFormat, 0);
        HdrCheck.IsChecked = s.HdrEnabled;
        SharpnessSlider.Value = s.Sharpness;
        ContrastSlider.Value = s.Contrast;
        ColorEnhanceCheck.IsChecked = s.ColorEnhancement;
        BrightnessCheck.IsChecked = s.BrightnessBoost;

        SystemAudioCheck.IsChecked = s.CaptureSystemAudio;
        MicCheck.IsChecked = s.CaptureMic;
        MicGainSlider.Value = s.MicGainPercent;
        NoiseSuppressionCheck.IsChecked = s.MicNoiseSuppression;
        SelectValue(AudioTrackCombo, s.MergeAudioTracks, 0);

        AutoStartCheck.IsChecked = s.RunAtStartup;
        AutoRecordCheck.IsChecked = s.StartRecordingOnLaunch;
        OverlayCheck.IsChecked = s.ShowOverlayIndicator;
        BatteryCheck.IsChecked = s.BatterySaveMode;

        EncoderInfoText.Text = $"当前编码器：{_engine.Ring.CurrentEncoder?.EncoderName ?? "未启动"}";
        FfmpegInfoText.Text = $"FFmpeg：{_engine.Encoders.FfmpegPath}";
        StatusInfoText.Text = $"录制状态：{_engine.Ring.Status}";

        UpdateHotkeyDisplays();
    }

    /// <summary>按值选中下拉项，未命中则用回退索引。</summary>
    private static void SelectValue(ComboBox combo, object? value, int fallbackIndex)
    {
        if (combo.ItemsSource is System.Collections.IEnumerable source)
        {
            int i = 0;
            foreach (var item in source)
            {
                if (item is CItem c && Equals(c.Value, value))
                {
                    combo.SelectedIndex = i;
                    return;
                }
                i++;
            }
        }
        combo.SelectedIndex = fallbackIndex;
    }

    private void WireHotkeyButtons()
    {
        SaveReplayRebind.Click += (_, _) => BeginCapture(HotkeyKind.SaveReplay);
        ManualRecordRebind.Click += (_, _) => BeginCapture(HotkeyKind.ManualRecord);
        ScreenshotRebind.Click += (_, _) => BeginCapture(HotkeyKind.Screenshot);
        OpenPanelRebind.Click += (_, _) => BeginCapture(HotkeyKind.OpenPanel);

        SaveReplayClear.Click += (_, _) => { _engine.Settings.Settings.SaveReplayHotkey.Enabled = false; UpdateHotkeyDisplays(); };
        ManualRecordClear.Click += (_, _) => { _engine.Settings.Settings.ManualRecordHotkey.Enabled = false; UpdateHotkeyDisplays(); };
        ScreenshotClear.Click += (_, _) => { _engine.Settings.Settings.ScreenshotHotkey.Enabled = false; UpdateHotkeyDisplays(); };
        OpenPanelClear.Click += (_, _) => { _engine.Settings.Settings.OpenPanelHotkey.Enabled = false; UpdateHotkeyDisplays(); };
    }

    private void BeginCapture(HotkeyKind kind)
    {
        _capturingKind = kind;
        CaptureHint.Text = $"请按下新的快捷键（用于：{HotkeyName(kind)}）… 按 Esc 取消";
    }

    private static string HotkeyName(HotkeyKind kind) => kind switch
    {
        HotkeyKind.SaveReplay => "保存回放",
        HotkeyKind.ManualRecord => "手动录制",
        HotkeyKind.Screenshot => "截图",
        HotkeyKind.OpenPanel => "呼出设置",
        _ => "",
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingKind is not HotkeyKind kind) return;

        if (e.Key == Key.Escape)
        {
            _capturingKind = null;
            CaptureHint.Text = "";
            e.Handled = true;
            return;
        }

        // 忽略单独的修饰键
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 0x2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 0x1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 0x4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 0x8;

        int vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk == 0) return;

        var cfg = new HotkeyConfig(modifiers, vk, enabled: true);
        var s = _engine.Settings.Settings;
        switch (kind)
        {
            case HotkeyKind.SaveReplay: s.SaveReplayHotkey = cfg; break;
            case HotkeyKind.ManualRecord: s.ManualRecordHotkey = cfg; break;
            case HotkeyKind.Screenshot: s.ScreenshotHotkey = cfg; break;
            case HotkeyKind.OpenPanel: s.OpenPanelHotkey = cfg; break;
        }
        _capturingKind = null;
        CaptureHint.Text = "";
        UpdateHotkeyDisplays();
        e.Handled = true;
    }

    private void UpdateHotkeyDisplays()
    {
        var s = _engine.Settings.Settings;
        SaveReplayDisplay.Text = Display(s.SaveReplayHotkey);
        ManualRecordDisplay.Text = Display(s.ManualRecordHotkey);
        ScreenshotDisplay.Text = Display(s.ScreenshotHotkey);
        OpenPanelDisplay.Text = Display(s.OpenPanelHotkey);
    }

    private static string Display(HotkeyConfig cfg) =>
        !cfg.Enabled ? "（已禁用）" : cfg.ToDisplayString();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _engine.Settings.Settings;
        s.ReplayDurationSeconds = (int)((CItem)DurationCombo.SelectedItem!).Value!;
        s.TempCacheDir = TempDirBox.Text.Trim();
        s.MaxCacheGb = long.TryParse(MaxCacheBox.Text, out var mc) ? mc : 20;
        s.DiskWarningThresholdGb = long.TryParse(DiskWarnBox.Text, out var dw) ? dw : 5;

        var res = ((CItem)ResolutionCombo.SelectedItem!).Value as string;
        if (!string.IsNullOrEmpty(res) && res.Contains('x'))
        {
            var parts = res.Split('x');
            s.CaptureWidth = int.Parse(parts[0]);
            s.CaptureHeight = int.Parse(parts[1]);
        }
        s.Framerate = (int)((CItem)FramerateCombo.SelectedItem!).Value!;
        s.BitratePreset = (string)((CItem)BitrateCombo.SelectedItem!).Value!;
        if (int.TryParse(CustomBitrateBox.Text, out var cb)) s.CustomBitrateMbps = cb;
        s.EncoderFormat = (string)((CItem)EncoderCombo.SelectedItem!).Value!;
        s.HdrEnabled = HdrCheck.IsChecked == true;
        s.Sharpness = (int)SharpnessSlider.Value;
        s.Contrast = (int)ContrastSlider.Value;
        s.ColorEnhancement = ColorEnhanceCheck.IsChecked == true;
        s.BrightnessBoost = BrightnessCheck.IsChecked == true;

        s.CaptureSystemAudio = SystemAudioCheck.IsChecked == true;
        s.CaptureMic = MicCheck.IsChecked == true;
        s.MicGainPercent = (int)MicGainSlider.Value;
        s.MicNoiseSuppression = NoiseSuppressionCheck.IsChecked == true;
        s.MergeAudioTracks = (bool)((CItem)AudioTrackCombo.SelectedItem!).Value!;

        s.RunAtStartup = AutoStartCheck.IsChecked == true;
        s.StartRecordingOnLaunch = AutoRecordCheck.IsChecked == true;
        s.ShowOverlayIndicator = OverlayCheck.IsChecked == true;
        s.BatterySaveMode = BatteryCheck.IsChecked == true;

        _engine.Settings.Save();
        _engine.SyncRunAtStartup();

        // 应用：重启录制以套用新参数
        _engine.RestartRecording();
        // 重新注册热键
        var failures = _app.ReRegisterHotkeys();
        // 覆盖指示器开关
        if (s.ShowOverlayIndicator) _app.ShowOverlay();
        else _app.HideOverlay();

        SaveResultText.Text = failures.Count > 0
            ? $"已保存，但有 {failures.Count} 个快捷键冲突"
            : "设置已保存并生效";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BrowseTemp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择临时缓存目录",
            InitialDirectory = !string.IsNullOrEmpty(TempDirBox.Text) && Directory.Exists(TempDirBox.Text)
                ? TempDirBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer),
        };
        if (dialog.ShowDialog(this) == true)
            TempDirBox.Text = dialog.FolderName;
    }

    private void OpenExportDir_Click(object sender, RoutedEventArgs e)
        => System.Diagnostics.Process.Start("explorer.exe", $"\"{_engine.Settings.Settings.ResolveExportDir()}\"");

    private void OpenTempDir_Click(object sender, RoutedEventArgs e)
        => System.Diagnostics.Process.Start("explorer.exe", $"\"{_engine.Settings.Settings.ResolveTempDir()}\"");
}
