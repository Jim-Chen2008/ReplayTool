using System.Runtime.InteropServices;
using System.Text;

namespace ReplayTool.Core.Native;

/// <summary>当前显示器信息（分辨率与刷新率检测）。</summary>
public static class DisplayInfo
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    public static int PrimaryWidth => GetSystemMetrics(SM_CXSCREEN);
    public static int PrimaryHeight => GetSystemMetrics(SM_CYSCREEN);

    /// <summary>主显示器刷新率（Hz），查询失败返回 60。</summary>
    public static int PrimaryRefreshRate
    {
        get
        {
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(null, 0xFFFFFFFF /* ENUM_CURRENT_SETTINGS */, ref mode))
            {
                if (mode.dmDisplayFrequency > 20 && mode.dmDisplayFrequency <= 1000)
                    return (int)mode.dmDisplayFrequency;
            }
            return 60;
        }
    }

    /// <summary>返回 "1920x1080" 形式的主屏分辨率描述。</summary>
    public static string PrimaryResolutionString => $"{PrimaryWidth}x{PrimaryHeight}";
}
