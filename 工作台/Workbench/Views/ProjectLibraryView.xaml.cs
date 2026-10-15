// ============================================================
// ProjectLibraryView.xaml.cs - 项目自己的素材库
// ============================================================
//
// 【为什么素材库要跟着项目走，而不是全局一个】
//   全局素材库的问题：做到第 10 期时，里面横着 10 期的录音，
//   找这一期的那条要翻半天；而且删素材时会担心碰到别的期。
//   跟着项目走之后，"这一期的素材"永远是干净的一小堆。
//
// 【导入是"复制进来"，不是"引用外部路径"】
//   复制进来才叫"这个项目的素材"：以后你整理硬盘、
//   把源目录挪走，项目也不会因为找不到文件而哑掉。
//   代价是占一份磁盘 —— 对这个量级完全值得。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench.Views;

public partial class ProjectLibraryView : UserControl, INavPage
{
    private static readonly string[] MediaExts =
        { ".m4a", ".mp3", ".wav", ".flac", ".aac", ".wma", ".mp4", ".mov", ".mkv", ".webm", ".avi" };

    private readonly List<MediaRow> _rows = new();

    public ProjectLibraryView()
    {
        InitializeComponent();
        ListFiles.ItemsSource = _rows;
        Loaded += (s, e) => Refresh();
    }

    public void OnEnter() => Refresh();

    public void OnLeave() { }

    private WorkbenchWindow? Shell => WorkbenchWindow.FindOwner(this);

    private ProjectSession Session => WorkbenchWindow.Session;

    // ========================================================
    // 刷新
    // ========================================================

    public void Refresh()
    {
        _rows.Clear();

        var proj = Session.Current;
        if (proj == null)
        {
            TxtDir.Text = "没有打开任何项目";
            UpdateEmpty("素材库是每个项目独立的 —— 先从「进行中」打开一个项目。");
            return;
        }

        string mediaDir = proj.MediaDir;
        TxtDir.Text = mediaDir;

        try
        {
            Directory.CreateDirectory(mediaDir);
            foreach (string f in Directory.GetFiles(mediaDir)
                         .Where(f => MediaExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                         .OrderByDescending(File.GetLastWriteTime))
            {
                var fi = new FileInfo(f);
                _rows.Add(new MediaRow
                {
                    Name = fi.Name,
                    FullPath = f,
                    Meta = fi.Length > 1024 * 1024
                        ? $"{fi.Length / 1024.0 / 1024.0:F1} MB · {fi.LastWriteTime:MM-dd HH:mm}"
                        : $"{fi.Length / 1024.0:F0} KB · {fi.LastWriteTime:MM-dd HH:mm}",
                });
            }
        }
        catch (Exception ex)
        {
            UpdateEmpty("读不了这个目录：" + ex.Message);
            return;
        }

        ListFiles.ItemsSource = null;
        ListFiles.ItemsSource = _rows;

        TxtHint.Text = _rows.Count > 0
            ? "双击 = 拿它去做粗剪；右键 = 设为粗剪源 / 在资源管理器里显示 / 移出项目"
            : "";

        UpdateEmpty(_rows.Count == 0
            ? "这个项目还没有素材。\n\n点右上角「＋ 导入素材」把这一期的录音放进来。"
            : null);
    }

    private void UpdateEmpty(string? message)
    {
        bool empty = message != null;
        TxtEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListFiles.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) TxtEmpty.Text = message;
    }

    // ========================================================
    // 操作
    // ========================================================

