// ============================================================
// AudioPatchLayout.cs - "就地重拼"的施工图（纯逻辑）
//
// 【为什么不是"重新读那一块"】
//   最直觉的增量做法是"只把改动的那一块从源文件里重新读一遍"。实测下来
//   这条路走不通：NAudio 在 m4a 上用 CurrentTime 定位，落点是随机的 ——
//   同一个文件在不同位置上会偏 −768、−512、+1998、−2039 个采样
//   （±42 毫秒，方向还不固定）。重新读出来的那一块会整体挪几十毫秒，
//   和前后的块之间就会出现"丢一段 / 重一段"，接缝直接听得出问题。
//
// 【那怎么办 —— 干脆不读文件】
//   时间轴上每一块的样本，本质都是：
//         源素材样本 × 增益 × 淡入淡出包络
//   而上一份拼好的成品里已经存着它了。所以只要新状态是旧的"子集 + 换个包络"，
//   就能从旧成品直接算出新值：
//         新值 = 旧值 × (新包络 / 旧包络)
//   完全不碰源文件 → 不涉及定位 → 没有精度问题，而且几乎不花时间。
//
//   这条路覆盖了日常绝大多数编辑：
//     挪块（源区间没变）         ✓
//     裁短（新区间是旧的子集）    ✓
//     调音量 / 改淡入淡出        ✓
//     整条平移 / 删块            ✓
//   只有"要更多源素材"的操作（把边缘往外拉、切开、新增块、换源文件）
//   才需要重新读文件 —— 那时退回整条重拼。
//
// 【怎么保证算出来是对的】
//   唯一的数学风险是"旧包络为 0 → 除不回来"。包络里会为 0 的只有两种情形：
//     ① 有淡入的第一个样本（i × 增益 / 淡入时长，i = 0）
//     ② 旧增益本身就是 0（整块静音，乘什么进去都是 0）
//   这两种都只要看一个点/一个字段就能判定可行性 —— 不用逐样本试探。
//   判定不过就整条重拼。
//
// 【为什么每个段都要给一个"活"】
//   施工图里不仅列出"要重算的段"，也列出"一点没动、整片照搬的段"。
//   因为新成品的缓冲区是新分配的，没动的段也得搬过去，否则就丢了。
//   照搬用整片内存拷贝（比逐样本重算快一个数量级），所以代价可以忽略。
//   按新计划顺序给出这些活，段与段万一重叠时"后写的覆盖先写的"，
//   和整条重拼的行为完全一致。
//
//   这里全是纯算术，不碰音频库，可以脱离界面和设备直接断言。
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut.Core.Timeline;

/// <summary>缓冲区里的一段采样区间 [Start, End)</summary>
public readonly struct SampleSpan
{
    public SampleSpan(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; }
    public int End { get; }
    public int Length => End - Start;

    /// <summary>两段区间是否真的重叠（首尾相接不算重叠）</summary>
    public bool Overlaps(SampleSpan other) => Start < other.End && other.Start < End;

    public override string ToString() => $"[{Start}, {End})";
}

/// <summary>
/// 一段要干的活：把旧计划第 <see cref="OldIndex"/> 段的样本（从它的第
/// <see cref="Offset"/> 个起，共 <see cref="Length"/> 个）弄到新计划第
/// <see cref="NewIndex"/> 段该在的位置上。
///
/// <see cref="IsCopy"/> = true 表示这一段一点没动，整片照搬就行；
/// false 表示增益/淡化/长度变了，得按包络比重新算一遍。
/// </summary>
public readonly struct SegmentJob
{
    public SegmentJob(int newIndex, int oldIndex, int offset, int length, bool isCopy)
    {
        NewIndex = newIndex;
        OldIndex = oldIndex;
        Offset = offset;
        Length = length;
        IsCopy = isCopy;
    }

    /// <summary>新计划里的第几段</summary>
    public int NewIndex { get; }

    /// <summary>旧计划里的第几段（同一块身份）</summary>
    public int OldIndex { get; }

    /// <summary>从旧段在第几个样本开始取（源区间往后缩了多少）</summary>
    public int Offset { get; }

    /// <summary>取多少个样本（新采样率下）</summary>
    public int Length { get; }

    /// <summary>true = 整片照搬；false = 按包络比重算</summary>
    public bool IsCopy { get; }

    public override string ToString()
    {
        string verb = IsCopy ? "照搬" : "重算";
        return $"新#{NewIndex} ← {verb} 旧#{OldIndex}[{Offset}..{Offset + Length})";
    }
}

