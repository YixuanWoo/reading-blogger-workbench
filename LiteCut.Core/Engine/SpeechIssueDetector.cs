// ============================================================
// SpeechIssueDetector.cs - 从文字里找出"该剪的地方"
// ============================================================
//
// 【这一层解决什么】
//
//   《美的历程》那条素材，人工剪掉 237.2 秒：
//     165.2 秒是静音     ← 波形层已经解决
//      72.0 秒是废话     ← 就是这一层要解决的
//
//   所谓的"废话"拆开是四类：
//
//     ① 填充词   "嗯""啊""这个""就是"
//     ② 半截重说 "我们要讲的是唐代……（卡住）我们要讲的是唐代的美学历程"
//     ③ 词级口吃 "我们今天我们今天来讲一期"
//     ④ 整句重复 隔了几句又把同一句话说了一遍
//
//   这四类有共同的判据特征，但也有本质区别，
//   所以分成四个独立的检测器，互不干扰。
//
// 【为什么 ② 和 ③ 要分开】
//
//   两者的物理表现不同：
//     ② 发生在**两句之间**——中间通常有一个明显的停顿（句子级播报）
//     ③ 发生在**一句之内**——中间几乎没有间隔，就是嘴瓢了一下
//   判据完全不同：② 比的是"短句是不是被长句包住"，③ 找的是"相邻重复子串"。
//
// 【一个贯穿全局的原则：拿不准就标出来，不动手】
//
//   每个检测结果都带 Confidence（把握）和 AutoCut（是否动刀）。
//   置信度没过阈值的，照样出现在列表里，但 AutoCut = false ——
//   界面上显示为"建议你看一眼"，而不是剪掉。
//   同一份 CutPlan 里两种都留着，用户审核时一勾就行。
//
// ============================================================

using System;
using System.Collections.Generic;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>
/// 一处文本层问题。
/// 注意：这是"检测到的问题"，还不是最终决策 —— 决策由 AutoCutEngine 汇总后做出。
/// </summary>
public sealed class TextIssue
{
    /// <summary>建议删除区间的起点（秒）。已套用安全 pad。</summary>
    public double StartSec { get; set; }

    /// <summary>建议删除区间的终点（秒）。</summary>
    public double EndSec { get; set; }

    /// <summary>问题类型。</summary>
    public CutReason Reason { get; set; }

    /// <summary>把握（0~1）。</summary>
    public double Confidence { get; set; }

    /// <summary>建议自动剪掉（true），还是只提示人工看（false）。</summary>
    public bool AutoCut { get; set; }

    /// <summary>证据文本（就是被认为多余的那几个字）。审核界面上直接显示它。</summary>
    public string Evidence { get; set; } = "";

    /// <summary>给人看的说明（"剔除方块字里有 1 处"这类，帮用户快速判断）。</summary>
    public string Note { get; set; } = "";

    /// <summary>来自第几句。</summary>
    public int SegmentIndex { get; set; } = -1;

    /// <summary>时长。</summary>
    public double DurationSec => EndSec - StartSec;
}

/// <summary>语义层检测器。</summary>
public static class SpeechIssueDetector
{
    /// <summary>
    /// 在一整份转写稿上跑四类检测。
    /// </summary>
    /// <param name="transcript">转写稿。为 null 或没有句子时返回空列表——这就是"降级"的发生点。</param>
    /// <param name="opt">语义层参数</param>
    /// <param name="includeRetakes">
    /// 要不要跑"纯文本重录检测"（默认要）。
    ///
    /// 【什么时候会传 false】
    ///   有稿子（script.md）且对稿可信时 —— 那种情况下重录交给 ScriptAligner
    ///   按"稿子里这句只写了一次"来判，比纯文本判据稳得多，两条路一起跑反而会重复下刀。
    ///   这是 T-28「对稿」接入后的唯一行为变化点。
    /// </param>
    /// <returns>按时间排序的问题列表。</returns>
    public static List<TextIssue> Detect(Transcript? transcript, TextCutOptions opt, bool includeRetakes = true)
    {
        var issues = new List<TextIssue>();

        // 没有文字 → 直接返回空。上层会因此自动退回纯波形模式。
        if (transcript == null || !opt.Enabled) return issues;
        if (transcript.Segments == null || transcript.Segments.Count == 0) return issues;

        var segs = new List<TranscriptSegment>(transcript.Segments);
        segs.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

        if (opt.RemoveFillers) issues.AddRange(ScanFillers(segs, opt));
        if (opt.DetectStutter) issues.AddRange(ScanStutter(segs, opt));
        if (opt.DetectFalseStart || opt.DetectTextRepeat) issues.AddRange(ScanAcrossSegments(segs, opt));
        if (opt.DetectRetake && includeRetakes) issues.AddRange(ScanRetakes(segs, opt));

        issues.Sort((x, y) => x.StartSec.CompareTo(y.StartSec));
        return issues;
    }

