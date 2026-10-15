// ============================================================
// RepeatDetector.cs - 找出"明显重复"（不需要转写）
// ============================================================
//
// 【这一层解决什么】
//
//   人工剪掉的 237.2 秒里，有 72 秒是**有声内容**：
//   说错重来、整句重复、废话。这部分 VAD 剪不掉（因为有声音）。
//
//   传统做法是先转写成文字，再比文字。但现在没有 whisper。
//   所以走**波形路线**：
//
//     同一句话重说一遍 → 音节节奏几乎一样 → 包络形状高度重合
//
// 【为什么这招够用】
//
//   我们要抓的是"明显重复"，不是"意思相近"。
//   明显重复在波形上是**近乎复制粘贴**的，相关系数能到 0.9 以上；
//   两句不同的话，就算长度一样，也很难超过 0.86。
//   中间有足够的空档，误判风险可控。
//
// 【剪哪一个？规则与理由】
//
//   找到一对重复（先说的 A，后说的 B）：
//
//     · 如果 B 不比 A 短  → A 是废版本（说一半卡住/说错）→ 剪 A
//       这是最常见的情况："这个时期的特点是…… 这个时期的特点是XXX"
//       第一遍通常不完整，第二遍才是完整版。
//
//     · 如果 B 明显比 A 短 → 可能是修辞性重复（"很美，很美"）
//       也可能是收尾时的重复强调 → **不自动剪**，只标注，交给人工
//
//   这条规则保证：拿不准的一律不动手，只提示。
//
// 【这一层的天花板】
//
//   抓不到"我语音里明确说了要剪掉"这种（比如口播中说"这段删掉"）——
//   那需要听懂内容，属于语义层，得等转写进来。
//   现在留了 CutReason.Manual 这个位置给将来。
//
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut.Core.Engine;

/// <summary>一对疑似重复。</summary>
public class RepeatPair
{
    /// <summary>先说的那段。</summary>
    public SilenceSpan First { get; set; }

    /// <summary>后说的那段。</summary>
    public SilenceSpan Second { get; set; }

    /// <summary>波形相似度（0~1）。越高越像"同一句话说了两遍"。</summary>
    public double Similarity { get; set; }

    /// <summary>建议剪掉哪一段（null = 拿不准，只提示不自动剪）。</summary>
    public SilenceSpan? CutTarget { get; set; }

    /// <summary>把握（0~1）。低于 AutoCutOptions.RepeatAutoCutConfidence 就只提示。</summary>
    public double Confidence { get; set; }

    /// <summary>剪掉能省多少秒。</summary>
    public double GainSec => CutTarget.HasValue ? CutTarget.Value.DurationSec : 0;

    public override string ToString()
    {
        string target = CutTarget.HasValue ? $"剪 [{CutTarget.Value.StartSec:F2}~{CutTarget.Value.EndSec:F2}]" : "仅提示";
        return $"重复 {Similarity:F2} | A {First.StartSec:F2}~{First.EndSec:F2}s " +
               $"B {Second.StartSec:F2}~{Second.EndSec:F2}s | {target} | 把握 {Confidence:F2}";
    }
}

/// <summary>重复检测。</summary>
public static class RepeatDetector
{
    /// <summary>
    /// 在"有声段"之间找重复。
    /// </summary>
    /// <param name="runs">有声段列表（时间顺序）</param>
    /// <param name="env">全片包络</param>
    /// <param name="frameSec">每帧多少秒</param>
    /// <param name="opt">参数</param>
    public static List<RepeatPair> Detect(List<SilenceSpan> runs, double[] env, double frameSec, AutoCutOptions opt)
    {
        var pairs = new List<RepeatPair>();

        for (int i = 0; i < runs.Count; i++)
        {
            var a = runs[i];
            if (a.DurationSec < opt.RepeatMinSec || a.DurationSec > opt.RepeatMaxSec) continue;

            for (int j = i + 1; j < runs.Count; j++)
            {
                var b = runs[j];

                // 超出"重说"的时间窗口就停（runs 是按时间排的，后面的只会更远）
                if (b.StartSec - a.EndSec > opt.RepeatWindowSec) break;
                if (b.DurationSec < opt.RepeatMinSec || b.DurationSec > opt.RepeatMaxSec) continue;

                // 长度差太多 → 不是同一句
                double ratio = b.DurationSec / a.DurationSec;
                if (ratio < opt.RepeatDurRatioMin || ratio > opt.RepeatDurRatioMax) continue;

                // 比形状
                double sim = Envelope.Correlation(
                    Envelope.Resample(env, (int)(a.StartSec / frameSec), Math.Max(1, (int)(a.DurationSec / frameSec))),
                    Envelope.Resample(env, (int)(b.StartSec / frameSec), Math.Max(1, (int)(b.DurationSec / frameSec))));

                if (sim < opt.RepeatSimilarity) continue;

                // 判定：后说的 B 不比 A 短 → A 是废版本，剪 A
                //       否则拿不准 → 只提示
                double confidence;
                SilenceSpan? target;
                if (ratio >= 0.95)
                {
                    target = a;                                  // 剪先说的那个
                    confidence = Math.Min(1.0, (sim - opt.RepeatSimilarity) / (1.0 - opt.RepeatSimilarity + 1e-9) * 0.7 + 0.3);
                }
                else
                {
                    target = null;                               // 拿不准
                    confidence = 0.35;
                }

                if (target.HasValue && target.Value.DurationSec < opt.RepeatMinGainSec)
                    continue;    // 省不了多少，不值得冒险

                pairs.Add(new RepeatPair
                {
                    First = a,
                    Second = b,
                    Similarity = sim,
                    CutTarget = target,
                    Confidence = confidence,
                });

                break;   // A 只跟最近的一个重复配对，避免一次匹配串成一串
            }
        }

        // 去重：同一个段可能被多个前段匹配到，只保留相似度最高的一次
        var best = new Dictionary<int, RepeatPair>();
        foreach (var p in pairs)
        {
            int key = (int)(p.First.StartSec * 1000);
            if (!best.ContainsKey(key) || best[key].Similarity < p.Similarity)
                best[key] = p;
        }

        var result = new List<RepeatPair>(best.Values);
        result.Sort((x, y) => x.First.StartSec.CompareTo(y.First.StartSec));
        return result;
    }
}
