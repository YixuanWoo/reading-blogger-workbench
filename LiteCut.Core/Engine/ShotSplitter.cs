// ============================================================
// ShotSplitter.cs - 把成片口播稿切成镜头
// ============================================================
//
// 【三种切法，用户选（2026-10-01 拍板：默认 c；a/b 保留为可选项）】
//
//   (a) 一句一镜头 —— 用真实句时间
//       你写稿时一句一个意思，说完一句换画面，最自然。
//       毛病：长句会十几秒不换画面，看着呆；真实项目里切出 219 镜，
//       每句一换画面「像 PPT 翻页」，所以它已不再是默认（但想用随时可拨）。
//
//   (b) 固定节奏 + 吸附句边界 —— 默认 6 秒一镜
//       规定"每 N 秒换一次画面"，但**切换点一律落在某句话说完之后**，
//       绝不切在半句话中间（半句话上换画面，字幕会断得莫名其妙）。
//
//   (c) 语义合并 ★ 默认 —— 贪心把相邻句子并成 5~9 秒的语义块
//       一个块 = 一个不切走的场景，块内几拍都是同一个界面在演化（句子=节拍，块=场景）。
//       它解决的是 (a) 的碎、(b) 的机械：既不一句一换，也不按固定秒数硬凑，
//       而是"凑够 5~9 秒、切点落句末"。切完仍可手动合并/拆分（界面层提供）。
//
// 【(b) 的贪心怎么选切点】
//   从第 i 句开始往后累加，在每一个"句末"都是一个候选切点，
//   选 |累计时长 − 目标时长| 最小的那个；但只要累计已经超过目标 1.6 倍就
//   不再往后看（否则为了凑时长会把半分钟并成一镜，反而失去切分的意义）。
//
// 【为什么放 Core 而不是界面层】
//   纯算术、能无头验证。界面只负责把开关拨过去。
//
// 【长句】
//   时长 ≥ 12 秒的镜头会打上 IsLong。它不是错误，只是提示 Agent：
//   这个镜头要撑十几秒，动效得做得复杂些，让句子内容动起来，
//   不要只顶一行字幕在那儿不动。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>镜头切法。</summary>
public enum ShotSplitMode
{
    /// <summary>(a) 一句一镜头（真实句时间）。细分场景可选，但已不是默认。</summary>
    BySentence = 0,

    /// <summary>(b) 固定节奏 + 吸附句边界。</summary>
    FixedPace = 1,

    /// <summary>(c) 语义合并：贪心把相邻句子并成 5~9 秒的语义块。★ 默认（2026-10-01 起）</summary>
    SemanticMerge = 2,
}

/// <summary>
/// 切出来的一个镜头（还没配画面 —— 配画面是 Agent 的事）。
/// </summary>
public class ShotSpan
{
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }

    /// <summary>这个镜头里说的话（多句时按顺序拼）。</summary>
    public string Text { get; set; } = "";

    public double SourceStartSec { get; set; }
    public double SourceEndSec { get; set; }

    /// <summary>长镜头（≥12 秒）—— 提示词里要求动效复杂些。</summary>
    public bool IsLong { get; set; }

    /// <summary>这一镜并了几句（(b) 模式下可能 >1）。</summary>
    public int SentenceCount { get; set; } = 1;

    public double DurationSec => EndSec - StartSec;
}

public static class ShotSplitter
{
    /// <summary>(b) 模式下最多"为了凑时长"往后看多少倍。</summary>
    private const double MaxStretch = 1.6;

    public static List<ShotSpan> Split(FinalScript script, ShotSplitMode mode, double targetSec = 6.0,
        double minSec = 5.0, double maxSec = 9.0)
    {
        if (script == null || script.Lines.Count == 0) return new List<ShotSpan>();
        if (targetSec < 1) targetSec = 1;
        if (minSec < 1) minSec = 1;
        if (maxSec < minSec) maxSec = minSec + 1;

        var spans = mode switch
        {
            ShotSplitMode.BySentence => SplitBySentence(script),
            ShotSplitMode.SemanticMerge => SplitBySemantic(script, minSec, maxSec),
            _ => SplitByPace(script, targetSec),
        };

        // ★ 切完必须"铺满"整条决策轴 —— 见 FillToTimeline 的说明。
        FillToTimeline(spans, script.TotalSec);
        return spans;
    }

