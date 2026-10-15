// ============================================================
// ScriptAligner.cs - 「对稿」：拿你写的稿子当标准答案，对一遍录音
// ============================================================
//
// 【这一步在整个流程里的位置】
//
//   写作台 → script.md ─┐
//                       ├─→ 对稿（本文件）→ 粗剪决策
//   录音 → transcript.json ┘
//
//   以前的粗剪只看录音（波形 + 转写稿）。现在多了一位"标准答案"：
//   你写的那份原稿。于是"这句是不是说错了重录的"这种问题，从"猜"
//   变成了"对答案" —— 这是本文件存在的全部理由。
//
// 【为什么"标准答案"这么值钱 —— 一个具体的例子】
//
//   录音里出现了两遍几乎一样的话。光看录音，两种可能：
//     (a) 他说错了重录一遍      → 前一遍该删
//     (b) 他在有意地重复强调    → 两遍都该留
//   纯音频/纯文本判断分不清，只能赌一个。
//
//   但翻一下稿子就清楚了：
//     · 稿子上这句话只写了一次 → 必然是 (a)，前一遍是废片，删。
//     · 稿子上本来就写了两遍   → 那是 (b)，一个字都别动。
//   —— 标准答案把"赌"变成了"查"。
//
// 【怎么做 —— 顺序对齐，不是校对】
//
//   要点：稿子和录音是**同一个顺序**（照着稿子念，不会倒着念）。
//   所以这不是"随便找最像的配对"，而是"沿着时间轴从左到右，
//   把稿子的第 1、2、3…句，依次对到录音的若干段上"。
//   技术上是一条单调对齐（monotonic alignment），用动态规划求最优。
//
//   允许四件事同时发生：
//     ① 一句稿子 ↔ 一段录音        （正常）
//     ② 一句稿子 ↔ 连续好几段录音  （ASR 把长句切开了；或重录了好几遍）
//     ③ 稿子有、录音里没有         （忘词 / 跳过去了）
//     ④ 录音有、稿子里没有         （临场发挥 / 加词）
//
// 【精度只到"句级"，这是刻意的】
//
//   对稿不追求逐字精确。理由：口播是口语，你不可能一字不差地念。
//   "这句话大体上就是稿子上那句" 就够用 —— 因为我们只用它做两件事：
//     · 判断重录（要的是"是不是同一句"，不是"有没有念错字"）
//     · 列偏差给人工看（人一眼就懂）
//   追求字级精度会引入大量假阳性，反而更糟。
//
// 【永远降级 —— 对不上就退回原样】
//
//   稿子版本对不上、或者本来就没写稿，对稿就什么都不动，
//   粗剪回到"只看录音"的老做法。下限 = 今天，上限 = 省掉大半精剪。
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>从稿子里拆出来的一句话。</summary>
public sealed class ScriptSentence
{
    /// <summary>在稿子里的序号（0 起）。</summary>
    public int Index { get; init; }

    /// <summary>原样文字（界面上显示用，带标点）。</summary>
    public string Text { get; init; } = "";

    /// <summary>归一化之后的文字（只剩字，用来比对）。</summary>
    public string Norm { get; init; } = "";
}

/// <summary>
/// 对稿结果。
///
/// 【为什么除了给人看的 Check，还要留一份"逐段归属"】
///   给别人看的是一句话结论（对齐率 96%、5 处偏差）；
///   给程序看的得是"录音的第 17 段属于稿子第 9 句、相似度 0.83"这种明细。
///   两者都要，前者进 CutPlan 存档，后者只在内存里用（下轮的上下文）。
/// </summary>
public sealed class ScriptAlignResult
{
    /// <summary>稿子找到了没。</summary>
    public bool Available { get; set; }

    /// <summary>这份对齐可不可信（过线才用它改剪法）。</summary>
    public bool Usable { get; set; }

    /// <summary>给 CutPlan 存档的那份概况。</summary>
    public ScriptCheck Check { get; set; } = new ScriptCheck();

    /// <summary>
    /// 逐段的稿子归属：录音第 j 段对应稿子第几句（-1 = 稿子上没有，属临场发挥）。
    /// 只在对齐可信时才有意义。
    /// </summary>
    public List<int> SegmentScriptIndex { get; } = new List<int>();

