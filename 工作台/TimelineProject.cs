// ============================================================
// TimelineProject.cs - 时间轴项目（管理所有块）
//
// 这个文件是时间轴的"数据中心"：
//   - 存所有块的列表
//   - 提供添加/删除/切开/移动块的方法
//   - 计算总时长
//   - 处理时间轴位置 ↔ 块内位置的换算
//
// 对应你的 Python 习惯：相当于一个"项目管理器"，
// 所有对时间轴的修改都通过这里，不直接改 UI。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteCut;

/// <summary>
/// 时间轴项目：管理所有素材块。
/// </summary>
public class TimelineProject
{
    /// <summary>所有块（按时间轴位置排序）</summary>
    public List<TimelineClip> Clips { get; } = new List<TimelineClip>();

    /// <summary>当前播放位置（秒，时间轴坐标系）</summary>
    public double PlayheadSec { get; set; }

    /// <summary>事件：时间轴内容变了（界面要刷新）</summary>
    public event EventHandler? Changed;

    /// <summary>事件：选中变了（按钮状态要刷新）</summary>
    public event EventHandler? SelectionChanged;

    // ============================================================
    // 基础操作
    // ============================================================

    /// <summary>总时长（从第一块起点到最后一块终点）</summary>
    public double TotalDurationSec
    {
        get
        {
            if (Clips.Count == 0) return 0;
            double minStart = Clips.Min(c => c.TimelineStartSec);
            double maxEnd = Clips.Max(c => c.TimelineEndSec);
            return maxEnd - minStart;
        }
    }

    /// <summary>第一块的起点（时间轴原点）</summary>
    public double StartSec => Clips.Count == 0 ? 0 : Clips.Min(c => c.TimelineStartSec);

