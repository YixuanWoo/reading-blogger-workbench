// ============================================================
// FinalScriptBuilder.cs - 把「转写稿 ∩ 剪辑决策」算成成片口播稿
// ============================================================
//
// 【一句话】
//   剪完之后观众到底听到了什么 —— 这是纯算术，一个字节都不需要 AI。
//
// 【为什么非要自己做，而不是让大模型读一遍音频】
//   ① 数据本来就在手边：转写稿有每句话的时间码，精剪每个块存的是
//      "引用源素材第几秒到第几秒"。求个交集就出来了，何必猜。
//   ② 让模型来"听"一遍再写稿，等于把已经算准的东西重新估一遍 ——
//      既慢又会引入幻觉（它会顺手把你没说的话补上）。
//
// 【★ 最容易搞错的一件事：剪掉停顿 ≠ 剪掉话】
//   剪辑剪掉的时间，绝大多数是**停顿、重录、口误、语气词**，不是内容。
//   拿一个真实项目量过（277 句 / 184 块）：剪掉的 228 秒里，151 秒是静音、
//   60 秒是重录，剩下不到 20 秒才是真删了话。
//   所以"句子的时间范围被块边界切到了"**根本不能**说明"这句话被剪断了" ——
//   按那个口径算，61% 的句子都会被误报（实测数据）。
//
//   正确的口径只有一条：**这句被剪掉的时间有多长**。
//   一句 3 秒的话前后各削掉 0.3 秒气口，内容一字不少 —— 不该报；
//   一句 3 秒的话中间挖掉 1.5 秒 —— 那是真少说了一截，该报。
//   阈值 TruncatedGapSec = 0.8 秒（约 3~4 个字），实测误报从 61% 降到 3%。
//
// 【三个还要守住的细节】
//   1. ★ **按"块在成片里的先后"排，不是按源时间**
//      精剪允许挪块（把后面那句挪到前面讲），剪完的语序和原片不一样。
//      按源时间排会得到一篇顺序错误的稿子 —— 而且是最难发现的那种错
//      （读起来通顺，只是画面全配错地方）。
//   2. **跨块的句子合并成一行**：一句被几块共同保留，这些块在成片里是
//      首尾相接的（中间剪掉的是停顿），合并后时间取"第一块的头→最后一块的尾"，
//      连续且准确。实测 53 句跨块，没有一句在成片里被别的内容隔开。
//   3. **有词级时间码就按词拼文本**（能精确到字）；只有句级时间码时只能整句进出 ——
//      这时"句内被挖掉一段"没法定位到具体哪几个字，就按上面的口径报个秒数。
//
// 【没有转写稿怎么办】
//   降级成"文案稿 + 按字数占比摊时间"（老办法），但必须把 IsEstimate 标成 true，
//   界面上写明"估算" —— 不能让用户以为这是精确值。
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>
/// 成片时间轴上的一块：成片第几秒到第几秒，对应源素材第几秒到第几秒。
/// （跟 WorkbenchState.DecisionTimeline() 的元组一一对应。）
/// </summary>
public readonly struct DecisionSpan
{
    public double TimelineStartSec { get; init; }
    public double TimelineEndSec { get; init; }
    public double SourceStartSec { get; init; }
    public double SourceEndSec { get; init; }

    public double DurationSec => TimelineEndSec - TimelineStartSec;
}

/// <summary>
/// 源素材上一段"剪掉了也不影响内容"的区间 —— 停顿、语气词、重说/重录的那一遍。
/// 有了它就能把"剪掉 2 秒口误"和"剪掉 2 秒正片内容"区分开（前者不该报）。
/// 拿不到（没跑粗剪）也没关系，那就退化成按缺口秒数保守判断。
/// </summary>
public readonly struct HarmlessSpan
{
    public double SourceStartSec { get; init; }
    public double SourceEndSec { get; init; }
}

/// <summary>
/// 长句阈值（秒）。超过这个时长的一句，一个镜头撑到底会很呆 ——
/// Agent 提示词里会专门要求"动效做得复杂些，让句子内容动起来"。
/// </summary>
public static class FinalScriptBuilder
{
    /// <summary>长句阈值（秒）。</summary>
    public const double LongLineSec = 12.0;