    /// <summary>
    /// 逐段与所属稿句的相似度（0~1）。未归属的为 0。
    /// </summary>
    public List<double> SegmentSimilarity { get; } = new List<double>();

    /// <summary>
    /// 对稿确认出来的重录（同一句稿子录了多遍）—— 已经是"待删的前几遍"，
    /// 直接能进 AutoCutEngine 的问题列表。
    /// </summary>
    public List<TextIssue> RetakeIssues { get; } = new List<TextIssue>();
}

/// <summary>「对稿」对齐器。</summary>
public static class ScriptAligner
{
    // ============================================================
    // 可调参数（都写成静态字段，方便日后实测标定，跟 TranscriptAligner 一个风格）
    // ============================================================

    /// <summary>
    /// 一段录音要跟一句稿子"像到什么程度"才算对上（默认 0.45）。
    ///
    /// 定得偏低是刻意的：口播是口语，你不可能一字不差地念稿。
    /// 而且 ASR 本身有错字 —— 阈值太高，"其实念的就是这句"会被判成"没念"。
    /// 至于"会不会滥配"，由后面的顺序约束（单调对齐）兜住：
    /// 就算某段跟好几句都像，它也只能落到顺序上最合理的那一句。
    /// </summary>
    public static double MinMatchSim { get; set; } = 0.45;

    /// <summary>
    /// 录音里一段"没配到任何稿句"的代价（默认 0.05）。
    /// 临场发挥是正常事，所以只给很小的代价 —— 不鼓励乱配，但也不惩罚加词。
    /// </summary>
    public static double FreeSegPenalty { get; set; } = 0.05;

    /// <summary>
    /// 稿子里一句"整个没录"的代价（默认 0.50）。
    /// 比上面大得多：忘词/跳过是明显异常，算法应该优先去配它，
    /// 实在配不上才认了这句没说。
    /// </summary>
    public static double SkipScriptPenalty { get; set; } = 0.50;

    /// <summary>
    /// 同一句稿子被连续好几段录音"命中"时的额外奖励（默认 0.15，按段数累加）。
    ///
    /// 【为什么要有这个奖励】
    ///   一句稿子录了 3 遍：算法有两条路走 ——
    ///     (a) 把 3 段都算给这一句（认出来这是重录）
    ///     (b) 只算 1 段，另外 2 段当成"临场发挥"
    ///   不给奖励的话，(b) 的代价更小，算法会选 (b) —— 我们就把重录漏掉了。
    ///   加上奖励之后，"扎堆"才成为更划算的选择，重录才抓得住。
    /// </summary>
    public static double RetakeClusterBonus { get; set; } = 0.15;

    /// <summary>
    /// 一句稿子最多允许配到几段录音（默认 6）。
    ///
    /// 【为什么要设上限】
    ///   一来是防跑偏（一句配 20 段显然是算法抽风）；
    ///   二来是控复杂度 —— 动态规划里枚举"配了几段"的范围就是它。
    ///   实测一句重录 3~4 遍就到顶了，6 足够宽。
    /// </summary>
    public static int MaxRunSegments { get; set; } = 6;

    /// <summary>
    /// 一句稿子配到的一串录音里，夹进一段"跟稿子不像"的记录，要罚多少分（默认 0.15）。
    ///
    /// 【为什么必须有这一条 —— 踩过的坑】
    ///   只奖励"扎堆"、不惩罚"夹带"的话，算法会把一段稿外发挥（比如"呃这个……"）
    ///   顺手吸进旁边那句的串里 —— 因为吸进去不花代价，还省了"临场发挥"那点罚分。
    ///   结果是"稿外发挥"永远数不出来，偏差清单永远空着。
    ///
    ///   罚分定得比 FreeSegPenalty（0.05）高，差额才够让算法宁可把它单独列成"临场发挥"。
    /// </summary>
    public static double NonMatchInRunPenalty { get; set; } = 0.15;

