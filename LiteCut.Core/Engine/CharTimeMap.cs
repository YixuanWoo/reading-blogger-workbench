// ============================================================
// CharTimeMap.cs - 把"第几个字"翻译成"第几秒"
// ============================================================
//
// 【为什么要这个东西】
//
//   算法在**文字**上思考（"这两个字是不是多余"），
//   但剪刀要落在**时间**上（"从第几秒剪到第几秒"）。
//
//   中间少一座桥：某个字到底是第几秒说的？
//
//   这座桥就是 CharTimeMap。它给出一句话里每个字的开始/结束时间，
//   于是算法只要回答"删掉第 i 到第 j 个字"，把它翻译成时间即可。
//
// 【为什么洗过字（normalize）之后还能对得上】
//
//   归一化会删掉标点和空格（见 TextSimilarity.Normalize），
//   所以"洗过之后的第 i 个字" ≠ "原文的第 i 个字符"。
//
//   解决办法：**逐词归一化**。
//   Segment 里给的是词级时间码，我们就一个词一个词处理：
//   每个词自己归一化，这个词剩下的字平均分摊这个词的时长。
//   然后把所有词的结果接起来 —— 得到的就是"洗过之后的每个字"对应的时间。
//   这样索引关系天然对齐，不用反查原文。
//
//   例：词表=[我们/今天/，/来讲]，时长分别是 0.3/0.4/0.1/0.5
//     洗完 = "我们今天来讲"（逗号被丢掉）
//     时间 = [我0.0 们0.15][今0.4 天0.6][来0.8 讲1.05]
//
// 【词级时间码不可用时怎么办】
//
//   有些转写引擎（包括一些 external agent 的输出）只给句级时间码。
//   这时我们按字数把整句时长**平均分**给每个字。
//
//   准吗？不准（一句话里有的字快有的字慢，误差几百毫秒）。
//   但没关系 —— 后面还有两个补救：
//     ① TranscriptAligner 会用包络校验整体对齐质量
//     ② Snapping 会在数百毫秒窗口内把切点吸附到真正的静音点上
//   也就是说：**文字负责定"删什么"，波形负责定"在哪下刀"**。
//   这正是双轨设计比单靠文字稳的地方。
//
// ============================================================

using System;
using System.Collections.Generic;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>一句话里，每个字对应的时间。</summary>
public sealed class CharTimeMap
{
    /// <summary>归一化之后的句子（无标点、无空格）。</summary>
    public string Text { get; } = "";

    /// <summary>第 i 个字的开始时间（秒）。长度与 Text.Length 相同。</summary>
    public double[] StartSec { get; } = Array.Empty<double>();

    /// <summary>第 i 个字的结束时间（秒）。长度与 Text.Length 相同。</summary>
    public double[] EndSec { get; } = Array.Empty<double>();

    /// <summary>这个映射是靠词级时间码算出来的，还是靠平分估出来的。</summary>
    public bool FromWordTimestamps { get; }

    /// <summary>字符数。</summary>
    public int Length => Text.Length;

    public CharTimeMap(string text, double[] startSec, double[] endSec, bool fromWordTimestamps)
    {
        Text = text;
        StartSec = startSec;
        EndSec = endSec;
        FromWordTimestamps = fromWordTimestamps;
    }

    /// <summary>第 i 个字的开始时间。越界就返回整句的边界，保证不炸。</summary>
    public double StartOf(int i)
    {
        if (StartSec.Length == 0) return 0;
        return StartSec[Math.Clamp(i, 0, StartSec.Length - 1)];
    }

    /// <summary>第 j-1 个字的结束时间（约定：区间是左闭右开 [i, j)）。</summary>
    public double EndOf(int j)
    {
        if (EndSec.Length == 0) return 0;
        return EndSec[Math.Clamp(j - 1, 0, EndSec.Length - 1)];
    }

    /// <summary>第 i 个字到第 j 个字（不含 j）的时长。</summary>
    public double DurationOf(int i, int j) => Math.Max(0, EndOf(j) - StartOf(i));

    /// <summary>
    /// 建一张映射表。
    /// 词级时间码可用就用词级的，不可用时退回句级平分。
    /// </summary>
    public static CharTimeMap Build(TranscriptSegment seg)
    {
        if (seg == null) return new CharTimeMap("", Array.Empty<double>(), Array.Empty<double>(), false);

        double segStart = seg.StartSec;
        double segEnd = seg.EndSec;
        if (segEnd <= segStart) segEnd = segStart + Math.Max(0.0, seg.DurationSec);

        // ---- 先试词级方案 ----
        if (seg.Words != null && seg.Words.Count > 0)
        {
            var text = new System.Text.StringBuilder();
            var starts = new List<double>();
            var ends = new List<double>();

            // 兜底游标
            double cursor = segStart;

            foreach (var w in seg.Words)
            {
                string norm = TextSimilarity.Normalize(w?.Text);
                if (norm.Length == 0) continue;   // 纯标点词，没有字，跳过

                double ws = w!.StartSec;
                double we = w.EndSec;

                // 时间码坏了的三条判据：区间无效、早于句首太多、晚于句尾太多
                bool broken = we <= ws
                              || ws < segStart - 0.5
                              || we > segEnd + 0.5;
                if (broken)
                {
                    // 这个词的时间不可信 → 用兜底游标给一个估计值
                    ws = cursor;
                    we = cursor + 0.001;
                }

                ws = Math.Max(ws, segStart);
                we = Math.Max(we, ws);
                cursor = we;

                // 这个词有 norm.Length 个字，平分它的时长
                double per = (we - ws) / norm.Length;
                for (int k = 0; k < norm.Length; k++)
                {
                    text.Append(norm[k]);
                    starts.Add(ws + per * k);
                    ends.Add(ws + per * (k + 1));
                }
            }

            if (text.Length > 0 && IsValid(starts, segStart, segEnd))
            {
                return new CharTimeMap(text.ToString(), starts.ToArray(), ends.ToArray(), true);
            }
            // 词级时间码整体不可信 → 落到下面，走句级平分
        }

        // ---- 退回方案：整句时长按字数平分 ----
        string whole = TextSimilarity.Normalize(seg.Text);
        int n = whole.Length;
        double total = Math.Max(0.0, segEnd - segStart);
        var s2 = new double[n];
        var e2 = new double[n];
        double per2 = n > 0 ? total / n : 0;
        for (int i = 0; i < n; i++)
        {
            s2[i] = segStart + per2 * i;
            e2[i] = segStart + per2 * (i + 1);
        }
        return new CharTimeMap(whole, s2, e2, false);
    }

    /// <summary>
    /// 检查一组时间点是否可信：必须单调不减，且大致落在句子范围内。
    /// 防止外部转写稿给出乱序的时间码，导致后面"剪掉的时间区间是反的"。
    /// </summary>
    private static bool IsValid(List<double> starts, double segStart, double segEnd)
    {
        if (starts.Count == 0) return false;
        double prev = double.MinValue;
        foreach (var s in starts)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return false;
            if (s < prev - 0.001) return false;   // 倒退太多
            prev = s;
        }
        // 整体偏离句子范围太远也不接受
        if (starts[0] < segStart - 1.0 || starts[^1] > segEnd + 1.0) return false;
        return true;
    }
}