    /// <summary>
    /// 一句被保留到多少才要。低于这个比例说明这句基本被剪光了，
    /// 硬留着只会变成"一行配不清画面的半句话"，宁可不要（宁少收不臆造）。
    /// </summary>
    public const double MinKeepRatio = 0.45;

    /// <summary>
    /// 一句被剪掉多少秒，才值得提醒"这句少了一截"。
    /// 0.8 秒 ≈ 3~4 个字。低于它的基本都是气口和语气词，不当成内容缺失。
    /// </summary>
    public const double TruncatedGapSec = 0.8;

    /// <summary>有词级时间码时，丢掉的词超过这么多秒才算"真少了内容"。</summary>
    private const double WordLostSec = 0.20;

    /// <summary>
    /// 这个删除原因剪掉的是不是"废话"（剪完内容一字不少）。
    ///
    /// Silence  气口        —— 停顿本来就不出声
    /// Filler   语气词      —— "嗯""那个"，去掉不影响语义
    /// Repeat   重复        —— 同样的话另一遍还在
    /// Stutter  卡顿重说    —— 留下来的那遍是完整的
    /// FalseStart 口误重来  —— 同上
    /// Retake   重录        —— 同上
    ///
    /// 反过来 Normal / OffTopic / Manual 剪掉的是**真讲了的话**，不能当无害。
    /// </summary>
    public static bool IsHarmless(CutReason r) => r switch
    {
        CutReason.Silence => true,
        CutReason.Filler => true,
        CutReason.Repeat => true,
        CutReason.Stutter => true,
        CutReason.FalseStart => true,
        CutReason.Retake => true,
        _ => false,
    };

    // ========================================================
    // 入口
    // ========================================================

    /// <summary>
    /// 算成片口播稿。有转写稿走精确算法，没有就退回文案稿估算。
    /// </summary>
    /// <param name="transcript">第①步导入的转写稿（可为 null）。</param>
    /// <param name="spans">决策时间轴（成片顺序）。</param>
    /// <param name="fallbackScriptText">降级用的文案稿。</param>
    /// <param name="totalSec">成片总时长。</param>
    /// <param name="harmless">粗剪标为"剪了不影响内容"的区间（停顿/重说等），可为 null。</param>
    public static FinalScript Build(
        Transcript? transcript,
        IReadOnlyList<DecisionSpan> spans,
        string fallbackScriptText,
        double totalSec,
        IReadOnlyList<HarmlessSpan>? harmless = null)
    {
        if (transcript != null && transcript.Segments.Count > 0 && spans.Count > 0)
        {
            var exact = FromTranscript(transcript, spans, harmless);
            if (exact.Lines.Count > 0) return exact;
            // 有转写稿却一句都没对上（稿子对错了音频）—— 不要硬凑，
            // 降级并在界面上说明，比给一份空稿子让人以为程序坏了强。
        }

        return FromScriptText(fallbackScriptText ?? "", totalSec);
    }

    // ========================================================
    // 精确：转写稿 ∩ 决策
    // ========================================================
    //
    // 【主循环为什么是"遍历句子"而不是"遍历块"】
    //   以块为纲，一句话被切成 3 块就会冒出 3 行（每行都是残缺的同一句），
    //   而且"这句话到底说没说完"要跨行才能判断。
    //   以句为纲，一句就是一行，缺口一眼看清 —— 也正是人读稿的方式。