    /// <summary>触发 Changed 事件（界面刷新）</summary>
    public void NotifyChanged()
    {
        Clips.Sort((a, b) => a.TimelineStartSec.CompareTo(b.TimelineStartSec));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>触发 SelectionChanged 事件</summary>
    public void NotifySelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    // 块的增删改
    // ============================================================

    /// <summary>添加一块到时间轴（默认放在末尾或指定位置）</summary>
    public void AddClip(TimelineClip clip)
    {
        Clips.Add(clip);
        NotifyChanged();
    }

    /// <summary>删除选中的块（从时间轴移除，不删源文件）</summary>
    public void RemoveSelectedClips()
    {
        var selected = Clips.Where(c => c.IsSelected).ToList();
        foreach (var clip in selected)
        {
            Clips.Remove(clip);
        }
        NotifyChanged();
    }

    /// <summary>
    /// 在指定位置（秒）把选中的块切成两块。
    /// 只能切"跨越这个位置的块"。
    /// </summary>
    public void SplitClipAt(double timelineSec)
    {
        var clipsToSplit = Clips.Where(c => c.IsSelected
            && timelineSec > c.TimelineStartSec
            && timelineSec < c.TimelineEndSec).ToList();

        foreach (var clip in clipsToSplit)
        {
            double offsetInClip = timelineSec - clip.TimelineStartSec; // 从块起点偏移多少秒
            double sourceSplitSec = clip.SourceStartSec + offsetInClip; // 对应源素材的位置
            double originalSourceEnd = clip.SourceEndSec; // 先存原来的源素材终点

            // 第一块：原来的起点 → 切开位置
            clip.SourceEndSec = sourceSplitSec;

            // 第二块：切开位置 → 原来的终点
            var newClip = new TimelineClip
            {
                SourcePath = clip.SourcePath,
                SourceName = clip.SourceName,
                SourceStartSec = sourceSplitSec,
                SourceEndSec = originalSourceEnd,  // 用原来的终点，不是已改的值
                SourceTotalSec = clip.SourceTotalSec,  // 继承源素材总时长（裁切边界用）
                TimelineStartSec = timelineSec,
                VolumeDb = clip.VolumeDb,
                FadeInSec = 0, // 切开的新块不需要淡入
                FadeOutSec = clip.FadeOutSec,
                IsSelected = true
            };
            Clips.Add(newClip);
        }

        NotifyChanged();
    }

    /// <summary>移动块到新的时间轴位置</summary>
    public void MoveClip(TimelineClip clip, double newTimelineStartSec)
    {
        clip.TimelineStartSec = Math.Max(0, newTimelineStartSec);
        NotifyChanged();
    }

    // ============================================================
    // 选中操作
    // ============================================================

    /// <summary>框选：选中在 [startSec, endSec) 范围内的块</summary>
    public void SelectClipsInRange(double startSec, double endSec, bool additive = false)
    {
        if (!additive)
        {
            foreach (var c in Clips) c.IsSelected = false;
        }

        foreach (var c in Clips)
        {
            // 块和选区有交集就算选中
            if (c.TimelineStartSec < endSec && c.TimelineEndSec > startSec)
            {
                c.IsSelected = true;
            }
        }
        NotifySelectionChanged();
    }

    /// <summary>点选：选中指定块（Ctrl = 追加）</summary>
    public void SelectClip(TimelineClip clip, bool additive)
    {
        if (!additive)
        {
            foreach (var c in Clips) c.IsSelected = false;
        }
        clip.IsSelected = true;
        NotifySelectionChanged();
    }

    /// <summary>全选</summary>
    public void SelectAll()
    {
        foreach (var c in Clips) c.IsSelected = true;
        NotifySelectionChanged();
    }

    /// <summary>取消所有选中</summary>
    public void ClearSelection()
    {
        foreach (var c in Clips) c.IsSelected = false;
        NotifySelectionChanged();
    }

    /// <summary>当前有几块被选中</summary>
    public int SelectedCount => Clips.Count(c => c.IsSelected);

    /// <summary>当前选中的块（拖动时"整组一起走"就是这个集合）</summary>
    public List<TimelineClip> GetSelectedClips() => Clips.Where(c => c.IsSelected).ToList();

    /// <summary>
    /// 点中一个块（块中间）时的选中规则。
    ///
    /// 【为什么单独抽成一个有名字的方法】
    ///   这里曾经有个 bug：只要没按 Ctrl，点中一个"已经在选中集合里"的块，
    ///   就把选中集清成只剩它一个 —— 于是框选多块之后一拖，只有鼠标下那块会动，
    ///   看着就像"多选拖动没实现"。规则本身只有三行，但它是多选拖动能不能用的关键，
    ///   所以抽出来、命名、并配断言测试，避免以后重构时又被悄悄改坏。
    ///
    /// 规则：
    ///   Ctrl+点         → 切换这一块的选中状态（加选 / 减选）
    ///   点中没选中的块  → 选中集收成它自己
    ///   点中已选中的块  → 保持整组不变（这样整组能一起拖走）
    /// </summary>
    public void ApplyBodyClickSelection(TimelineClip clicked, bool additive)
    {
        if (additive)
        {
            clicked.IsSelected = !clicked.IsSelected;
        }
        else if (!clicked.IsSelected)
        {
            foreach (var c in Clips) c.IsSelected = false;
            clicked.IsSelected = true;
        }

        NotifySelectionChanged();
    }

    /// <summary>
    /// 松开鼠标时的收口规则：这次按下如果**没有产生任何拖动**，
    /// 就把选中集收成点中的那一块。
    ///
    /// 和上一条配合起来才是完整手感（各家剪辑软件 / 文件管理器都一样）：
    ///   点一下 → 只选它
    ///   按住拖 → 选中集里所有块一起走
    /// 少了这一条，Ctrl+A 或框选之后就没法用"点一下"把选中集缩回一块了。
    /// </summary>
    public void ApplyBodyClickRelease(TimelineClip? clicked, bool additive, bool moved)
    {
        if (clicked == null || additive || moved) return;
        if (SelectedCount <= 1) return;

        foreach (var c in Clips) c.IsSelected = false;
        clicked.IsSelected = true;
        NotifySelectionChanged();
    }

    // ============================================================
    // 命中测试
    // ============================================================

    /// <summary>
    /// 按时间轴位置找块。
    /// 返回 (命中的块, 命中的边缘类型)——边缘用来判断是移动还是裁切。
    /// edgeToleranceSec：边缘判定容差（多少秒内算边缘）。
    ///
    /// 【两遍扫描，为什么不能一遍扫完】
    ///   旧实现从尾部逐块"先查边缘、再查身体"。A、B 首尾相接时，点在 A 的
    ///   右侧（还没过缝），只要离缝不到容差，就会先命中 B 的左边缘 ——
    ///   选中的是右边的块，看起来像"点击位置偏右"。裁切目标也跟着错。
    ///
    ///   新规则：
    ///   ① 点在某个块身上 → 边缘只认这个块自己的边缘（够得到容差才给），
    ///      否则就是拖块身。缝左侧永远抓到 A 的右边缘，缝右侧抓 B 的左边缘。
    ///   ② 点在空白处 → 在所有块的边缘里取"离点击位置最近"的那个
    ///      （这样窄缝里的空白也能抓到两侧的边缘，方便微调）。
    ///   ③ 身体命中从尾部向前找 —— 历史工程里可能存着重叠的块，
    ///      画在"上面"（后画）的块优先命中，重叠区选中的是上层块。
    /// </summary>
    public (TimelineClip? clip, string edge) HitTest(double timelineSec, double edgeToleranceSec)
    {
        // ---- 第一遍：点在谁身上 ----
        for (int i = Clips.Count - 1; i >= 0; i--)
        {
            var clip = Clips[i];
            if (timelineSec <= clip.TimelineStartSec || timelineSec >= clip.TimelineEndSec)
                continue;

            // 点在这个块身上：边缘只认它自己的
            if (timelineSec - clip.TimelineStartSec < edgeToleranceSec)
                return (clip, "left");
            if (clip.TimelineEndSec - timelineSec < edgeToleranceSec)
                return (clip, "right");
            return (clip, "body");
        }

        // ---- 第二遍：点在空白处，找最近的边缘 ----
        TimelineClip? bestClip = null;
        string bestEdge = "";
        double bestDist = double.MaxValue;
        for (int i = Clips.Count - 1; i >= 0; i--)
        {
            var clip = Clips[i];

            double dl = Math.Abs(timelineSec - clip.TimelineStartSec);
            if (dl < edgeToleranceSec && dl < bestDist)
            {
                bestDist = dl; bestClip = clip; bestEdge = "left";
            }

            double dr = Math.Abs(timelineSec - clip.TimelineEndSec);
            if (dr < edgeToleranceSec && dr < bestDist)
            {
                bestDist = dr; bestClip = clip; bestEdge = "right";
            }
        }

        return (bestClip, bestEdge);
    }

    /// <summary>某个素材是否被时间轴上的块引用（素材库删除时用）</summary>
    public bool IsSourceUsed(string sourcePath)
    {
        return Clips.Any(c => string.Equals(c.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    // 源素材相邻块（裁切边界用）
    // ============================================================

    /// <summary>
    /// 在源素材坐标里，找紧贴在本块"前面"的块：
    /// 同一个源文件、且它的源出点 &lt;= 本块源入点，取其中最接近本块的那一个。
    /// 这是剪刀切出来的"前一段"。裁切本块左边缘时，源入点不能低于它的源出点
    /// （否则就把前一段的内容抢过来了）。
    /// </summary>
    public TimelineClip? PrevClipInSource(TimelineClip clip)
    {
        TimelineClip? best = null;
        foreach (var c in Clips)
        {
            if (c == clip) continue;
            if (!string.Equals(c.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.SourceEndSec <= clip.SourceStartSec + 0.0001)
            {
                if (best == null || c.SourceEndSec > best.SourceEndSec) best = c;
            }
        }
        return best;
    }

    /// <summary>
    /// 在源素材坐标里，找紧贴在本块"后面"的块：
    /// 同一个源文件、且它的源入点 &gt;= 本块源出点，取其中最接近本块的那一个。
    /// 这是剪刀切出来的"后一段"。裁切本块右边缘时，源出点不能超过它的源入点
    /// （否则就把后一段的内容抢过来了）。
    /// </summary>
    public TimelineClip? NextClipInSource(TimelineClip clip)
    {
        TimelineClip? best = null;
        foreach (var c in Clips)
        {
            if (c == clip) continue;
            if (!string.Equals(c.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.SourceStartSec >= clip.SourceEndSec - 0.0001)
            {
                if (best == null || c.SourceStartSec < best.SourceStartSec) best = c;
            }
        }
        return best;
    }
}
