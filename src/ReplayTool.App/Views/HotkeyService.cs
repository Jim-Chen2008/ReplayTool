using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using ReplayTool.Core.Models;
using ReplayTool.Core.Native;
using ReplayTool.Core.Services;

namespace ReplayTool.App.Views;

/// <summary>
/// 全局热键服务（App 层，WPF 依赖）。用 HwndSource 隐藏窗口接收 WM_HOTKEY，
/// 转发给 ReplayEngine.HandleHotkey。设置变更后可重新注册。
/// </summary>
public class HotkeyService : IDisposable
{
    private readonly ReplayEngine _engine;
    private HwndSource? _source;
    private readonly Dictionary<int, HotkeyKind> _registrations = new();

    public event EventHandler<string>? RegistrationFailed;

    public HotkeyService(ReplayEngine engine)
    {
        _engine = engine;
    }

    public void Initialize()
    {
        if (_source != null) return;
        var parameters = new HwndSourceParameters("ReplayToolHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = -200,
            PositionY = -200,
            WindowStyle = 0x800000,     // WS_POPUP
            ExtendedWindowStyle = 0x80000, // WS_EX_TOOLWINDOW
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registrations.TryGetValue(id, out var kind))
            {
                _engine.HandleHotkey(kind);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>按当前设置注册全部快捷键，返回失败列表。</summary>
    public IReadOnlyList<(HotkeyKind, string)> RegisterAll()
    {
        var failures = new List<(HotkeyKind, string)>();
        Initialize();
        if (_source == null) return failures;

        var s = _engine.Settings.Settings;
        var items = new[]
        {
            (HotkeyKind.SaveReplay, s.SaveReplayHotkey),
            (HotkeyKind.ManualRecord, s.ManualRecordHotkey),
            (HotkeyKind.Screenshot, s.ScreenshotHotkey),
            (HotkeyKind.OpenPanel, s.OpenPanelHotkey),
        };

        foreach (var (kind, cfg) in items)
        {
            if (!cfg.Enabled) continue;
            if (!Register(kind, cfg))
            {
                failures.Add((kind, $"快捷键 {cfg.ToDisplayString()} 被其他程序占用"));
            }
        }
        if (failures.Count > 0)
            RegistrationFailed?.Invoke(this, string.Join("\n", failures.Select(f => f.Item2)));
        return failures;
    }

    private bool Register(HotkeyKind kind, HotkeyConfig cfg)
    {
        if (_source == null) return false;
        Unregister(kind);
        int id = (int)kind + 1;
        bool ok = Win32.RegisterHotKey(
            _source.Handle, id, cfg.Modifiers | Win32.MOD_NOREPEAT, (uint)cfg.VirtualKey);
        if (ok) _registrations[id] = kind;
        return ok;
    }

    private void Unregister(HotkeyKind kind)
    {
        if (_source == null) return;
        int id = (int)kind + 1;
        Win32.UnregisterHotKey(_source.Handle, id);
        _registrations.Remove(id);
    }

    public void Dispose()
    {
        if (_source != null)
        {
            foreach (var id in _registrations.Keys.ToList())
                Win32.UnregisterHotKey(_source.Handle, id);
            _registrations.Clear();
            _source.Dispose();
            _source = null;
        }
    }
}
