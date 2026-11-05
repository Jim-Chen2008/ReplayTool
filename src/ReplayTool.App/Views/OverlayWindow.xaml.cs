using System;
using System.Windows;
using System.Windows.Media;
using ReplayTool.Core.Models;
using ReplayTool.Core.Services;

namespace ReplayTool.App.Views;

/// <summary>
/// 覆盖指示器：屏幕右下角的小圆点，实时反映录制状态。
/// 绿色=录制中，红色=正在保存导出，橙色=暂停，灰色=停止。
/// 非交互（IsHitTestVisible=false），不干扰游戏操作。
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionAtCorner();
    }

    private void PositionAtCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    public void SetStatus(ReplayStatus status)
    {
        var color = status switch
        {
            ReplayStatus.Recording => Color.FromRgb(46, 204, 113),   // 绿
            ReplayStatus.Saving => Color.FromRgb(231, 76, 60),       // 红
            ReplayStatus.Paused => Color.FromRgb(243, 156, 18),      // 橙
            _ => Color.FromRgb(150, 150, 150),                       // 灰
        };
        Dot.Fill = new SolidColorBrush(color);
    }
}
