// ============================================================
// TimelineSnapshot.cs - 时间轴的一份完整快照
// ============================================================
//
// 【它是干什么的】
//   编辑器（EditorView）和外界的"存档/还原"都在这一层交换数据。
//
// 【为什么要单独定义它，而不是直接让编辑器用工作台的存档类型】
//   编辑器是独立的一块（理论上能脱离工作台单独跑）。
//   如果让它直接依赖 Workbench.Project.ClipPart，
//   以后工作台改了存档格式，编辑器就得跟着改 —— 耦合就长出来了。
//   中间隔一层中立的数据结构，两边各自演进，互不牵连。
//
//   映射在 StepFineCutView 里做（它是工作台这一侧的页面）。
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut;

/// <summary>
/// 时间轴的完整快照：所有块 + 视图状态。
/// 存下来再还原，能回到"一模一样"的样子（含手工边界、音量、淡入淡出、缩放）。
/// </summary>
public sealed class TimelineSnapshot
{
    /// <summary>所有块</summary>
    public List<TimelineClip> Clips { get; set; } = new();

    /// <summary>播放线位置（秒）</summary>
    public double PlayheadSec { get; set; }

    /// <summary>时间轴视口起点（秒）</summary>
    public double ViewStartSec { get; set; }

    /// <summary>时间轴缩放（秒/像素）</summary>
    public double SecPerPixel { get; set; } = 0.05;

    /// <summary>复制一个块（避免快照和界面共享同一个对象）</summary>
    public static TimelineClip Clone(TimelineClip c) => new()
    {
        Id = c.Id,
        SourcePath = c.SourcePath,
        SourceName = c.SourceName,
        SourceStartSec = c.SourceStartSec,
        SourceEndSec = c.SourceEndSec,
        SourceTotalSec = c.SourceTotalSec,
        TimelineStartSec = c.TimelineStartSec,
        VolumeDb = c.VolumeDb,
        FadeInSec = c.FadeInSec,
        FadeOutSec = c.FadeOutSec,
        IsSelected = c.IsSelected,
    };
}