    /// <summary>
    /// 破并列用的"匹配质量"微奖励（默认 0.03，按"这一串里每一段与稿句的相似度之和"累加）。
    ///
    /// 【为什么需要 —— 稿子里有两句长得很像时】
    ///   稿子写的是「第一句…」「第二句…」「第三句…」，录音里的"第二句"其实
    ///   跟"第一句"也有七八分像。于是可能出现两种**得分完全相同**的对齐：
    ///     甲：录音那段 → 它自己那一句（相似度 1.00）
    ///     乙：录音那段 → 被吸进旁边那句的串里（相似度 0.79）
    ///   两种剪出来的内容其实一样（都不出错），但甲更"讲道理"：
    ///   它把每一段配给了**最像它的那一句**。
    ///   加一点点"像得越准、分越高"的奖励，并列时就会自动选甲。
    ///
    /// 系数必须很小 —— 它只负责在并列时撬动天平，不该影响正常的最优解。
    /// </summary>
    public static double MatchQualityTieBreak { get; set; } = 0.03;

    /// <summary>
    /// 对齐率低于这个值就判定"稿子跟录音对不上"，自动退回不做对稿（默认 0.50）。
    ///
    /// 【为什么这道闸必须有】
    ///   最常见的翻车方式不是"算错"，而是"拿错了稿子" ——
    ///   稿子改过一版没同步、或者录音是上一期的。
    ///   这时如果还硬按稿子剪，会把好好的内容当成"重录"删掉。
    ///   宁可退回不做，也不能拿错答案判卷。
    /// </summary>
    public static double MinAlignmentRatio { get; set; } = 0.50;

    /// <summary>
    /// 对齐可信的最低"支持度"：至少要有这么多句真的对上了（默认 3）。
    /// 防止"稿子只有 2 句、恰好都对上"就被判定可信。
    /// </summary>
    public static int MinAlignedSentences { get; set; } = 3;

    /// <summary>动态规划规模上限（句数 × 段数）。超了就不做对稿，避免卡死。</summary>
    public static int MaxDpCells { get; set; } = 4_000_000;

    // ============================================================
    // 一、解析稿子（script.md → 一句话一句话）
    // ============================================================

    /// <summary>
    /// 把 script.md 正文拆成一句一句。
    ///
    /// 【为什么要"洗"一遍】
    ///   script.md 是给人看的 Markdown，里面有标题符号、加粗星号、列表点、引用杠……
    ///   这些东西一个字都不会被念出来。不洗掉，"**唐代**的美学" 跟录音里的
    ///   "唐代的美学" 会被判成两句不同的话。
    ///
    /// 【按什么断句 —— 一行一句为底，行内再按句末标点切】
    ///   口播稿的现实写法有两大类：
    ///     (a) 一句一行（最常见，尤其是列表式的稿子）
    ///     (b) 整段写成一行，靠标点断句
    ///   两类都要照顾到，所以规矩是：**每一行先当一个单位**，行内再按 。！？ 切。
    ///
    ///   ⚠ 曾经写成"把连续几行攒成一段再切"，结果翻车：
    ///     列表式的稿子每行都不带句号，攒完就变成"第一点 第二点 第三点"一整句，
    ///     稿子的句数虚少，对稿自然对不准。行级断句才对。
    ///   （代价：如果有人**手工硬换行**把一句话拆成两行，会多切一句。
    ///     手写口播稿几乎不会这么干，认了。）
    /// </summary>
    public static List<ScriptSentence> ParseScript(string? markdown)
    {
        var result = new List<ScriptSentence>();
        if (string.IsNullOrWhiteSpace(markdown)) return result;

        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        bool inFence = false;   // 在 ``` 代码块里

        void AddSentence(string s)
        {
            string norm = TextSimilarity.Normalize(s);
            if (norm.Length == 0) return;          // 纯符号，没内容
            result.Add(new ScriptSentence { Index = result.Count, Text = s.Trim(), Norm = norm });
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            // ---- 代码围栏：整段跳过（口播稿里通常没有，纯属保险）----
            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            // ---- 空行 / 分隔线 / 表格线：不是内容 ----
            if (line.Length == 0) continue;
            if (IsRule(line)) continue;

            string cleaned = CleanMarkdownLine(line);
            if (cleaned.Length == 0) continue;

            // 行内再按句末标点切（一行里写了三句话的情况）
            foreach (string s in SplitSentences(cleaned)) AddSentence(s);
        }

        return result;
    }

