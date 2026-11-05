using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using ReplayTool.Core.Services;

namespace ReplayTool.App.Views;

/// <summary>图库条目（MVVM 数据对象）。</summary>
public class GalleryItem
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Type { get; set; } = "Video";
    public string TypeText => Type == "Image" ? "图片" : "视频";
    public string SizeText { get; set; } = "";
    public string ModifiedText { get; set; } = "";

    public static GalleryItem FromFile(string path)
    {
        var fi = new FileInfo(path);
        var isImage = fi.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                      || fi.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                      || fi.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        return new GalleryItem
        {
            FileName = fi.Name,
            FullPath = fi.FullName,
            Type = isImage ? "Image" : "Video",
            SizeText = FormatSize(fi.Length),
            ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}

/// <summary>图库浏览器：展示导出目录下的回放视频与截图。</summary>
public partial class GalleryWindow : Window
{
    private readonly ReplayEngine _engine;
    private readonly ObservableCollection<GalleryItem> _items = new();

    public GalleryWindow(ReplayEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        ItemList.ItemsSource = _items;
        Refresh();
    }

    private void Refresh()
    {
        _items.Clear();
        try
        {
            var dir = _engine.Settings.Settings.ResolveExportDir();
            if (!Directory.Exists(dir)) return;

            var files = Directory.GetFiles(dir)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".mp4" or ".png" or ".jpg" or ".jpeg" or ".mov";
                })
                .OrderByDescending(f => File.GetLastWriteTime(f));

            foreach (var f in files)
                _items.Add(GalleryItem.FromFile(f));
        }
        catch (Exception ex)
        {
            CountText.Text = $"读取失败: {ex.Message}";
            return;
        }
        CountText.Text = $"共 {_items.Count} 个捕获（{_engine.Settings.Settings.ResolveExportDir()}）";
    }

    private List<GalleryItem> Selected => ItemList.SelectedItems.Cast<GalleryItem>().ToList();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var first = Selected.FirstOrDefault();
        if (first != null)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{first.FullPath}\"");
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var first = Selected.FirstOrDefault();
        if (first != null)
            System.Windows.Clipboard.SetText(first.FullPath);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        if (MessageBox.Show($"确定删除选中的 {items.Count} 个文件？", "删除捕获",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var item in items)
        {
            try { File.Delete(item.FullPath); } catch { /* ignore */ }
        }
        Refresh();
    }
}