    // ============================================================
    // ① 填充词
    // ============================================================

    /// <summary>
    /// 逐句扫描填充词。
    ///
    /// 做法：在句子洗过的文本上从左往右滑，每一步尝试"最长匹配"词表。
    /// 命中就记录，然后跳过这几个字继续。
    /// </summary>
    public static List<TextIssue> ScanFillers(List<TranscriptSegment> segs, TextCutOptions opt)
    {
        var res = new List<TextIssue>();

        foreach (var seg in segs)
        {
            var map = CharTimeMap.Build(seg);
            if (map.Length == 0) continue;

            int i = 0;
            // 连续填充词的起点索引，用于把它们合并成一处删除
            int runStart = -1;
            double runStartTime = 0;
            double runEndTime = 0;
            double runConfSum = 0;
            int runCount = 0;
            string runEvidence = "";
            bool runAutoCut = false;

            while (i < map.Length)
            {
                var m = FillerLexicon.MatchLongest(map.Text, i);

                if (m.Length == 0 || m.Kind == FillerKind.None)
                {
                    FlushFillerRun();
                    i++;
                    continue;
                }

                // 话语标记：默认只标注不动手
                bool isMarker = m.Kind == FillerKind.DiscourseMarker;
                double pad = opt.FillerPadSec;

                double start = map.StartOf(i);
                double end = map.EndOf(i + m.Length);

                // ---- 位置加成：同样一个"啊"，在不同位置的把握不一样 ----
                double conf = m.Confidence;
                bool atHead = (i == 0);
                bool atTail = (i + m.Length >= map.Length);
                bool midSentence = !atHead && !atTail;

                if (atHead) conf += 0.05;        // 句首的"嗯"几乎确定是开口的语气词
                if (atTail) conf += 0.02;        // 句尾拖音也算比较确定
                if (map.FromWordTimestamps && IsWholeWord(seg, start, end)) conf += 0.03; // 独立成词 = 不是从实词里截出来的

                // 位于句子中间且前面已经有字时，如果两侧是实词，多半是"嗯"被识别错或真·语气词；
                // 单独出现且 timings 合理就保留原判，不做惩罚（避免把真实口吃漏掉）
                conf = Math.Clamp(conf, 0.0, 0.99);

                bool autoCut = m.Kind == FillerKind.UserDefined
                               || (!isMarker && conf >= opt.FillerAutoCutConfidence)
                               || (isMarker && opt.RemoveDiscourseMarkers && conf >= opt.FillerAutoCutConfidence);

                if (runStart < 0)
                {
                    runStart = i;
                    runStartTime = start;
                    runEvidence = "";
                    runConfSum = 0;
                    runCount = 0;
                    runAutoCut = false;
                }
                runEndTime = end;
                runConfSum += conf;
                runCount++;
                runEvidence += map.Text.Substring(i, m.Length);
                if (autoCut) runAutoCut = true;

                i += m.Length;
            }
            FlushFillerRun();

            // ---- 把这一句的连续 filler 合成一处 ----
            void FlushFillerRun()
            {
                if (runStart < 0) return;

                double a = runStartTime;
                double b = runEndTime;
                // 套用 pad：往里缩一点，不要擦到相邻字的头尾
                double pad2 = opt.FillerPadSec;
                if (b - a > 2 * pad2 + opt.MinIssueSec) { a += pad2; b -= pad2; }

                if (b - a >= opt.MinIssueSec)
                {
                    res.Add(new TextIssue
                    {
                        StartSec = a,
                        EndSec = b,
                        Reason = CutReason.Filler,
                        Confidence = Math.Round(runConfSum / Math.Max(1, runCount), 3),
                        AutoCut = runAutoCut,
                        Evidence = runEvidence,
                        Note = runCount > 1 ? $"连续 {runCount} 处填充词" : "填充词",
                        SegmentIndex = seg.Index,
                    });
                }
                runStart = -1;
            }
        }

        return res;
    }