    public static FinalScript FromTranscript(
        Transcript t,
        IReadOnlyList<DecisionSpan> spans,
        IReadOnlyList<HarmlessSpan>? harmless = null)
    {
        var lines = new List<ScriptLine>();

        // 无害区间按源起点排一次（每句都要查它，别在循环里反复排）
        var hs = harmless == null || harmless.Count == 0
            ? null
            : harmless.OrderBy(h => h.SourceStartSec).ToList();

        foreach (var seg in t.Segments)
        {
            double dur = seg.EndSec - seg.StartSec;
            if (dur <= 1e-6) continue;

            // 这句在成片里被哪些块保留下来（按成片先后排好）
            var ps = Intersect(seg.StartSec, seg.EndSec, spans);

            // 一块都没覆盖 = 整句被剪掉 → 不该出现在成片稿里
            if (ps.Count == 0) continue;

            double covered = ps.Sum(p => p.SrcEnd - p.SrcStart);
            if (covered / dur < MinKeepRatio) continue;     // 剪掉一多半，不要

            // 成片位置：第一块的头 → 最后一块的尾。
            // 这几块在成片里首尾相接（中间缺的正是被剪掉的停顿），所以这段是连续的。
            double tA = ps[0].TimelineStart;
            double tB = ps[^1].TimelineEnd;

            var (text, wordLost) = ComposeText(seg, ps);
            if (text.Trim().Length == 0) continue;

            double cut;
            if (seg.Words.Count > 0)
            {
                // 有词级时间码：丢掉的词有多长，就是少了多少内容（词间空隙是停顿，不算）
                cut = wordLost;
            }
            else
            {
                // 只有句级时间码：缺口秒数 − 其中"剪了不影响内容"的部分
                double gap = Math.Max(0, dur - covered);
                double gapHarmless = GapCoveredByHarmless(seg.StartSec, seg.EndSec, ps, hs);
                cut = Math.Max(0, gap - gapHarmless);
            }

            lines.Add(new ScriptLine
            {
                Text = text,
                StartSec = tA,
                EndSec = tB,
                SourceStartSec = ps[0].SrcStart,
                SourceEndSec = ps[^1].SrcEnd,
                KeptRatio = Math.Clamp(covered / dur, 0, 1),
                CutSec = cut,
                // 只在这句**确实少了一截话**时才提醒。
                // 剪气口、剪重录、剪语气词都不算 —— 那些剪完内容一字不少。
                Truncated = cut > (seg.Words.Count > 0 ? WordLostSec : TruncatedGapSec),
            });
        }

        // ★ 收尾必须按**成片先后**重排。
        //   上面是按转写稿（= 源素材）的顺序遍历的，而精剪允许挪块 ——
        //   成片里"第二句"完全可能排在"第一句"前面。不重排的稿子读起来通顺，
        //   但顺序是错的（分镜会照着它把画面配到别的话上）。
        //   （同一段源音频被两个块引用 = 成片里确实播了两次，那就该出现两次，
        //     不做去重。）
        lines.Sort((a, b) =>
        {
            int c = a.StartSec.CompareTo(b.StartSec);
            return c != 0 ? c : a.SourceStartSec.CompareTo(b.SourceStartSec);
        });

        for (int k = 0; k < lines.Count; k++) lines[k].Index = k + 1;

        double total = spans.Count > 0 ? spans[^1].TimelineEndSec : 0;
        return new FinalScript
        {
            IsEstimate = false,
            Source = "transcript",
            TotalSec = total,
            CharCount = lines.Sum(l => l.Text.Count(c => !char.IsWhiteSpace(c))),
            Lines = lines,
        };
    }

    /// <summary>源素材的 [a,b] 落在成片里的每一段（按成片先后排）。</summary>
    private static List<Piece> Intersect(double a, double b, IReadOnlyList<DecisionSpan> spans)
    {
        var list = new List<Piece>();

        foreach (var s in spans)
        {
            double lo = Math.Max(a, s.SourceStartSec);
            double hi = Math.Min(b, s.SourceEndSec);
            if (hi - lo <= 1e-6) continue;

            list.Add(new Piece
            {
                SrcStart = lo,
                SrcEnd = hi,
                // 成片坐标 = 块在成片里的起点 + 块内偏移
                TimelineStart = s.TimelineStartSec + (lo - s.SourceStartSec),
                TimelineEnd = s.TimelineStartSec + (hi - s.SourceStartSec),
            });
        }

        list.Sort((x, y) => x.TimelineStart.CompareTo(y.TimelineStart));
        return list;
    }

    /// <summary>成片里的一小段：源区间 + 它在成片里的位置。</summary>
    private readonly struct Piece
    {
        public double SrcStart { get; init; }
        public double SrcEnd { get; init; }
        public double TimelineStart { get; init; }
        public double TimelineEnd { get; init; }
    }

    /// <summary>
    /// 句子里"被剪掉的那部分"有多少是**无害删除**（停顿、重说那几遍）。
    /// 剩下的才是真的少说了话。
    /// </summary>
    private static double GapCoveredByHarmless(
        double a, double b, List<Piece> pieces, IReadOnlyList<HarmlessSpan>? harmless)
    {
        if (harmless == null || harmless.Count == 0) return 0;

        // 缺口要在**源素材**上算，所以得按源顺序走一遍
        var bySource = pieces.OrderBy(p => p.SrcStart).ToList();

        double total = 0;
        double cur = a;

        foreach (var p in bySource)
        {
            if (p.SrcStart > cur) total += OverlapHarmless(cur, p.SrcStart, harmless);
            cur = Math.Max(cur, p.SrcEnd);
        }
        if (cur < b) total += OverlapHarmless(cur, b, harmless);

        return total;
    }

