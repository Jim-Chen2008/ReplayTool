using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Application = System.Windows.Application;
using ReplayTool.App.Views;
using ReplayTool.Core.Models;
using ReplayTool.Core.Services;

namespace ReplayTool.App;

/// <summary>WPF 应用入口：单实例、装配服务、接线事件。</summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private ReplayEngine? _engine;
    private TrayIconService? _tray;
    private HotkeyService? _hotkeys;
    private OverlayWindow? _overlay;
    private ToastNotifier? _toaster;
    private StreamWriter? _logWriter;

    public static App CurrentApp => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 单实例 ----
        _mutex = new Mutex(true, "ReplayTool.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        InitLog();

        _engine = new ReplayEngine();
        _toaster = new ToastNotifier();
        WireEngineEvents();

        // ---- 崩溃恢复（同步执行，必须在 Ring.Start() 清理遗留片段之前完成） ----
        if (e.Args.Contains("--recover"))
            RecoverAsync().GetAwaiter().GetResult();

        // ---- 热键（必须在 UI 线程） ----
        _hotkeys = new HotkeyService(_engine);
        _hotkeys.RegisterAll();

        // ---- 托盘 ----
        _tray = new TrayIconService(this);
        _tray.Initialize(_engine);

        // ---- 覆盖指示器 ----
        if (_engine.Settings.Settings.ShowOverlayIndicator)
            ShowOverlay();

        // ---- 启动录制 ----
        if (_engine.Settings.Settings.StartRecordingOnLaunch)
            _engine.Start();

        Log($"应用启动完成。FFmpeg: {_engine.Encoders.FfmpegPath}");

        // 自动启动参数时隐藏首次引导提示
        if (!e.Args.Contains("--autostart"))
        {
            _toaster.Show("即时回放已就绪", "按 Alt+F10 保存最近的回放画面");
        }
    }

    private void WireEngineEvents()
    {
        _engine!.Log += (_, msg) => Log(msg);
        _engine.Toast += (_, msg) =>
        {
            if (msg == "__OPEN_PANEL__")
            {
                _tray?.OpenSettings();
                return;
            }
            Dispatcher.BeginInvoke(() => _toaster?.Show("提示", msg));
        };
        _engine.ReplaySaved += (_, path) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _toaster?.Show("回放视频已保存", path);
                _tray?.ShowBalloon("回放视频已保存", path);
            });
        };
        _engine.ScreenshotSaved += (_, path) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _toaster?.Show("截图已保存", path);
                _tray?.ShowBalloon("截图已保存", Path.GetFileName(path));
            });
        };
        _engine.StatusChanged += (_, status) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _overlay?.SetStatus(status);
                _tray?.UpdateRecordingState(status);
            });
        };
        _engine.ManualRecordingChanged += (_, isRecording) =>
        {
            Dispatcher.BeginInvoke(() => _tray?.UpdateManualRecordingState(isRecording));
        };
    }

    /// <summary>设置变更后重新注册全局热键，返回冲突列表。</summary>
    public IReadOnlyList<(HotkeyKind, string)> ReRegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService(_engine!);
        return _hotkeys.RegisterAll();
    }

    public void ShowOverlay()
    {
        if (_overlay == null)
        {
            _overlay = new OverlayWindow();
            _overlay.Show();
            _overlay.SetStatus(_engine?.Ring.Status ?? ReplayStatus.Stopped);
        }
    }

    public void HideOverlay()
    {
        _overlay?.Close();
        _overlay = null;
    }

    /// <summary>引擎实例（供托盘/设置/图库访问）。</summary>
    public ReplayEngine? Engine => _engine;

    private async Task RecoverAsync()
    {
        try
        {
            var paths = await _engine!.Recovery.RecoverFromCrashAsync();
            if (paths.Count > 0)
                _toaster?.Show("已恢复崩溃前的回放", paths[0]);
        }
        catch { /* ignore */ }
    }

    private void InitLog()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReplayTool");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "log.txt");
            _logWriter = new StreamWriter(path, append: true) { AutoFlush = true };
            _logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ==== 应用启动 ====");
        }
        catch { /* 日志失败不影响运行 */ }
    }

    private void Log(string msg)
    {
        try
        {
            _logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
        catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _hotkeys?.Dispose();
            _engine?.Dispose();
            _tray?.Dispose();
            _toaster?.CloseAll();
            _overlay?.Close();
            _logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] ==== 应用退出 ====");
            _logWriter?.Dispose();
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