    private MediaRow? RowOf(object sender)
        => sender is FrameworkElement fe && fe.DataContext is MediaRow r ? r : null;

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var proj = Session.Current;
        if (proj == null) { Shell?.Status("先打开一个项目，再导入素材"); return; }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入素材到本项目",
            Multiselect = true,
            Filter = "音视频素材|*.m4a;*.mp3;*.wav;*.flac;*.aac;*.wma;*.mp4;*.mov;*.mkv;*.webm|所有文件|*.*",
        };
        if (!string.IsNullOrWhiteSpace(WorkbenchWindow.State.Config.LastMediaDir) &&
            Directory.Exists(WorkbenchWindow.State.Config.LastMediaDir))
            dlg.InitialDirectory = WorkbenchWindow.State.Config.LastMediaDir;

        if (dlg.ShowDialog() != true) return;

        string dir = proj.MediaDir;
        Directory.CreateDirectory(dir);

        int copied = 0;
        var failed = new List<string>();
        foreach (string src in dlg.FileNames)
        {
            try
            {
                string dest = Path.Combine(dir, Path.GetFileName(src));

                // 同名但确实是另一个文件 → 加时间戳，不覆盖已有的
                if (File.Exists(dest) &&
                    !string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    dest = Path.Combine(dir,
                        Path.GetFileNameWithoutExtension(src) + "_" +
                        DateTime.Now.ToString("HHmmss") +
                        Path.GetExtension(src));
                }

                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest),
                                  StringComparison.OrdinalIgnoreCase))
                    continue;   // 本来就是项目里的文件，跳过

                File.Copy(src, dest, overwrite: false);
                copied++;
            }
            catch (Exception ex)
            {
                failed.Add(Path.GetFileName(src) + "：" + ex.Message);
            }
        }

        WorkbenchWindow.State.Config.LastMediaDir =
            Path.GetDirectoryName(dlg.FileNames.FirstOrDefault() ?? "") ?? "";
        WorkbenchWindow.State.SaveConfig();

        Shell?.MarkDirty();                 // 素材变了 → 项目有未保存改动

        Refresh();
        Shell?.Status(failed.Count == 0
            ? $"已导入 {copied} 个素材到本项目"
            : $"导入 {copied} 个，{failed.Count} 个失败：{failed[0]}");
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var proj = Session.Current;
        if (proj == null) return;

        try
        {
            Directory.CreateDirectory(proj.MediaDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = proj.MediaDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开文件夹：" + ex.Message, "素材库",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ListFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => SetAsCutSource(RowOf(sender));

    private void MenuSetSource_Click(object sender, RoutedEventArgs e)
        => SetAsCutSource(ListFiles.SelectedItem as MediaRow);

    /// <summary>
    /// 把素材交给第①步（粗剪）当源文件。
    /// 走的是既有的静态槽位 —— 页面是重建的，直接调方法拿不到新实例。
    /// </summary>
    private void SetAsCutSource(MediaRow? row)
    {
        if (row == null) return;

        Step1CutView.PendingFile = row.FullPath;
        Shell?.GoTo(NavTree.Ids.StepCut);
        Shell?.Status($"已把「{row.Name}」交给粗剪");
    }

    private void MenuReveal_Click(object sender, RoutedEventArgs e)
    {
        var row = ListFiles.SelectedItem as MediaRow;
        if (row == null) return;

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{row.FullPath}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开资源管理器：" + ex.Message, "素材库",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        var row = ListFiles.SelectedItem as MediaRow;
        var proj = Session.Current;
        if (row == null || proj == null) return;

        var ok = MessageBox.Show(
            $"把「{row.Name}」移出本项目？\n\n" +
            "文件会被挪到 media\\_已移除 里，不会真的删掉。",
            "移出素材", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            string trash = Path.Combine(proj.MediaDir, "_已移除");
            Directory.CreateDirectory(trash);
            string dest = Path.Combine(trash, Path.GetFileName(row.FullPath));
            if (File.Exists(dest))
                dest = Path.Combine(trash,
                    Path.GetFileNameWithoutExtension(dest) + "_" +
                    DateTime.Now.ToString("HHmmss") + Path.GetExtension(dest));

            File.Move(row.FullPath, dest);
            Shell?.MarkDirty();
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("移出失败：" + ex.Message, "素材库",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ========================================================
    // 列表行（只用于绑定显示）
    // ========================================================

    public sealed class MediaRow
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Meta { get; set; } = "";
    }
}
