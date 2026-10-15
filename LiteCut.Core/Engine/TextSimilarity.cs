// ============================================================
// TextSimilarity.cs - 两串中文文字"有多像"
// ============================================================
//
// 【为什么需要它】
//
//   波形能比音，比不了字。有了转写稿之后，"这句话是不是重复了"
//   这个问题就从"比两条曲线"变成了"比两串文字" —— 后者准得多。
//
//   这份文件提供四把尺子，各有各的用处：
//
//     Dice       —— 整体相似度。判断"这两句是不是同一句"。
//     Overlap    —— 覆盖度。判断"这句是不是被那句包住了"（管长不管短）。
//     LcsRatio   —— 最长公共子序列比。判断"这两句是不是讲同一件事"。
//     PrefixLen  —— 公共前缀字数。专治"说到一半卡住又重说"。
//
// 【为什么用字符 bigram，而不是分词】
//
//   中文没有空格，分词要先挂词典（jieba 之类的），多一个依赖就多一处崩的可能。
//   而"字的两两组合"（bigram）天生能表达词的边界感：
//     "讲的是唐代" → [讲的, 的是, 是唐, 唐代]
//   两句只要有共同的词，bigram 就会重合；字序不同就不重合。
//   实测这类任务上 bigram 的效果跟分词接近，但不依赖任何外部库。
//   —— 跨平台考虑：LiteCut.Core 要能原样在 Mac 上跑，依赖越少越好。
//
// 【⚠️ 归一化一定要在最前面做】
//
//   转写稿里混着标点、空格、全角符号、大小写。
//   不归一化，"唐代。" 和 "唐代" 会被判成两句不同的话。
//   而且不同转写引擎的标点习惯不一样 —— 有的是「，」有的是「,」。
//
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut.Core.Engine;

/// <summary>文本相似度工具。</summary>
public static class TextSimilarity
{
    // ============================================================
    // 归一化
    // ============================================================

