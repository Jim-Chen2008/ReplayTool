using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ReplayTool.App.Views;

/// <summary>
/// 轻量提示弹窗：右下角悬浮、自动消失、不抢焦点、不出现于任务栏。
/// 支持多提示堆叠。
/// </summary>
public class ToastNotifier
{
    private readonly List<Window> _active = new();
    private readonly object _lock = new();

    public void Show(string title, string message)
    {
        var win = BuildWindow(title, message);
        lock (_lock) _active.Add(win);
        win.Closed += (_, _) => { lock (_lock) _active.Remove(win); };
        PositionAtBottomRight(win);
        win.Show();
        win.Opacity = 0;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        win.BeginAnimation(Window.OpacityProperty, anim);
    }

    private static Window BuildWindow(string title, string message)
    {
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb((byte)235, (byte)37, (byte)37, (byte)38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb((byte)120, (byte)120, (byte)120, (byte)120)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 380,
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new Ellipse
        {
            Width = 10, Height = 10, Margin = new Thickness(0, 4, 10, 0),
            Fill = new SolidColorBrush(Color.FromRgb(46, 204, 113)),
        });
        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(232, 232, 232)),
            Margin = new Thickness(0, 0, 0, 3),
        });
        textStack.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(154, 154, 154)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 330,
        });
        stack.Children.Add(textStack);
        root.Child = stack;

        var win = new Window
        {
            Content = root,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false,
        };

        // 3.5 秒后自动淡出关闭
        win.Loaded += (_, _) =>
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.5),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                fade.Completed += (_, _) => win.Close();
                win.BeginAnimation(Window.OpacityProperty, fade);
            };
            timer.Start();
        };
        return win;
    }

    private void PositionAtBottomRight(Window win)
    {
        var area = SystemParameters.WorkArea;
        double top = area.Bottom - win.ActualHeight - 16;
        double left = area.Right - win.ActualWidth - 16;
        win.Left = left;
        win.Top = top;
    }

    public void CloseAll()
    {
        List<Window> copy;
        lock (_lock) copy = _active.ToList();
        foreach (var w in copy)
        {
            try { w.Close(); } catch { /* ignore */ }
        }
    }
}