    /// <summary>
    /// 判断 [start, end] 是否恰好等于某一个词的完整区间。
    ///
    /// 这是个很有用的精度提升手段：
    ///   如果 filler 是从实词内部抠出来的（比如"这是对的行为"里抠出"的是"），
    ///   它通常不对齐任何词边界。
    ///   而真正的填充词，转写引擎会给它单独一个词。
    /// </summary>
    private static bool IsWholeWord(TranscriptSegment seg, double start, double end)
    {
        if (seg.Words == null || seg.Words.Count == 0) return false;
        const double tol = 0.035;
        foreach (var w in seg.Words)
        {
            if (Math.Abs(w.StartSec - start) <= tol && Math.Abs(w.EndSec - end) <= tol)
                return true;
        }
        return false;
    }

    // ============================================================
    // ③ 词级口吃（一句之内，紧挨着重复了一小串字）
    // ============================================================

    /// <summary>
    /// 在一句之内找"相邻重复子串"。
    ///
    /// 例：归一化后是 "我们今天我们今天来讲"
    ///     在 i=0, k=4 处发现 前四个字 == 后四个字 → 判定 Delete 前四个字。
    ///
    /// 为什么要针对"相邻"：卡壳的物理表现就是**中间不断句**地再说一遍。
    /// 中间隔了句号就不算卡壳，那是两句话（交给 ② 或 ④ 处理）。
    /// </summary>
    public static List<TextIssue> ScanStutter(List<TranscriptSegment> segs, TextCutOptions opt)
    {
        var res = new List<TextIssue>();

        foreach (var seg in segs)
        {
            var map = CharTimeMap.Build(seg);
            if (map.Length < opt.StutterMinChars * 2) continue;

            int i = 0;
            while (i < map.Length - opt.StutterMinChars)
            {
                int hitLen = 0;
                double hitConf = 0;

                // 从长到短试 —— 优先合并更长的重复窗口
                for (int k = opt.StutterMaxChars; k >= opt.StutterMinChars; k--)
                {
                    if (i + 2 * k > map.Length) continue;

                    string a = map.Text.Substring(i, k);
                    string b = map.Text.Substring(i + k, k);

                    bool exact = a == b;
                    double sim = exact ? 1.0 : TextSimilarity.LcsRatio(a, b, 40);
                    if (!exact && sim < 0.80) continue;

                    // 时间约束：整个过程必须短促 + 两段之间不能有长间隔
                    double whole = map.DurationOf(i, i + 2 * k);
                    if (whole > opt.StutterMaxSec) continue;

                    double innerGap = map.StartOf(i + k) - map.EndOf(i + k - 1);
                    if (innerGap > opt.StutterMaxInnerGapSec) continue;

                    hitLen = k;
                    // 完全一模一样最确定；近似匹配降一档；越短促越确定
                    hitConf = (exact ? 0.88 : 0.70 + 0.10 * (sim - 0.80) / 0.20)
                              + (whole <= 1.0 ? 0.05 : 0.0);
                    break;
                }

                if (hitLen == 0) { i++; continue; }

                double start = map.StartOf(i);
                double end = map.EndOf(i + hitLen);
                double pad = opt.FillerPadSec;
                if (end - start > 2 * pad + opt.MinIssueSec) { start += pad; end -= pad; }

                if (end - start >= opt.MinIssueSec)
                {
                    double conf = Math.Clamp(hitConf, 0, 0.99);
                    res.Add(new TextIssue
                    {
                        StartSec = start,
                        EndSec = end,
                        Reason = CutReason.Stutter,
                        Confidence = Math.Round(conf, 3),
                        AutoCut = conf >= opt.StutterAutoCutConfidence,
                        Evidence = map.Text.Substring(i, hitLen),
                        Note = $"口吃重复（{map.Text.Substring(i, hitLen)} 紧跟着又说了一遍）",
                        SegmentIndex = seg.Index,
                    });
                }

                // 跳过这两段，避免同一个位置被重复命中更小的窗口
                i += hitLen * 2;
            }
        }

        return res;
    }

    // ============================================================
    // ② 半截重说  与  ④ 整句重复（都发生在两句之间，合并扫描）
    // ============================================================

