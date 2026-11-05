using System.Runtime.InteropServices;
using System.Text;

namespace ReplayTool.Core.Native;

/// <summary>
/// Win32 P/Invoke 声明集合。仅包含本项目所需的最小 API 子集。
/// </summary>
public static class Win32
{
    public const int WM_HOTKEY = 0x0312;
    public const int HWND_MESSAGE = -3;
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetModuleFileName(uint hModule, [Out] StringBuilder lpFilename, uint nSize);

    [DllImport("kernel32.dll")]
    public static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(
        IntPtr hProcess, uint ProcessInformationClass, out PROCESS_POWER_THROTTLING_STATE ProcessInformation, uint ProcessInformationSize);

    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>是否正在以管理员权限运行。</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以管理员权限重新启动自身。</summary>
    public static void RestartAsAdmin(string? arguments = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "ReplayTool.exe",
            UseShellExecute = true,
            Verb = "runas",
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>获取当前前台窗口所属进程的可执行文件名（用于回放文件命名）。</summary>
    public static string? GetForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return null;
        var name = GetProcessName(pid);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>根据进程 ID 获取可执行文件名（去掉扩展名）。</summary>
    public static string? GetProcessName(uint pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
