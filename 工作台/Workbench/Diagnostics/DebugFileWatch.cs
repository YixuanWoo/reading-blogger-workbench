// ============================================================
// DebugFileWatch.cs - 调试模式下的"文件发生了什么"记录器
// ============================================================
//
// 【为什么用监听，而不是在每个写盘的地方加一行日志】
//   工作台写文件的地方散在十几个文件里（project.json / 各 stages / 渲染产物 /
//   复盘 / 稿子 / Agent 任务包……）。挨个加日志，加漏一个就永远不知道那件事发生过。
//   直接用系统的文件监听盯着目录 —— **谁写的、写的是哪个文件**，一个都跑不掉。
//
// 【盯哪几个目录】
//   ① 项目根（递归）    —— 项目里的一切（档案、stages、素材、渲染产物）
//   ② 配置目录          —— config.json / inspiration.json（%APPDATA%\翊瑄的工作台）
//
// 【为什么要限流】
//   渲染一批镜头会在几秒里产生成百上千条事件。全写进日志，真正想看的那几条
//   就被淹了。所以：每超过一批就折叠成一行"（省略 N 条）"，主次分明。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace LiteCut.Workbench.Diagnostics;

/// <summary>
/// 监视目录变化并写进调试日志。开着调试模式时才工作。
/// </summary>
public sealed class DebugFileWatch : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();

    /// <summary>被折叠掉的事件数（每攒够一批就报一次）。</summary>
    private int _suppressed;
    private DateTime _lastFlush = DateTime.Now;

    /// <summary>一秒钟最多记几条 —— 超了就折叠。</summary>
    private const int PerSecondCap = 40;
    private int _thisSecond;
    private int _currentSecond = -1;

    /// <summary>盯上这些目录。传进来不存在的目录会被忽略（不报错）。</summary>
    public void Watch(params string?[] dirs)
    {
        foreach (var d in dirs)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            if (!Directory.Exists(d)) continue;
            if (IsAlreadyWatched(d!)) continue;

            try
            {
                var w = new FileSystemWatcher(d!)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };

                w.Created += (_, e) => Report("file+", e.FullPath, "");
                w.Changed += (_, e) => Report("file~", e.FullPath, "");
                w.Deleted += (_, e) => Report("file-", e.FullPath, "");
                w.Renamed += (_, e) => Report("file>", e.FullPath, "← " + e.OldFullPath);
                w.Error += (_, e) => DebugLog.Write("file!", "文件监听的缓冲区溢出了（变化太密集）",
                                                    new { dir = d, error = e.GetException()?.Message });

                w.EnableRaisingEvents = true;
                _watchers.Add(w);

                DebugLog.Event("开始监视目录", new { dir = d });
            }
            catch (Exception ex)
            {
                DebugLog.Error("监视目录失败：" + d, ex);
            }
        }
    }

    private bool IsAlreadyWatched(string dir)
    {
        foreach (var w in _watchers)
            if (string.Equals(w.Path, dir, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Report(string kind, string path, string extra)
    {
        // 自己写的调试日志别再回灌自己（否则一条日志引发一条日志，越滚越多）
        try
        {
            string self = DebugLog.FilePath;
            if (self.Length > 0 && string.Equals(path, self, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch { }

        lock (_gate)
        {
            int sec = DateTime.Now.Second;
            if (sec != _currentSecond) { _currentSecond = sec; _thisSecond = 0; }

            if (_thisSecond >= PerSecondCap)
            {
                _suppressed++;
                // 每 5 秒或者攒够 200 条，报一次"省略了多少"
                if (_suppressed >= 200 || (DateTime.Now - _lastFlush).TotalSeconds >= 5)
                {
                    DebugLog.Write("file…", $"（变化太密集，已省略 {_suppressed} 条文件事件）");
                    _suppressed = 0;
                    _lastFlush = DateTime.Now;
                }
                return;
            }

            _thisSecond++;
        }

        DebugLog.Write(kind, path, extra.Length > 0 ? new { from = extra } : null);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }
}