    /// <summary>
    /// 把切好的块**铺满**整条成片轴：句与句之间的气口归**前一块**。
    ///
    /// 【为什么非做不可】（2026-10-03 用户实报：⑤ 出片被自检拦下，音画差 1.340s）
    ///   精剪（决策）保留的那些"句间留白"是用户的**节奏设计**（气口、停顿），
    ///   一个字都不该丢。可上面三种切法都只用"这句话说到哪"当块尾 ——
    ///   留白不属于任何一句话，**没有任何一块认领它**：
    ///     落在块内部的留白被块长盖住了（没事），
    ///     落在**块边界**上的留白就整段丢失。
    ///   实测一个真实项目：26 块加起来 187.512s，精剪轴 188.852s，
    ///   合计短 1.340s（片头前导 0.560 + 边界留白 0.780）。
    ///
    /// 【丢了之后不是"少 1.3 秒"这么轻】
    ///   组接是"从连续音轨上按每块的时长切一段贴上去"。两轴不同长
    ///   → 每块分到的音频整体挪位（片头错 0.56s，往片尾累积到 1.34s），
    ///   音画错位、最后一句还被切掉半截。
    ///
    /// 【铺满的口径：气口归前一块】
    ///   不再用"这句话说到哪"当块尾，改用"**下一块的第一句话从哪开始**"当块尾。
    ///   中间那口气就自动归给了前一块 —— 也符合直觉：一句话说完，画面多停一会儿。
    ///   （想改成"归后一块"或"对半分"，只改这里。）
    ///
    /// 【只动时间窗，不动源区间】
    ///   SourceStartSec / SourceEndSec 一个字不改 —— 镜头 ID 的稳定性靠它
    ///   （界面按源区间继承老镜头），所以重切之后 ID 不丢，只是标"待重渲"。
    /// </summary>
    private static void FillToTimeline(List<ShotSpan> spans, double totalSec)
    {
        if (spans.Count == 0 || totalSec <= 0) return;

        spans[0].StartSec = 0;                       // 吃掉片头前导（第一句之前的那口气）
        for (int i = 0; i < spans.Count - 1; i++)
        {
            double next = spans[i + 1].StartSec;     // 下一块的第一句话从哪开始
            if (next > spans[i].StartSec) spans[i].EndSec = next;
        }
        spans[^1].EndSec = totalSec;                 // 吃掉片尾余量
    }

    private static List<ShotSpan> SplitBySentence(FinalScript script)
    {
        var res = new List<ShotSpan>();
        int idx = 1;

        foreach (var line in script.Lines)
        {
            res.Add(new ShotSpan
            {
                Index = idx++,
                StartSec = line.StartSec,
                EndSec = line.EndSec,
                Text = line.Text,
                SourceStartSec = line.SourceStartSec,
                SourceEndSec = line.SourceEndSec,
                IsLong = line.DurationSec >= FinalScriptBuilder.LongLineSec,
                SentenceCount = 1,
            });
        }

        return res;
    }

    private static List<ShotSpan> SplitByPace(FinalScript script, double target)
    {
        var res = new List<ShotSpan>();
        var lines = script.Lines;
        int i = 0, idx = 1;

        while (i < lines.Count)
        {
            int best = i;
            double bestErr = Math.Abs(lines[i].DurationSec - target);

            double cum = 0;
            for (int j = i; j < lines.Count; j++)
            {
                cum += lines[j].DurationSec;

                double err = Math.Abs(cum - target);
                if (err < bestErr) { bestErr = err; best = j; }

                if (cum >= target * MaxStretch) break;
            }

            var first = lines[i];
            var last = lines[best];
            double dur = last.EndSec - first.StartSec;

            var sb = new StringBuilder();
            for (int k = i; k <= best; k++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(lines[k].Text);
            }

            res.Add(new ShotSpan
            {
                Index = idx++,
                StartSec = first.StartSec,
                EndSec = last.EndSec,
                Text = sb.ToString(),
                SourceStartSec = first.SourceStartSec,
                SourceEndSec = last.SourceEndSec,
                IsLong = dur >= FinalScriptBuilder.LongLineSec,
                SentenceCount = best - i + 1,
            });

            i = best + 1;
        }

        return res;
    }

