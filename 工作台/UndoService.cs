// ============================================================
// UndoService.cs - 撤销重做服务（快照模式）
//
// 原理：每次修改时间轴之前，把所有块的完整数据存一份快照。
// 撤销 = 弹出上一个快照恢复。
// 重做 = 把撤销掉的快照再弹回来。
//
// 这种方式简单可靠，对轻量工具足够用。
// 缺点：每次操作存一份完整数据，占内存。但你的素材量不大，没问题。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteCut;

/// <summary>
/// 撤销重做服务。
/// </summary>
public class UndoService
{
    /// <summary>
    /// 一个快照：所有块的完整数据。
    /// </summary>
    private class Snapshot
    {
        public List<TimelineClip> Clips { get; set; } = new();
    }

    // 历史栈（最近的在栈顶）
    private Stack<Snapshot> _undoStack = new();
    // 重做栈
    private Stack<Snapshot> _redoStack = new();

    // 最大历史长度（防止内存爆）
    private const int MaxHistory = 50;

    /// <summary>时间轴项目（用来恢复快照）</summary>
    private readonly TimelineProject _project;

    /// <summary>构造：绑定时间轴项目</summary>
    public UndoService(TimelineProject project)
    {
        _project = project;
    }

    /// <summary>能不能撤销</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>能不能重做</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// 清空全部历史。项目被整体替换时调用（比如工作台交接铺入新块）——
    /// 不清的话，用户按一下撤销会回到交接前的旧时间轴，状态就乱了。
    /// </summary>
    public void Reset()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    /// <summary>
    /// 【关键】在修改时间轴之前调用：拍下当前状态的快照。
    /// 修改完成后不需要调用什么，快照已经在栈里了。
    /// </summary>
    public void CaptureSnapshot()
    {
        var snapshot = TakeSnapshot();
        _undoStack.Push(snapshot);

        // 超过最大长度，扔掉最早的
        while (_undoStack.Count > MaxHistory)
        {
            // 从栈底删（Stack 不直接支持，转列表再操作）
            var list = _undoStack.ToList();
            list.RemoveAt(0);
            _undoStack = new Stack<Snapshot>(Enumerable.Reverse(list));
        }

        // 新操作进来，重做栈清空
        _redoStack.Clear();
    }

    /// <summary>撤销</summary>
    public void Undo()
    {
        if (_undoStack.Count == 0) return;

        // 先存当前状态到重做栈
        _redoStack.Push(TakeSnapshot());

        // 弹出上一个快照恢复
        var snapshot = _undoStack.Pop();
        RestoreSnapshot(snapshot);
    }

    /// <summary>重做</summary>
    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        // 先存当前状态到撤销栈
        _undoStack.Push(TakeSnapshot());

        // 弹出重做快照恢复
        var snapshot = _redoStack.Pop();
        RestoreSnapshot(snapshot);
    }

    // ============================================================
    // 内部：拍快照和恢复快照
    // ============================================================

    /// <summary>拍当前所有块的快照</summary>
    private Snapshot TakeSnapshot()
    {
        var clips = _project.Clips.Select(c => new TimelineClip
        {
            Id = c.Id,                 // 身份也要保住：撤销回来还是"同一个块"，不是新块
            SourcePath = c.SourcePath,
            SourceName = c.SourceName,
            SourceStartSec = c.SourceStartSec,
            SourceEndSec = c.SourceEndSec,
            SourceTotalSec = c.SourceTotalSec,
            TimelineStartSec = c.TimelineStartSec,
            VolumeDb = c.VolumeDb,
            FadeInSec = c.FadeInSec,
            FadeOutSec = c.FadeOutSec,
            IsSelected = c.IsSelected
        }).ToList();

        return new Snapshot { Clips = clips };
    }

    /// <summary>恢复快照到时间轴项目</summary>
    private void RestoreSnapshot(Snapshot snapshot)
    {
        _project.Clips.Clear();
        foreach (var clip in snapshot.Clips)
        {
            _project.Clips.Add(clip);
        }
        _project.NotifyChanged();
        _project.NotifySelectionChanged();
    }
}