    /// <summary>
    /// ⑤ 重录：同一句话在现场录了好几遍，只保留最后（最完整）的那一遍。
    ///
    /// 【为什么单独做一类，而不是并入 ④ 整句重复】
    ///   ④ 的判据是 bigram-Dice，要求两句话字面高度一致。
    ///   但真实 ASR 一定有同音错字，重录的两遍往往长这样：
    ///     "为了证明自己的朝代的合法性"  vs  "为了证明自己改朝啊"
    ///     "那难道我们就要让为时论"      vs  "那难道我们就让这些反制言论"
    ///   它们的 Dice 只有 0.45~0.50，和"偶然用词相近的正常句"拉不开差距，
    ///   用 ④ 的阈值根本抓不到（这正是第一版"只剪了空白"的直接原因）。
    ///
    ///   换用**字符集合重叠率**就能拉开：真重录 0.73~1.00，正常句 0.10~0.20。
    ///
    /// 【三重判据，缺一不可】
    ///   1. 字符重叠率够高（核心，对错字鲁棒）
    ///   2. 且「开头相同」或「bigram 也相似」（把"用词撞车"剔掉）
    ///   3. 且时间间隔在窗口内（把"隔了半分钟另起一层意思"剔掉）
    ///
    /// 【保留哪一版】
    ///   默认保留后出现的那遍（人重录时后面通常更顺）。
    ///   但若后一遍明显更短（不足前一遍的 0.75），说明它是半截废片，
    ///   这时不动手 —— 否则会把完整句删掉、留下半截。
    /// </summary>
    public static List<TextIssue> ScanRetakes(List<TranscriptSegment> segs, TextCutOptions opt)
    {
        var res = new List<TextIssue>();
        int n = segs.Count;
        if (n < 2) return res;

        // 洗一遍文本：去标点、全角转半角
        var norm = new string[n];
        for (int i = 0; i < n; i++) norm[i] = TextSimilarity.Normalize(segs[i].Text);

        var already = new HashSet<int>();   // 已被判为废片的句子，不重复处理

        for (int j = 1; j < n; j++)
        {
            string b = norm[j];
            if (b.Length < opt.RetakeMinChars) continue;

            // 往前找被这一遍"替代掉"的版本。
            // 句子已按时间排序，越往前间隔越大，超过窗口就可以停了。
            for (int i = j - 1; i >= 0; i--)
            {
                double gap = segs[j].StartSec - segs[i].EndSec;
                if (gap > opt.RetakeWindowSec) break;
                if (gap < -0.5) continue;          // 时间有重叠/倒挂，跳过

                string a = norm[i];
                if (a.Length < opt.RetakeMinChars) continue;
                if (already.Contains(i)) continue;

                // 保留更完整的那遍：后一遍明显更短就别删前一遍
                if (b.Length < a.Length * opt.RetakeKeepLongerRatio) continue;

                double ov = TextSimilarity.CharOverlap(a, b);
                if (ov < opt.RetakeSuggestCharOverlap) continue;

                double pre = TextSimilarity.PrefixRatio(a, b);
                double dice = TextSimilarity.Dice(a, b);
                if (pre < opt.RetakePrefix && dice < opt.RetakeDice) continue;

                bool auto = ov >= opt.RetakeCharOverlap;
                double conf;
                if (auto)
                {
                    // 0.75 → 0.62，1.00 → 0.97；重叠越高越敢删
                    conf = Math.Clamp(
                        0.62 + 0.35 * (ov - opt.RetakeCharOverlap) / Math.Max(1e-6, 1.0 - opt.RetakeCharOverlap),
                        0, 0.97);
                }
                else
                {
                    // 落在建议区间的，把握压在自动剪门槛之下
                    conf = Math.Clamp(
                        0.45 + 0.25 * (ov - opt.RetakeSuggestCharOverlap) / Math.Max(1e-6, opt.RetakeCharOverlap - opt.RetakeSuggestCharOverlap),
                        0, 0.60);
                }

                res.Add(new TextIssue
                {
                    StartSec = segs[i].StartSec,
                    EndSec = segs[i].EndSec,
                    Reason = CutReason.Retake,
                    Confidence = Math.Round(conf, 3),
                    AutoCut = auto && conf >= opt.RetakeAutoCutConfidence,
                    Evidence = segs[i].Text,
                    Note = $"与第 {segs[j].Index} 句重录（字重叠 {ov:F2}，开头同 {pre:F2}，隔 {gap:F1}s）",
                    SegmentIndex = segs[i].Index,
                });
                already.Add(i);
            }
        }

        return res;
    }

