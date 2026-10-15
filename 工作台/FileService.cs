// ============================================================
// FileService.cs - 文件服务（打开、导出、素材库管理）
//
// 职责：
//   1. 弹出"打开文件"对话框，返回选中的路径
//   2. 弹出"导出"对话框，返回要保存的路径
//   3. 管理素材库文件夹：列出所有 m4a 素材（按时间倒序）
//
// 素材库 = iPhone 上传的音频自动落盘的地方。
// 对应你的 Python 习惯：相当于一个"文件操作模块"。
// ============================================================

using System;                       // 基础类型
using System.Collections.Generic;   // List<T>
using System.IO;                    // 文件/目录操作
using System.Text;                  // Encoding.UTF8（写"已移除映射"清单用）
using Microsoft.Win32;              // WPF 的打开/保存对话框

namespace LiteCut;

/// <summary>
/// 素材库中的一个条目（列表里显示用）。
/// 注意：WPF 绑定只能绑属性（{ get; set; }），不能绑字段，
///       所以这里全部写成属性形式。
/// </summary>
public class LibraryItem
{
    /// <summary>完整文件路径</summary>
    public string FilePath { get; set; } = "";

    /// <summary>文件名（带扩展名）</summary>
    public string FileName { get; set; } = "";

    /// <summary>文件大小（字节）</summary>
    public long SizeBytes { get; set; }

    /// <summary>最后修改时间</summary>
    public DateTime ModifiedTime { get; set; }

    /// <summary>文件大小的友好显示（KB/MB），给 XAML 绑定用</summary>
    public string SizeText
    {
        get
        {
            if (SizeBytes >= 1024 * 1024) return (SizeBytes / 1024.0 / 1024).ToString("0.0") + " MB";
            return (SizeBytes / 1024.0).ToString("0") + " KB";
        }
    }

    /// <summary>修改时间的友好显示（MM-dd HH:mm），给 XAML 绑定用</summary>
    public string TimeText => ModifiedTime.ToString("MM-dd HH:mm");
}

/// <summary>
/// 文件服务：对话框 + 素材库。
/// </summary>
public class FileService
{
    private readonly string _libraryDir; // 素材库文件夹路径

    /// <summary>
    /// "已移除映射"清单文件：素材库删除条目 = 把路径记到这里。
    /// 磁盘上的文件永远不动；列表扫描时把清单里的路径过滤掉。
    /// 放素材库文件夹内，跟素材库走。
    /// </summary>
    private readonly string _removedListPath;

    /// <summary>素材库文件夹路径（外部读取用）</summary>
    public string LibraryDir => _libraryDir;

    /// <summary>素材库内容变化（新文件上传进来时触发）</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>构造：指定素材库位置，并确保文件夹存在</summary>
    public FileService(string libraryDir)
    {
        _libraryDir = libraryDir;
        Utils.EnsureDirectory(libraryDir);
        _removedListPath = Path.Combine(_libraryDir, ".litecut_removed.txt");
    }

    // ============================================================
    // 打开 / 导出对话框
    // ============================================================

    /// <summary>
    /// 弹出"打开音频"对话框。
    /// 返回选中的完整路径；用户取消则返回 null。
    /// </summary>
    public string? OpenFileDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "打开音频（m4a）",
            Filter = "音频文件 (*.m4a;*.mp3;*.wav)|*.m4a;*.mp3;*.wav|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// 弹出"导出"对话框。
    /// 默认文件名带时间戳（例如 口播音频_正式版_20260919.m4a），
    /// 避免覆盖原文件。
    /// </summary>
    /// <param name="suggestedName">建议的文件名（不含扩展名）</param>
    public string? SaveFileDialog(string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出为 m4a",
            Filter = "m4a 音频 (*.m4a)|*.m4a",
            FileName = suggestedName + "_" + Utils.CreateTimestamp() + ".m4a",
            DefaultExt = ".m4a",
            AddExtension = true
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // ============================================================
    // 素材库
    // ============================================================

    /// <summary>
    /// 列出素材库里的所有 m4a 文件，按修改时间倒序（最新在前）。
    /// 没有素材时返回空列表（不会报错）。
    /// </summary>
    public List<LibraryItem> ListLibrary()
    {
        var items = new List<LibraryItem>();

        if (!Directory.Exists(_libraryDir)) return items;

        // 已移除映射的路径（删除条目时记下，列表不再显示，但磁盘文件不动）
        var removed = LoadRemovedList();

        // 只认 .m4a（也顺手认 .mp3/.wav，宽容一点）
        string[] files = Directory.GetFiles(_libraryDir, "*.*");
        foreach (string file in files)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".m4a" && ext != ".mp3" && ext != ".wav") continue;
            if (removed.Contains(file)) continue;  // 映射已被移除 → 不显示

            var info = new FileInfo(file);
            items.Add(new LibraryItem
            {
                FilePath = file,
                FileName = info.Name,
                SizeBytes = info.Length,
                ModifiedTime = info.LastWriteTime
            });
        }

        // 最新在前
        items.Sort((a, b) => b.ModifiedTime.CompareTo(a.ModifiedTime));
        return items;
    }

    /// <summary>通知"素材库变了"（上传服务器新文件入库后调用）</summary>
    public void NotifyLibraryChanged()
    {
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    // "映射"管理：素材库条目 = 指向磁盘文件的引用（类似达芬奇）
    // 删除条目 = 只删映射（记入清单），磁盘文件永远不动、不进回收站。
    // ============================================================

    /// <summary>读取"已移除映射"清单（每行一个完整路径）</summary>
    private HashSet<string> LoadRemovedList()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_removedListPath))
            {
                foreach (var line in File.ReadAllLines(_removedListPath, Encoding.UTF8))
                {
                    var t = line.Trim();
                    if (t.Length > 0) set.Add(t);
                }
            }
        }
        catch { /* 清单读坏了就当空清单，不影响使用 */ }
        return set;
    }

    /// <summary>
    /// 删除一条映射：把路径记入清单，素材库列表不再显示它。
    /// 不碰磁盘文件。
    /// </summary>
    public void RemoveMapping(string filePath)
    {
        try
        {
            var set = LoadRemovedList();
            set.Add(filePath);
            File.WriteAllLines(_removedListPath, set, Encoding.UTF8);
            // 清单文件设为隐藏，别在资源管理器里碍眼
            try { File.SetAttributes(_removedListPath, FileAttributes.Hidden); } catch { }
        }
        catch { /* 清单写失败不致命，列表会恢复显示 */ }
    }
}