    /// <summary>把一行 Markdown 洗成"只剩会被念出来的字"。</summary>
    private static string CleanMarkdownLine(string line)
    {
        string s = line.TrimStart();

        // 引用 >
        while (s.StartsWith(">")) s = s.Substring(1).TrimStart();

        // 标题 #
        int h = 0;
        while (h < s.Length && s[h] == '#') h++;
        if (h > 0 && h < s.Length && s[h] == ' ') s = s.Substring(h + 1).TrimStart();

        // 列表点：- / * / + / 1. / 1)
        s = StripListMarker(s);

        // 中文序号：一、 / （一） / 1、
        s = StripCjkOrdinal(s);

        // 图片 ![alt](url) → alt（必须先于链接处理，否则会被当成链接）
        s = System.Text.RegularExpressions.Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        // 链接 [text](url) → text
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        // 强调 / 行内代码符号：删掉即可（它们本身不会被念）
        s = s.Replace("**", "").Replace("__", "").Replace("~~", "")
             .Replace("`", "").Replace("*", "").Replace("_", "");
        // 残留的 HTML 标签
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");

        return s.Trim();
    }

    /// <summary>剥掉行首的列表点（"- " / "* " / "+ " / "1. " / "1) "）。</summary>
    private static string StripListMarker(string s)
    {
        if (s.Length >= 2 && (s[0] == '-' || s[0] == '*' || s[0] == '+') && s[1] == ' ')
            return s.Substring(2).TrimStart();

        int i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i > 0 && i < s.Length && (s[i] == '.' || s[i] == ')' || s[i] == '、') )
            return s.Substring(i + 1).TrimStart();

