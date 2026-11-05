using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ReplayTool.App.Views;

/// <summary>运行时生成应用图标（托盘/覆盖指示器），无需携带二进制资源。</summary>
public static class AppIcon
{
    /// <summary>生成 32x32 圆点图标。</summary>
    public static Icon Generate(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 24, 24);
            // 高光
            using var highlight = new SolidBrush(Color.FromArgb(120, 255, 255, 255));
            g.FillEllipse(highlight, 8, 7, 9, 7);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static Icon Default => Generate(Color.FromArgb(61, 139, 253));
}