    /// <summary>[g0,g1] 与所有无害区间的交叠总长。</summary>
    private static double OverlapHarmless(double g0, double g1, IReadOnlyList<HarmlessSpan> hs)
    {
        double t = 0;
        foreach (var h in hs)
        {
            double ov = Math.Min(g1, h.SourceEndSec) - Math.Max(g0, h.SourceStartSec);
            if (ov > 0) t += ov;
            if (h.SourceStartSec >= g1) break;      // hs 按起点排好时可直接跳出
        }
        return t;
    }

    // ========================================================
    // 拼文本
    // ========================================================
    //
    // 有词级时间码 → 一个词一个词地判（能精确到字，边界切在词上就丢整个词）
    // 只有句级     → 整句进出（这时句内被挖掉一段没法定位到字，由调用方报秒数）

    private static (string Text, double LostSec) ComposeText(TranscriptSegment seg, List<Piece> ps)
    {
        if (seg.Words == null || seg.Words.Count == 0)
            return (seg.Text ?? "", 0);

        var sb = new StringBuilder();
        double lost = 0;

        foreach (var w in seg.Words)
        {
            // 保留判据：词的中点落在成片保留区内。
            // 用中点而不是起止都框住，是为了不因为转写引擎几十毫秒的误差丢掉整词。
            double mid = (w.StartSec + w.EndSec) / 2.0;

            bool keep = false;
            foreach (var p in ps)
            {
                if (mid >= p.SrcStart && mid < p.SrcEnd) { keep = true; break; }
            }

            if (keep) sb.Append(w.Text);
            else lost += Math.Max(0, w.EndSec - w.StartSec);
        }

        return (sb.ToString(), lost);
    }

    // ========================================================
    // 降级：文案稿 + 字数占比
    // ========================================================

    /// <summary>
    /// 没有转写稿时的退路。按字数占比把总时长分给每句 —— 这是**近似**，
    /// 所以 IsEstimate 必须是 true，界面要写明。
    /// </summary>
    public static FinalScript FromScriptText(string text, double totalSec)
    {
        var sentences = SplitSentences(text);
        var lines = new List<ScriptLine>();

        double sumLen = sentences.Sum(s => Math.Max(1, WeightOf(s)));
        double cursor = 0;

        for (int i = 0; i < sentences.Count; i++)
        {
            // 最后一句吃掉剩下的时间，避免累积误差让末句残缺
            double dur = (i == sentences.Count - 1)
                ? totalSec - cursor
                : totalSec * (Math.Max(1, WeightOf(sentences[i])) / sumLen);
            if (dur <= 0.01) dur = 0.01;

            lines.Add(new ScriptLine
            {
                Index = i + 1,
                Text = sentences[i],
                StartSec = cursor,
                EndSec = cursor + dur,
                SourceStartSec = cursor,
                SourceEndSec = cursor + dur,
                KeptRatio = 1,
                CutSec = 0,
                Truncated = false,
            });
            cursor += dur;
        }

        return new FinalScript
        {
            IsEstimate = true,
            Source = "script",
            TotalSec = totalSec,
            CharCount = lines.Sum(l => l.Text.Count(c => !char.IsWhiteSpace(c))),
            Lines = lines,
        };
    }

    // ========================================================
    // 切句（界面层也用这一份，别各写各的）
    // ========================================================

    /// <summary>切句。句号、叹号、问号、分号、换行都算断句点，保留标点。</summary>
    public static List<string> SplitSentences(string text)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return res;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            // 保留标点：写提示词时，带标点的句子更好读
            var parts = Regex.Split(line, "(?<=[。！？!?；;])")
                             .Select(x => x.Trim())
                             .Where(x => x.Length > 0);

            res.AddRange(parts);
        }

        return res;
    }

    /// <summary>一句的重量 = 中文字数 + 英文字数/2（中文一字一音，英文一个词不一定）。</summary>
    public static int WeightOf(string line) =>
        line.Count(c => c > 0x2E80) + line.Count(char.IsLetterOrDigit) / 2;
}