        return s;
    }

    /// <summary>剥掉行首的中文序号（"一、" / "（二）" / "3、"）。</summary>
    private static string StripCjkOrdinal(string s)
    {
        const string digits = "零一二三四五六七八九十百千";
        int i = 0;

        // 圆括号包裹的，如 （一）
        bool paren = false;
        if (s.Length > 0 && (s[0] == '（' || s[0] == '(')) { paren = true; i = 1; }

        int start = i;
        while (i < s.Length && (digits.IndexOf(s[i]) >= 0 || char.IsDigit(s[i]))) i++;
        if (i == start) return s;   // 没吃到序号

        if (paren)
        {
            if (i < s.Length && (s[i] == '）' || s[i] == ')'))
                return s.Substring(i + 1).TrimStart();
            return s;
        }

        // 不带括号的，后面必须跟顿号/点号才算序号（否则"一"可能是正文）
        if (i < s.Length && (s[i] == '、' || s[i] == '.' || s[i] == '．'))
            return s.Substring(i + 1).TrimStart();

        return s;
    }

    /// <summary>整行是不是分隔线 / 表格线这类"纯装饰"。</summary>
    private static bool IsRule(string s)
    {
        int dash = 0, star = 0, under = 0, other = 0;
        foreach (char c in s)
        {
            switch (c)
            {
                case '-': dash++; break;
                case '*': star++; break;
                case '_': under++; break;
                case ' ':
                case '|':
                case ':': break;
                default: other++; break;
            }
        }
        if (other > 0) return false;
        return dash >= 3 || star >= 3 || under >= 3;
    }

    /// <summary>把一段文字按句末标点切成若干句。</summary>
    private static IEnumerable<string> SplitSentences(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            sb.Append(c);
            if (IsSentenceEnd(c))
            {
                string one = sb.ToString().Trim();
                sb.Clear();
                if (one.Length > 0) yield return one;
            }
        }
        string last = sb.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    private static bool IsSentenceEnd(char c) => c == '。' || c == '！' || c == '？'
                                                 || c == '!' || c == '?' || c == '…';

    // ============================================================
    // 二、顺序对齐（动态规划）
    // ============================================================

    /// <summary>
    /// 把稿子和录音对一遍。
    /// </summary>
    /// <param name="transcript">录音的转写稿（必须带时间码，且已过校验）</param>
    /// <param name="scriptMarkdown">script.md 的正文字符串</param>
    /// <param name="opt">语义层参数（借它取"重录"那几个阈值，可空）</param>
    public static ScriptAlignResult Align(Transcript? transcript, string? scriptMarkdown, TextCutOptions? opt = null)
    {
        var res = new ScriptAlignResult();
        var check = new ScriptCheck { Available = !string.IsNullOrWhiteSpace(scriptMarkdown) };
        res.Check = check;
        res.Available = check.Available;   // "有没有稿子" —— 后面即使对不了，这个事实也不变

        var script = ParseScript(scriptMarkdown);
        check.SentenceCount = script.Count;

        // ---- 各种"没法对"的情况：说清楚原因，然后什么都不动 ----
        if (transcript?.Segments == null || transcript.Segments.Count == 0)
        {
            if (check.Available) check.Warnings.Add("没有转写稿，无法对稿");
            return res;   // Available 保持"有没有稿子"，Used 一律 false
        }
        if (script.Count == 0)
        {
            check.Warnings.Add(check.Available ? "稿子里没找到可用的句子" : "没有稿子（script.md）");
            return res;
        }

        var segs = transcript.Segments;

        // 归一化每一段录音
        var segNorm = new string[segs.Count];
        for (int j = 0; j < segs.Count; j++)
            segNorm[j] = TextSimilarity.Normalize(segs[j].Text);

        int M = script.Count, N = segs.Count;

        // 规模保护：超了就放弃对稿（宁可退回老做法，不能让界面卡死）
        if ((long)M * N > MaxDpCells)
        {
            check.Warnings.Add($"稿子 {M} 句 × 录音 {N} 段 规模过大，已跳过对稿");
            return res;
        }

        // ---- 相似度矩阵 ----
        var sim = new double[M, N];
        for (int i = 0; i < M; i++)
            for (int j = 0; j < N; j++)
                sim[i, j] = Similarity(script[i].Norm, segNorm[j]);

        // ---- 动态规划 ----
        //
        // dp[i, j] = "用前 i 句稿子、去解释前 j 段录音"的最好得分。
        // 每一步只有三种走法：
        //   ① 这一段是临场发挥，不配给任何稿句   → dp[i, j-1] - FreeSegPenalty
        //   ② 这句稿子整个没录                    → dp[i-1, j] - SkipScriptPenalty
        //   ③ 这句稿子配到"从第 k 段到第 j 段"这一串 → dp[i-1, k] + RunScore(...)
        // 求最大得分那一条路，就是最优对齐。
        var dp = new double[M + 1, N + 1];
        var choice = new byte[M + 1, N + 1];   // 0=临场发挥 1=跳过稿句 2=配到一串
        var choiceK = new int[M + 1, N + 1];   // choice==2 时，这一串从第几段开始

        for (int i = 1; i <= M; i++) { dp[i, 0] = dp[i - 1, 0] - SkipScriptPenalty; choice[i, 0] = 1; }
        for (int j = 1; j <= N; j++) { dp[0, j] = dp[0, j - 1] - FreeSegPenalty; choice[0, j] = 0; }

        for (int i = 1; i <= M; i++)
        {
            for (int j = 1; j <= N; j++)
            {
                // ① 临场发挥
                double best = dp[i, j - 1] - FreeSegPenalty;
                byte bc = 0;
                int bk = 0;

                // ② 这句稿子没录
                double skip = dp[i - 1, j] - SkipScriptPenalty;
                if (skip > best) { best = skip; bc = 1; }

                // ③ 这句稿子配到一段（或连续几段）录音
                int lo = Math.Max(0, j - MaxRunSegments);
                for (int k = lo; k <= j - 1; k++)
                {
                    double run = RunScore(i - 1, k, j, sim, script, segNorm);
                    if (double.IsNegativeInfinity(run)) continue;
                    double v = dp[i - 1, k] + run;
                    if (v > best) { best = v; bc = 2; bk = k; }
                }

                dp[i, j] = best;
                choice[i, j] = bc;
                choiceK[i, j] = bk;
            }
        }

        // ---- 回溯：把"最优路径"还原成"每一段录音归谁" ----
        var assign = new int[N];
        var assignSim = new double[N];
        for (int j = 0; j < N; j++) assign[j] = -1;

        int ci = M, cj = N;
        while (ci > 0 || cj > 0)
        {
            byte c = choice[ci, cj];
            if (ci == 0) c = 0;        // 稿句用完了，只剩临场发挥
            else if (cj == 0) c = 1;   // 录音用完了，只剩跳过的稿句

            if (c == 0) { assign[cj - 1] = -1; cj--; }
            else if (c == 1) { ci--; }
            else
            {
                int k = choiceK[ci, cj];
                for (int t = k; t < cj; t++) { assign[t] = ci - 1; assignSim[t] = sim[ci - 1, t]; }
                cj = k;
                ci--;
            }
        }

        for (int j = 0; j < N; j++)
        {
            res.SegmentScriptIndex.Add(assign[j]);
            res.SegmentSimilarity.Add(Math.Round(assignSim[j], 3));
        }

        // ---- 统计 ----
        var sentSegs = new List<List<int>>(M);
        for (int i = 0; i < M; i++) sentSegs.Add(new List<int>());
        int adLib = 0;
        for (int j = 0; j < N; j++)
        {
            if (assign[j] >= 0) sentSegs[assign[j]].Add(j);
            else adLib++;
        }

        int aligned = 0;
        for (int i = 0; i < M; i++) if (sentSegs[i].Count > 0) aligned++;

        check.AlignedCount = aligned;
        check.AdLibCount = adLib;
        check.AlignmentRatio = M > 0 ? Math.Round((double)aligned / M, 3) : 0;

        // ---- 判定这份对齐能不能用 ----
        res.Usable = check.AlignmentRatio >= MinAlignmentRatio && aligned >= MinAlignedSentences;
        check.Used = res.Usable;
        if (!res.Usable && res.Available)
            check.Warnings.Add(
                $"对稿可信度不足（对齐率 {check.AlignmentRatio:P0}，{aligned}/{M} 句）→ 本轮不按稿子改剪法");

        // ---- 找重录（同一句稿子配到了好几段录音）----
        var retakeOpt = opt ?? TextCutOptions.Balanced();
        DetectRetakes(segs, segNorm, sentSegs, retakeOpt, res);
        check.RetakeCount = res.RetakeIssues.Count;

        // ---- 生成给人看的偏差清单 ----
        BuildDeviations(segs, segNorm, script, sentSegs, assign, assignSim, res);

        return res;
    }

    /// <summary>
    /// 一句稿子配到"第 k 段到第 j 段"这一串录音，值多少分。
    ///
    /// 三部分：
    ///   · 基础分 = 这一串里"最像"的那一段有多像（取最大值，不取平均）——
    ///     因为一串里只要有一段是**完整念对了**这句稿子，这句就算念了；
    ///     其余几段是重录的废片，不该拉低分数。
    ///   · 扎堆奖励 = 这一串里有几段"各自都像这句稿子"，每多一段加一点分。
    ///     见 RetakeClusterBonus 的说明：没有它，重录会被算法当成"临场发挥"漏掉。
    ///   · 夹带惩罚 = 这一串里混进来的、跟稿子不像的段，每段扣一点分。
    ///     见 NonMatchInRunPenalty 的说明：没有它，稿外发挥会被无声吞掉。
    /// </summary>
    private static double RunScore(int i, int k, int j, double[,] sim,
                                   List<ScriptSentence> script, string[] segNorm)
    {
        double best = 0;
        double sumQual = 0;   // 串里"够像"的那几段的相似度之和（只用于破并列）
        int qualifying = 0;
        for (int t = k; t < j; t++)
        {
            double v = sim[i, t];
            if (v > best) best = v;
            if (v >= MinMatchSim) { qualifying++; sumQual += v; }
        }

        if (best < MinMatchSim) return double.NegativeInfinity;   // 这一段串跟这句稿子不像，别配

        int strays = (j - k) - qualifying;   // 串里"跟稿子不像"的段数
        return best
               + RetakeClusterBonus * (qualifying - 1)
               - NonMatchInRunPenalty * strays
               + MatchQualityTieBreak * sumQual;
    }

    /// <summary>
    /// 三把尺子加权，算"这段录音跟这句稿子有多像"。0~1。
    ///
    /// 【为什么是这三把，各自的权重为什么这么给】
    ///   Dice（bigram 整体相似，0.5）—— 主判据。它对称，又天然惩罚长度悬殊，
    ///        所以"两句明明不一样长"会自然压低分数，不容易滥配。
    ///   LCS 比上**较长**那句（0.35）—— 管语序。同上，用较长的当分母，
    ///        是为了不让"两个字恰好出现"就冒充高相似（那正是"较短当分母"的毛病）。
    ///   CharOverlap 字符集合重叠（0.15）—— 抗 ASR 错字。权重给最低，
    ///        因为它丢了语序，只能当辅助。
    /// </summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return 0;   // 一两个字说明不了什么，直接不配

        double dice = TextSimilarity.Dice(a, b);
        double lcsLonger = LcsLongerRatio(a, b);
        double ch = TextSimilarity.CharOverlap(a, b);
        return 0.50 * dice + 0.35 * lcsLonger + 0.15 * ch;
    }

    /// <summary>
    /// 最长公共子序列长度 ÷ 较长的那个长度。
    /// （TextSimilarity.LcsRatio 是除以较短的那句，语义不同 —— 这里要的是"覆盖了多半"。）
    /// </summary>
    private static double LcsLongerRatio(string a, string b)
    {
        int longer = Math.Max(a.Length, b.Length);
        if (longer == 0) return 0;
        // 借 LcsRatio 的分子：它返回 LCS / min，乘回 min 就还原成 LCS 长度
        int shorter = Math.Min(a.Length, b.Length);
        double r = TextSimilarity.LcsRatio(a, b);
        long lcs = (long)Math.Round(r * shorter);
        return Math.Clamp((double)lcs / longer, 0, 1);
    }

    // ============================================================
    // 三、从对齐结果里"抓重录"
    // ============================================================

    /// <summary>
    /// 在"同一句稿子配到的多段录音"里找重录。
    ///
    /// 【判据：既要'都像稿子'，又要'互相像'】
    ///   "都像稿子"是前提（DP 已经保证了）。
    ///   "互相像"是必需的补充 —— 因为一句长稿子可能被 ASR 切成前后两半：
    ///     稿子：「我们要讲的是唐代的美学，它有三个特点」
    ///     录音：①「我们要讲的是唐代的美学」 ②「它有三个特点」
    ///   这两段**各自**都像稿子，但它们是**互补**的，不是重复 —— 两段都得留。
    ///   只有"两段互相也像"（说了两遍差不多的话），才是重录，前一遍才该删。
    ///
    /// 【保留哪一遍 —— 保后不保前】
    ///   同一句稿子录了好几遍，人重录的动机就是"前面那遍说坏了"。
    ///   所以默认保留**最后**那一遍。
    ///   但若后一遍明显更短（不足前一遍的 0.70），说明它是半截废片，
    ///   这时不动手 —— 否则会把完整句删掉、留下半截。
    /// </summary>
    private static void DetectRetakes(
        List<TranscriptSegment> segs, string[] segNorm,
        List<List<int>> sentSegs,
        TextCutOptions opt, ScriptAlignResult res)
    {
        var dropped = new HashSet<int>();

        for (int i = 0; i < sentSegs.Count; i++)
        {
            var list = sentSegs[i];
            if (list.Count < 2) continue;

            for (int x = 0; x + 1 < list.Count; x++)
            {
                int a = list[x];        // 前一遍（候选废片）
                int b = list[x + 1];    // 后一遍（候选保留）
                if (dropped.Contains(a)) continue;

                string na = segNorm[a], nb = segNorm[b];
                if (na.Length < opt.RetakeMinChars || nb.Length < opt.RetakeMinChars) continue;

                // 后一遍明显更短 → 它是半截，别删前一遍
                if (nb.Length < na.Length * opt.RetakeKeepLongerRatio) continue;

                // "互相像"：字符集合重叠够高，且开头相同或 bigram 也像
                double ov = TextSimilarity.CharOverlap(na, nb);
                if (ov < opt.RetakeSuggestCharOverlap) continue;

                double pre = TextSimilarity.PrefixRatio(na, nb);
                double dice = TextSimilarity.Dice(na, nb);
                if (pre < opt.RetakePrefix && dice < opt.RetakeDice) continue;

                // 把握：重叠越高越敢删；再乘一个"稿子确认"加成
                bool strong = ov >= opt.RetakeCharOverlap;
                double conf = strong
                    ? Math.Clamp(0.66 + 0.28 * (ov - opt.RetakeCharOverlap) / Math.Max(1e-6, 1.0 - opt.RetakeCharOverlap), 0, 0.97)
                    : Math.Clamp(0.46 + 0.22 * (ov - opt.RetakeSuggestCharOverlap) / Math.Max(1e-6, opt.RetakeCharOverlap - opt.RetakeSuggestCharOverlap), 0, 0.60);

                res.RetakeIssues.Add(new TextIssue
                {
                    StartSec = segs[a].StartSec,
                    EndSec = segs[a].EndSec,
                    Reason = CutReason.Retake,
                    Confidence = Math.Round(conf, 3),
                    AutoCut = strong && conf >= opt.RetakeAutoCutConfidence,
                    Evidence = segs[a].Text,
                    Note = $"对稿：稿子第 {i + 1} 句只写了一次，这里录了多遍 —— 保留后一遍（字重叠 {ov:F2}）",
                    SegmentIndex = segs[a].Index,
                });
                dropped.Add(a);
            }
        }
    }

    // ============================================================
    // 四、偏差清单（纯给人看，不参与机器决策）
    // ============================================================

    /// <summary>
    /// 把"稿子没录的""临场发挥的"列出来。这两类的处理原则是：
    /// **只报告，不动手** —— 忘词和加词都可能是你要的，机器不该替你决定。
    /// </summary>
    private static void BuildDeviations(
        List<TranscriptSegment> segs, string[] segNorm,
        List<ScriptSentence> script, List<List<int>> sentSegs,
        int[] assign, double[] assignSim, ScriptAlignResult res)
    {
        var check = res.Check;
        const int MaxList = 60;   // 列表别太长，界面上也没人看得完

        // ---- ① 稿子上有、录音里没有（忘词 / 跳过）----
        for (int i = 0; i < script.Count && check.Deviations.Count < MaxList; i++)
        {
            if (sentSegs[i].Count > 0) continue;

            // 时间给不出来（这句压根没说），用左右邻居夹出个"大概位置"帮助定位
            double at = GuessMissingTime(i, sentSegs, segs);

            check.Deviations.Add(new ScriptDeviation
            {
                Kind = "notSpoken",
                SourceStartSec = Math.Round(at, 3),
                SourceEndSec = Math.Round(at, 3),
                Script = script[i].Text,
                Note = "稿子上有这一句，录音里没找到 —— 可能忘词或跳过去了",
            });
        }

        // ---- ② 录音里有、稿子上没有（临场发挥 / 加词）----
        for (int j = 0; j < segs.Count && check.Deviations.Count < MaxList; j++)
        {
            if (assign[j] >= 0) continue;
            string t = segs[j].Text?.Trim() ?? "";
            if (t.Length == 0) continue;

            check.Deviations.Add(new ScriptDeviation
            {
                Kind = "adLib",
                SourceStartSec = Math.Round(segs[j].StartSec, 3),
                SourceEndSec = Math.Round(segs[j].EndSec, 3),
                Spoken = t,
                Note = "录音里有这句，稿子上没有 —— 像是临场发挥或加词",
            });
        }

        // ---- ③ 重录（同一句录了多遍）----
        foreach (var it in res.RetakeIssues)
        {
            if (check.Deviations.Count >= MaxList) break;
            check.Deviations.Add(new ScriptDeviation
            {
                Kind = "retake",
                SourceStartSec = Math.Round(it.StartSec, 3),
                SourceEndSec = Math.Round(it.EndSec, 3),
                Spoken = it.Evidence,
                Similarity = it.Confidence,
                Note = it.Note ?? "同一句录了多遍",
            });
        }
    }

    /// <summary>
    /// 猜一个"没录的那句稿子本该出现在哪一秒"。
    /// 做法：取它前面最近一句已录稿句的结束时间，和后面最近一句已录稿句的开始时间，
    ///       两个时间之间（大概率就是它被跳过的那段）。
    /// </summary>
    private static double GuessMissingTime(int i, List<List<int>> sentSegs, List<TranscriptSegment> segs)
    {
        // 往前找
        for (int p = i - 1; p >= 0; p--)
        {
            if (sentSegs[p].Count == 0) continue;
            int last = sentSegs[p][^1];
            return segs[last].EndSec;
        }
        // 往后找
        for (int q = i + 1; q < sentSegs.Count; q++)
        {
            if (sentSegs[q].Count == 0) continue;
            int first = sentSegs[q][0];
            return segs[first].StartSec;
        }
        return 0;
    }
}