/// <summary>
/// 就地重拼的施工图。<see cref="CanPatch"/> 为 false 时 <see cref="Reason"/>
/// 说明为什么改不了（用于日志/调试）。
/// </summary>
public sealed class AudioPatchPlan
{
    private static readonly SegmentJob[] None = Array.Empty<SegmentJob>();

    private AudioPatchPlan(bool canPatch, string reason, SegmentJob[] jobs, bool nothingToDo)
    {
        CanPatch = canPatch;
        Reason = reason;
        Jobs = jobs;
        NothingToDo = nothingToDo;
    }

    /// <summary>能不能就地重拼</summary>
    public bool CanPatch { get; }

    /// <summary>不能就地重拼的原因（能时为空）</summary>
    public string Reason { get; }

    /// <summary>要干的活，按新计划顺序排列（含"照搬"和"重算"两种）</summary>
    public IReadOnlyList<SegmentJob> Jobs { get; }

    /// <summary>
    /// 新成品会和旧成品一模一样 → 连新数组都不用分配，直接复用旧的。
    ///
    /// 【为什么不是"所有段都是照搬"就够了】
    ///   删掉一块之后，剩下的段确实全是"照搬"，但被删那块占的位置在新成品里
    ///   必须变成静音。只判"全是照搬"就会把旧数组原样交出去 —— 被删的那块
    ///   声音还留在里面。所以还得加一个前提：旧的段一个都没少。
    /// </summary>
    public bool NothingToDo { get; }

    internal static AudioPatchPlan Patch(SegmentJob[] jobs, bool nothingToDo)
        => new(true, "", jobs, nothingToDo);

    internal static AudioPatchPlan Reject(string reason) => new(false, reason, None, false);
}

/// <summary>就地重拼的判定与包络计算</summary>
public static class AudioPatchLayout
{
    /// <summary>秒比较用的容差</summary>
    private const double Eps = 1e-6;

    /// <summary>某一段在缓冲区里占的那片采样</summary>
    public static SampleSpan Footprint(AudioRenderPlan plan, AudioSegment seg)
    {
        int start = (int)((seg.TimelineStartSec - plan.StartSec) * plan.SampleRate);
        int length = OutputLength(plan, seg);
        return new SampleSpan(start, start + length);
    }

    /// <summary>两段是否完全一样（任何一个字段不同都算"改动过"）</summary>
    public static bool SameSegment(AudioSegment a, AudioSegment b)
    {
        return a.Id == b.Id
            && string.Equals(a.SourcePath, b.SourcePath, StringComparison.Ordinal)
            && a.SourceStartSec == b.SourceStartSec
            && a.SourceEndSec == b.SourceEndSec
            && a.TimelineStartSec == b.TimelineStartSec
            && a.Gain == b.Gain
            && a.FadeInSec == b.FadeInSec
            && a.FadeOutSec == b.FadeOutSec;
    }

    /// <summary>
    /// 某段输出里第 i 个样本的包络值（增益 × 淡入 × 淡出）。
    /// 必须和渲染器里施加包络的顺序保持一致，否则推导出来的值会有极小偏差。
    /// </summary>
    public static double EnvelopeAt(int i, int outLength, int fadeInSamples, int fadeOutSamples, double gain)
    {
        double v = gain;
        if (fadeInSamples > 0 && i < fadeInSamples) v *= (double)i / fadeInSamples;
        if (fadeOutSamples > 0 && i >= outLength - fadeOutSamples) v *= (double)(outLength - i) / fadeOutSamples;
        return v;
    }

    /// <summary>一段在缓冲区里会输出多少个样本</summary>
    public static int OutputLength(AudioRenderPlan plan, AudioSegment seg)
    {
        int n = (int)(seg.SourceLengthSec * plan.SampleRate);
        return n > 0 ? n : 0;
    }