    /// <summary>
    /// 把一段话洗成"只剩字"的形式，供后续比较用。
    ///
    /// 做四件事：全角转半角 → 大写转小写 → 丢掉标点空格符号 → 丢掉拟声重复号
    /// 例："其实呢， 我们就是说 —— 唐代的美学。"
    ///     → "其实呢我们就是说唐代的美学"
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char raw in text)
        {
            char c = raw;

            // 全角 → 半角（Ａ-Ｚ ０-９ 等，U+FF01~U+FF5E）
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);
            // 全角空格 → 普通空格（其实下一步就会被丢掉，写出来是为了说明到过这里）
            else if (c == '　') c = ' ';
            // 大写 → 小写
            else if (c >= 'A' && c <= 'Z') c = (char)(c + 32);

            // 丢掉：空白、标点（含中文标点，.NET 的 IsPunctuation 认得它们）、符号
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsPunctuation(c)) continue;
            if (char.IsSymbol(c)) continue;

            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>归一化之后还剩几个字。</summary>
    public static int Length(string normalized) => normalized.Length;

    // ============================================================
    // 四把尺子
    // ============================================================

    /// <summary>
    /// 取出所有相邻字对（bigram）。
    /// 少于 2 个字就返回空 —— 单字没有 bigram 可比，靠 PrefixLen 处理。
    /// </summary>
    public static List<string> Bigrams(string normalized)
    {
        var grams = new List<string>(Math.Max(0, normalized.Length - 1));
        for (int i = 0; i < normalized.Length - 1; i++)
            grams.Add(normalized.Substring(i, 2));
        return grams;
    }

    /// <summary>
    /// Dice 系数（0~1）：整体相似度，把两句的长度都算进去。
    ///
    ///   2 × 共同的 bigram 数 / (A 的 bigram 数 + B 的 bigram 数)
    ///
    /// 适合判断"这两句是不是同一句"。
    /// 缺点：一句 20 字、一句 5 字，哪怕那 5 字全被包含在 20 字里，
    /// Dice 也只有 2×4/(19+4) ≈ 0.35 —— 会被误判成"不像"。
    /// 这种"长短悬殊"的情况要用下面的 Overlap。
    /// </summary>
    public static double Dice(string normalizedA, string normalizedB)
    {
        var ga = Bigrams(normalizedA);
        var gb = Bigrams(normalizedB);
        if (ga.Count == 0 || gb.Count == 0)
        {
            // 没有 bigram（单字或空串）：退化成"是不是同一个字"
            if (normalizedA.Length == 0 || normalizedB.Length == 0) return 0;
            return normalizedA == normalizedB ? 1.0 : 0.0;
        }

        var shared = MultisetIntersectionCount(ga, gb);
        return 2.0 * shared / (ga.Count + gb.Count);
    }

    /// <summary>
    /// 重叠系数（0~1）：较短的那句有多大比例被较长的那句覆盖。
    ///
    ///   共同的 bigram 数 / min(A 的 bigram 数, B 的 bigram 数)
    ///
    /// 专门用来抓"半截句"——
    ///   A = "我们要讲的是唐代"        （半截，8 字）
    ///   B = "我们要讲的是唐代的美学"  （重说全了，13 字）
    /// Dice 只有 0.6 左右，但 Overlap 接近 1.0（A 几乎被 B 完全覆盖）。
    /// 这正是"说到一半卡住又从头说"的特征。
    /// </summary>
    public static double Overlap(string normalizedA, string normalizedB)
    {
        var ga = Bigrams(normalizedA);
        var gb = Bigrams(normalizedB);
        if (ga.Count == 0 || gb.Count == 0)
        {
            if (normalizedA.Length == 0 || normalizedB.Length == 0) return 0;
            return normalizedA == normalizedB ? 1.0 : 0.0;
        }

        var shared = MultisetIntersectionCount(ga, gb);
        return (double)shared / Math.Min(ga.Count, gb.Count);
    }

    /// <summary>
    /// 字符集合重叠率：|A∩B| / min(|A|,|B|)。只看"用到了哪些字"，不看顺序和次数。
    ///
    /// 【为什么专门要有这一把尺子】
    ///   中文 ASR 几乎必然有同音错字。真人重录的两遍，文字上常常是这样：
    ///     "为了证明自己的朝代的合法性"  vs  "为了证明自己改朝啊"
    ///     "那难道我们就要让为时论"      vs  "那难道我们就让这些反制言论"
    ///     "你还得假设出土的文物和探石寺年" vs "出出土的文物的探石寺子"
    ///
    ///   这类"同一句话的不同错字版本"，用 bigram 系列的 Dice/Overlap 会严重低估
    ///   —— 因为错字把二元组整片打断（实测 Dice 只有 0.45~0.50，和正常句拉不开差距）。
    ///   但**用到的字**大部分还是一样的，字符集合重叠率能到 0.73~0.89。
    ///
    /// 【代价】
    ///   丢掉了语序信息，所以两句话"用词相同但意思不同"也可能得高分。
    ///   因此它从不单独使用 —— 必须搭配"开头是否相同"或"bigram 相似度"一起判
    ///   （见 SpeechIssueDetector 的重录检测）。
    /// </summary>
    public static double CharOverlap(string normalizedA, string normalizedB)
    {
        if (string.IsNullOrEmpty(normalizedA) || string.IsNullOrEmpty(normalizedB)) return 0;

        var sa = new HashSet<char>(normalizedA);
        var sb = new HashSet<char>(normalizedB);
        if (sa.Count == 0 || sb.Count == 0) return 0;

        int shared = 0;
        foreach (char c in sa)
            if (sb.Contains(c)) shared++;

        return (double)shared / Math.Min(sa.Count, sb.Count);
    }

    /// <summary>
    /// 最长公共子序列比率（0~1）。
    ///
    ///   LCS(A, B) 的长度 / min(A 长度, B 长度)
    ///
    /// 子序列不要求连续（区别于子串），所以对"中间添了几个字"更宽容：
    ///   A = "今天聊唐代美学"
    ///   B = "今天来聊一下唐代的美学"   ← 转写添了虚词
    /// 这两句字面上差不少，但 LCS 几乎是完整的 A。
    ///
    /// 代价是 O(n×m) 的时间和空间。中文一句话通常不超过 50 字，够用。
    /// 超过 MaxLcsLen 的句子我们直接放弃比较（返回 0），避免长句拖慢整体。
    /// </summary>
    public static double LcsRatio(string normalizedA, string normalizedB, int maxLen = 200)
    {
        string a = normalizedA, b = normalizedB;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Length > maxLen || b.Length > maxLen) return 0;

        // 保证 b 是较短的那个，压一下 DP 表的宽度
        if (b.Length > a.Length) (a, b) = (b, a);

        int m = a.Length, n = b.Length;
        var prev = new int[n + 1];
        var cur = new int[n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                cur[j] = a[i - 1] == b[j - 1]
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], cur[j - 1]);
            }
            // 滚动数组：算完一行就翻下去，省内存
            for (int j = 0; j <= n; j++) { prev[j] = cur[j]; cur[j] = 0; }
        }

        return (double)prev[n] / n;   // n 是较短串的长度
    }

    /// <summary>
    /// 从头开始数，两句话有几个字是一样的。
    ///
    /// 返回 : 公共前缀长度 / 较短串长度（0~1）
    ///
    /// "说到一半卡住再重说"最典型的样子是：前面几个字完全一样，
    /// 后半句才走岔。这个指标对那种情况反应最灵敏。
    /// </summary>
    public static double PrefixRatio(string normalizedA, string normalizedB)
    {
        if (normalizedA.Length == 0 || normalizedB.Length == 0) return 0;
        int max = Math.Min(normalizedA.Length, normalizedB.Length);
        int i = 0;
        while (i < max && normalizedA[i] == normalizedB[i]) i++;
        return (double)i / max;
    }

    // ============================================================
    // 内部实现
    // ============================================================

    /// <summary>
    /// 两个多重集合（可以有重复元素）的交集元素个数。
    ///
    /// 用多重集合而不是普通集合：一句话里出现两次"的"，另一句出现一次"的"，
    /// 交集只能算一次，不能算两次 —— 否则重复虚词会把相似度虚高。
    /// </summary>
    private static int MultisetIntersectionCount(List<string> a, List<string> b)
    {
        // 拿较小的那个建表，省一点开销
        var pool = new Dictionary<string, int>(a.Count + b.Count);
        foreach (var g in a) pool[g] = pool.TryGetValue(g, out int c) ? c + 1 : 1;

        int shared = 0;
        foreach (var g in b)
        {
            if (pool.TryGetValue(g, out int left) && left > 0)
            {
                shared++;
                pool[g] = left - 1;
            }
        }
        return shared;
    }
}