    /// <summary>
    /// 跨句比较。对每个句子向后看若干句，依次判断是否构成 ④ 或 ②。
    ///
    /// 为什么 ②④ 要合并扫：
    ///   两者的候选是同一批句对 (i, j)。分两遍扫会互相抢同一个句对，
    ///   —— 同一个句对可能既是"重复"又是"半截"（实际上"半截"是"部分重复"的特殊形态）。
    ///   合并后按优先级判：先看是不是整句重复，不是再看是不是半截重说。
    /// </summary>
    public static List<TextIssue> ScanAcrossSegments(List<TranscriptSegment> segs, TextCutOptions opt)
    {
        var res = new List<TextIssue>();
        int n = segs.Count;
        if (n < 2) return res;

        // 已经被当作"第二次说的版本"占用的句子，不再作为被删对象重复处理
        var claimedSecond = new HashSet<int>();

        for (int i = 0; i < n; i++)
        {
            if (claimedSecond.Contains(i)) continue;

            string ni = TextSimilarity.Normalize(segs[i].Text);
            if (ni.Length == 0) continue;

            for (int j = i + 1; j < n; j++)
            {
                var si = segs[i];
                var sj = segs[j];

                double gap = sj.StartSec - si.EndSec;
                if (gap < -0.05) continue;                       // 时间倒挂，数据有问题，跳过这对
                if (gap > Math.Max(opt.FalseStartMaxGapSec, opt.TextRepeatWindowSec)) break;

                string nj = TextSimilarity.Normalize(sj.Text);
                if (nj.Length == 0) continue;

                // ---------- ② 半截重说（先于 ④ 判定）----------
                //
                // 为什么顺序重要："半截重说"其实是"部分重复"的一种**特殊形态**
                // —— 短句被长句包住。先判它，能给到更精确的诊断；
                // 不满足（长度太接近）才落到 ④ 判成普通的重复。
                if (opt.DetectFalseStart && gap <= opt.FalseStartMaxGapSec)
                {
                    int minLen = Math.Min(ni.Length, nj.Length);
                    if (minLen < opt.FalseStartMinChars) continue;

                    double overlap = TextSimilarity.Overlap(ni, nj);
                    if (overlap < opt.FalseStartOverlap) continue;

                    double prefix = TextSimilarity.PrefixRatio(ni, nj);
                    if (prefix < opt.FalseStartPrefix) continue;

                    // 必须"长度明显不同"才叫半截 —— 长度接近那是重复，走 ④
                    int maxLen = Math.Max(ni.Length, nj.Length);
                    double lenGap = (double)(maxLen - minLen) / maxLen;
                    if (lenGap < opt.FalseStartLenGap) continue;

                    // 删掉**更短的那句**（它是失败的尝试）
                    bool firstIsShort = ni.Length <= nj.Length;
                    var victim = firstIsShort ? si : sj;
                    string victimText = firstIsShort ? ni : nj;

                    double conf = 0.72
                                  + 0.15 * Clamp01((prefix - opt.FalseStartPrefix) / Math.Max(1e-6, 1 - opt.FalseStartPrefix))
                                  + 0.08 * Clamp01((overlap - opt.FalseStartOverlap) / Math.Max(1e-6, 1 - opt.FalseStartOverlap))
                                  - 0.10 * Clamp01(gap / Math.Max(1e-6, opt.FalseStartMaxGapSec));
                    conf = Math.Clamp(conf, 0, 0.99);

                    res.Add(new TextIssue
                    {
                        StartSec = victim.StartSec,
                        EndSec = victim.EndSec,
                        Reason = CutReason.FalseStart,
                        Confidence = Math.Round(conf, 3),
                        AutoCut = conf >= opt.FalseStartAutoCutConfidence,
                        Evidence = victimText,
                        Note = $"半截重说：这句没说完，第 {(firstIsShort ? sj.Index : si.Index)} 句重说了一遍更完整的版本",
                        SegmentIndex = victim.Index,
                    });
                    claimedSecond.Add(firstIsShort ? j : i);
                    break;
                }

                // ---------- ④ 整句重复（长度接近，不属于"半截"的一类）----------
                if (opt.DetectTextRepeat
                    && gap <= opt.TextRepeatWindowSec
                    && Math.Min(ni.Length, nj.Length) >= opt.TextRepeatMinChars)
                {
                    double dice = TextSimilarity.Dice(ni, nj);
                    if (dice >= opt.TextRepeatDice)
                    {
                        // 删掉**先说的那次**：后一次通常是更顺畅的版本
                        double conf = Math.Clamp(0.62 + 0.30 * (dice - opt.TextRepeatDice) / Math.Max(1e-6, 1.0 - opt.TextRepeatDice), 0, 0.99);
                        res.Add(new TextIssue
                        {
                            StartSec = si.StartSec,
                            EndSec = si.EndSec,
                            Reason = CutReason.Repeat,
                            Confidence = Math.Round(conf, 3),
                            AutoCut = conf >= opt.TextRepeatAutoCutConfidence,
                            Evidence = ni,
                            Note = $"与第 {sj.Index} 句重复（相似度 {dice:F2}，相隔 {gap:F1}s）",
                            SegmentIndex = si.Index,
                        });
                        claimedSecond.Add(j);
                        break;
                    }
                }
            }
        }

        return res;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
}