    /// <summary>
    /// 比对两份计划，算出"每一段该怎么搬到新成品里"。
    ///
    /// 判定不过（要更多源素材、换了源文件、有全新的块）就返回 CanPatch=false，
    /// 让调用方整条重拼 —— 慢一点，但一定正确。
    /// </summary>
    public static AudioPatchPlan Analyze(AudioRenderPlan oldPlan, AudioRenderPlan newPlan)
    {
        if (oldPlan == null) throw new ArgumentNullException(nameof(oldPlan));
        if (newPlan == null) throw new ArgumentNullException(nameof(newPlan));

        if (oldPlan.SampleRate != newPlan.SampleRate) return AudioPatchPlan.Reject("采样率变了");
        if (newPlan.Segments.Count == 0) return AudioPatchPlan.Reject("新时间轴是空的");
        if (oldPlan.Segments.Count == 0) return AudioPatchPlan.Reject("旧成品是空的");

        // 旧计划按身份（Id）索引：时间轴按位置排序，挪一下下标就全乱，
        // 只有身份才是稳定的。
        var oldById = new Dictionary<Guid, int>(oldPlan.Segments.Count);
        for (int i = 0; i < oldPlan.Segments.Count; i++) oldById[oldPlan.Segments[i].Id] = i;

        var jobs = new List<SegmentJob>(newPlan.Segments.Count);

        for (int i = 0; i < newPlan.Segments.Count; i++)
        {
            var seg = newPlan.Segments[i];

            if (!oldById.TryGetValue(seg.Id, out int oldIndex))
                return AudioPatchPlan.Reject("出现了全新的块（要读源素材）");

            var oldSeg = oldPlan.Segments[oldIndex];
            int length = OutputLength(newPlan, seg);

            // 一点没动 → 整片照搬（这类段是绝大多数）
            if (SameSegment(oldSeg, seg))
            {
                if (length > 0) jobs.Add(new SegmentJob(i, oldIndex, 0, length, true));
                continue;
            }

            if (!string.Equals(oldSeg.SourcePath, seg.SourcePath, StringComparison.Ordinal))
                return AudioPatchPlan.Reject("换了源文件");

            // 新的源区间必须是旧源区间的子集（只需要更少的素材，才推导得出来）
            if (seg.SourceStartSec < oldSeg.SourceStartSec - Eps)
                return AudioPatchPlan.Reject("源入点往前扩了（要读源素材）");
            if (seg.SourceEndSec > oldSeg.SourceEndSec + Eps)
                return AudioPatchPlan.Reject("源出点往后扩了（要读源素材）");

            if (length <= 0) continue;                       // 空块，不用管

            // 源入点往后缩了多少（样本数）。
            //
            // 【为什么是"两个截断值相减"，而不是"先减再乘"】
            //   渲染器把源区间起点算成 (long)(秒 × 采样率)，也就是对每个起点
            //   各自做一次截断。所以偏移量必须用同样两次截断的结果相减 ——
            //   用 (新起点 − 旧起点) × 采样率 会差 1 个采样（浮点下
            //   24.4×48000 = 1171199.99999999993，截断成 1171199；
            //   而 0.4×48000 截断成 19199，对不上）。差 1 个采样就是 20 微秒的
            //   错位，听不出来，但会导致"增量重拼"和"整条重拼"结果不一致 ——
            //   这种"看着没事、对不上账"的状态最容易被后来的改动放大成 bug。
            long newFrom = (long)(seg.SourceStartSec * newPlan.SampleRate);
            long oldFrom = (long)(oldSeg.SourceStartSec * newPlan.SampleRate);
            long offsetL = newFrom - oldFrom;
            int offset = (int)offsetL;
            int oldLength = OutputLength(oldPlan, oldSeg);

            if (offsetL < 0) return AudioPatchPlan.Reject("源入点偏移算出来是负的");
            if (offset + length > oldLength) return AudioPatchPlan.Reject("取的范围超出了旧成品");

            // 旧包络为 0 就除不回来。会为 0 的有两处：
            //   ① 旧增益是 0（整块静音）
            //   ② 有淡入时的第 0 个样本
            //      （淡入系数 = i / 淡入样本数，i=0 就是 0；
            //        淡出系数的最小值是 1/淡出样本数，永远为正）
            if (oldSeg.Gain == 0)
                return AudioPatchPlan.Reject("旧成品的增益是 0，恢复不出来");

            int oldFadeIn = (int)(oldSeg.FadeInSec * oldPlan.SampleRate);
            int newFadeIn = (int)(seg.FadeInSec * newPlan.SampleRate);
            if (offset == 0 && oldFadeIn > 0 && newFadeIn == 0)
                return AudioPatchPlan.Reject("旧成品第 0 个样本是被淡入压成 0 的，恢复不出来");

            jobs.Add(new SegmentJob(i, oldIndex, offset, length, false));
        }

        // 全部照搬 + 旧的段一个不少 → 新成品和旧成品一模一样，可以原样复用。
        // （少了后半句，"删掉一块"就会被误判成"什么都没动"）
        bool allCopies = true;
        for (int i = 0; i < jobs.Count; i++)
            if (!jobs[i].IsCopy) { allCopies = false; break; }

        bool nothingToDo = allCopies && jobs.Count == oldPlan.Segments.Count;

        return AudioPatchPlan.Patch(jobs.ToArray(), nothingToDo);
    }
}
