namespace ReplayTool.Core.Models;

/// <summary>快捷键种类。</summary>
public enum HotkeyKind
{
    SaveReplay,
    ManualRecord,
    Screenshot,
    OpenPanel,
}

/// <summary>
/// 快捷键配置。与 Win32 RegisterHotKey 的修饰键/虚拟键码对应。
/// </summary>
public class HotkeyConfig
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 修饰键位掩码（与 MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN 对应）。
    /// Alt=0x1, Ctrl=0x2, Shift=0x4, Win=0x8。
    /// </summary>
    public uint Modifiers { get; set; }

    /// <summary>Win32 虚拟键码（VK_*）。</summary>
    public int VirtualKey { get; set; }

    public HotkeyConfig() { }

    public HotkeyConfig(uint modifiers, int virtualKey, bool enabled = true)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Enabled = enabled;
    }

    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;

    public static HotkeyConfig Default(HotkeyKind kind) => kind switch
    {
        // Alt+F10（对标 N 卡即时回放保存）
        HotkeyKind.SaveReplay => new HotkeyConfig(MOD_ALT, (int)Keys.VK_F10),
        // Alt+F9（对标 N 卡手动录制）
        HotkeyKind.ManualRecord => new HotkeyConfig(MOD_ALT, (int)Keys.VK_F9),
        // Alt+F1（对标 N 卡截图）
        HotkeyKind.Screenshot => new HotkeyConfig(MOD_ALT, (int)Keys.VK_F1),
        // Alt+F2（唤起设置面板）
        HotkeyKind.OpenPanel => new HotkeyConfig(MOD_ALT, (int)Keys.VK_F2),
        _ => new HotkeyConfig(),
    };

    /// <summary>人类可读描述，如 "Alt+F10"。</summary>
    public string ToDisplayString()
    {
        var s = "";
        if ((Modifiers & MOD_CONTROL) != 0) s += "Ctrl+";
        if ((Modifiers & MOD_ALT) != 0) s += "Alt+";
        if ((Modifiers & MOD_SHIFT) != 0) s += "Shift+";
        if ((Modifiers & MOD_WIN) != 0) s += "Win+";
        if (VirtualKey >= 0x70 && VirtualKey <= 0x7B)
            s += "F" + (VirtualKey - 0x70 + 1);
        else
            s += string.Format("VK(0x{0:X})", VirtualKey);
        return s;
    }
}

/// <summary>Win32 虚拟键常量子集。</summary>
public static class Keys
{
    public const int VK_F1 = 0x70;
    public const int VK_F2 = 0x71;
    public const int VK_F3 = 0x72;
    public const int VK_F4 = 0x73;
    public const int VK_F5 = 0x74;
    public const int VK_F6 = 0x75;
    public const int VK_F7 = 0x76;
    public const int VK_F8 = 0x77;
    public const int VK_F9 = 0x78;
    public const int VK_F10 = 0x79;
    public const int VK_F11 = 0x7A;
    public const int VK_F12 = 0x7B;
}
