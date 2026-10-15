// ============================================================
// TimelineClip.cs - 时间轴上的一个素材块
//
// 这个文件定义"块"的数据结构：
//   一块 = 从某个素材文件里取一段，放在时间轴的某个位置，
//          带自己的音量和淡入淡出。
//
// 对应达芬奇里的"片段（Clip）"概念。
// ============================================================

using System;

namespace LiteCut;

/// <summary>
/// 时间轴上的一个素材块。
/// 注意：这里存的是"引用"，不是音频数据本身——
/// 音频数据在播放/导出时才从源文件读出来。
/// </summary>
public class TimelineClip
{
    // ---------- 身份 ----------
    /// <summary>唯一 ID（用来区分不同块，即使来自同一个素材）</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>源素材文件路径（素材库里的引用）</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>素材文件名（显示用，缓存一下省得每次算）</summary>
    public string SourceName { get; set; } = "";

    // ---------- 裁切范围（从源素材取哪一段） ----------
    /// <summary>从源素材的第几秒开始取（入点）</summary>
    public double SourceStartSec { get; set; }

    /// <summary>取到源素材的第几秒结束（出点）</summary>
    public double SourceEndSec { get; set; }

    /// <summary>源素材文件的总时长（秒）。导入时记录，裁切时源出点不能超过它。</summary>
    public double SourceTotalSec { get; set; }

    // ---------- 时间轴位置 ----------
    /// <summary>这块在时间轴上的起始位置（秒）</summary>
    public double TimelineStartSec { get; set; }

    // ---------- 块级效果 ----------
    /// <summary>这块的音量偏移（dB，0 = 原音量）</summary>
    public double VolumeDb { get; set; } = 0;

    /// <summary>这块的淡入时长（秒，0 = 无淡入）</summary>
    public double FadeInSec { get; set; } = 0;

    /// <summary>这块的淡出时长（秒，0 = 无淡出）</summary>
    public double FadeOutSec { get; set; } = 0;

    // ---------- 计算属性 ----------
    /// <summary>这块在源素材里的时长（秒）</summary>
    public double SourceDurationSec => SourceEndSec - SourceStartSec;

    /// <summary>这块在时间轴上的结束位置（秒）</summary>
    public double TimelineEndSec => TimelineStartSec + SourceDurationSec;

    /// <summary>这块是否被选中（界面用）</summary>
    public bool IsSelected { get; set; }
}