    /// <summary>
    /// 语义合并（任务三）：贪心把相邻句子并成 [minSec, maxSec] 秒的语义块。
    ///
    /// 规则：
    ///   从第一句起向后累加；一旦累计 ≥ minSec，就在"不越过 maxSec 的前提下"
    ///   选离区间中点最近的句末作切点；若再加一句就 ≥ maxSec，则在此前切；
    ///   若单句本身就 ≥ maxSec，它独自成一块（不硬切半句）。
    ///   切块点永远落在句末，绝不切在半句话中间。
    ///
    /// 两个边界处理（避免退化）：
    ///   ① 超长单句（≥ maxSec）：块首就是它 → 直接独自成块；循环里遇到它
    ///      （且不是块首）→ 当前块截止到上一句，超长句留到下一轮独自成块。
    ///   ② 尾部凑不满 minSec：把"能塞进 maxSec 的全部"并成一块；若整个尾巴
    ///      都不到 maxSec，则全部并成一块（避免退化成一句一块的碎块）。
    /// </summary>
    private static List<ShotSpan> SplitBySemantic(FinalScript script, double minSec, double maxSec)
    {
        var res = new List<ShotSpan>();
        var lines = script.Lines;
        int i = 0, idx = 1;

        while (i < lines.Count)
        {
            int best;

            // ① 块首就是超长单句 → 独自成块（不硬切半句）
            if (lines[i].DurationSec >= maxSec)
            {
                best = i;
            }
            else
            {
                best = i;
                double cum = 0;
                double bestErr = double.MaxValue;
                double mid = (minSec + maxSec) / 2;
                bool reachedMin = false;

                for (int j = i; j < lines.Count; j++)
                {
                    cum += lines[j].DurationSec;

                    // 循环里撞到一个超长单句（不是块首）：它不能并进来，
                    // 当前块截止到上一句，超长句留到下一轮独自成块
                    if (lines[j].DurationSec >= maxSec && j > i)
                    {
                        best = j - 1;
                        reachedMin = true;
                        break;
                    }

                    // 达到 min 之后才有资格当切点；偏向区间中点
                    if (cum >= minSec)
                    {
                        reachedMin = true;
                        double err = Math.Abs(cum - mid);
                        if (err < bestErr) { bestErr = err; best = j; }
                    }

                    // 再加一句就超 max 了 → 在此前切（best 已是最优句末）
                    if (cum >= maxSec) break;
                }

                // ② 尾部凑不满 minSec（循环跑完也没到 min，也没撞超长句）：
                //    把"能塞进 maxSec 的全部"并成一块，避免退化成一句一块
                if (!reachedMin)
                {
                    best = i;
                    cum = 0;
                    for (int j = i; j < lines.Count; j++)
                    {
                        double next = cum + lines[j].DurationSec;
                        if (next > maxSec && j > i) break;
                        cum = next;
                        best = j;
                    }
                }
            }

            if (best < i) best = i;   // 兜底，避免死循环

            var first = lines[i];
            var last = lines[best];
            double dur = last.EndSec - first.StartSec;

            var sb = new StringBuilder();
            for (int k = i; k <= best; k++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(lines[k].Text);
            }

            res.Add(new ShotSpan
            {
                Index = idx++,
                StartSec = first.StartSec,
                EndSec = last.EndSec,
                Text = sb.ToString(),
                SourceStartSec = first.SourceStartSec,
                SourceEndSec = last.SourceEndSec,
                IsLong = dur >= FinalScriptBuilder.LongLineSec,
                SentenceCount = best - i + 1,
            });

            i = best + 1;
        }

        return res;
    }
}
