using System;
using System.Drawing;
using System.Windows.Forms;
using ReplayTool.Core.Models;
using ReplayTool.Core.Services;

namespace ReplayTool.App.Views;

/// <summary>
/// 系统托盘服务：托盘图标 + 右键菜单 + 状态指示。
/// 基于 WinForms NotifyIcon（WPF 集成，消息泵由 Dispatcher 驱动）。
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly App _app;
    private NotifyIcon? _notifyIcon;
    private SettingsWindow? _settingsWindow;
    private GalleryWindow? _galleryWindow;
    private ToolStripMenuItem? _manualRecordItem;
    private ToolStripMenuItem? _replayItem;

    public TrayIconService(App app)
    {
        _app = app;
    }

    public void Initialize(ReplayEngine engine)
    {
        _manualRecordItem = new ToolStripMenuItem("手动录制（Alt+F9）") { CheckOnClick = false };
        _manualRecordItem.Click += (_, _) => engine.ToggleManualRecording();

        _replayItem = new ToolStripMenuItem("保存回放（Alt+F10）");
        _replayItem.Click += async (_, _) => await engine.SaveReplayAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_replayItem);
        menu.Items.Add(_manualRecordItem);
        menu.Items.Add(new ToolStripMenuItem("保存截图（Alt+F1）", null, async (_, _) =>
        {
            await engine.TakeScreenshotAsync();
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开图库", null, (_, _) => OpenGallery()));
        menu.Items.Add(new ToolStripMenuItem("设置", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => _app.Shutdown()));

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcon.Default,
            Text = "即时回放",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) { /* 右键菜单由 ContextMenuStrip 自动弹出 */ }
        };

        UpdateRecordingState(engine.Ring.Status);
    }

    public void UpdateRecordingState(ReplayStatus status)
    {
        if (_notifyIcon == null) return;
        _notifyIcon.Icon = status switch
        {
            ReplayStatus.Recording => AppIcon.Generate(Color.FromArgb(46, 204, 113)),
            ReplayStatus.Saving => AppIcon.Generate(Color.FromArgb(231, 76, 60)),
            ReplayStatus.Paused => AppIcon.Generate(Color.FromArgb(243, 156, 18)),
            _ => AppIcon.Generate(Color.FromArgb(150, 150, 150)),
        };
        _notifyIcon.Text = status switch
        {
            ReplayStatus.Recording => "即时回放 · 录制中",
            ReplayStatus.Saving => "即时回放 · 正在保存…",
            ReplayStatus.Paused => "即时回放 · 已暂停",
            _ => "即时回放 · 已停止",
        };
    }

    public void UpdateManualRecordingState(bool isRecording)
    {
        if (_manualRecordItem == null) return;
        _manualRecordItem.Checked = isRecording;
        _manualRecordItem.Text = isRecording
            ? "停止手动录制（Alt+F9）"
            : "手动录制（Alt+F9）";
    }

    public void ShowBalloon(string title, string text) =>
        _notifyIcon?.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void OpenSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsVisible)
        {
            _settingsWindow = new SettingsWindow(_app.Engine!, _app);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void OpenGallery()
    {
        if (_galleryWindow == null || !_galleryWindow.IsVisible)
        {
            _galleryWindow = new GalleryWindow(_app.Engine!);
            _galleryWindow.Closed += (_, _) => _galleryWindow = null;
        }
        _galleryWindow.Show();
        _galleryWindow.Activate();
    }

    public void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _settingsWindow?.Close();
        _galleryWindow?.Close();
    }
}
