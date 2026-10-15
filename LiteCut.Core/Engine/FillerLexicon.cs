// ============================================================
// FillerLexicon.cs - 中文口播里的"口水话"词表
// ============================================================
//
// 【为什么词表要分两级】
//
//   用户的原话是："明显口吃/重复保留，拿不准的部分可以保留。"
//   这条要求直接决定了词表的组织方式。
//
//   中文填充词有一道天然的分界线：
//
//     ┌─ 一级：纯口气词（嗯、啊、呃…）
//     │      它没有意思，只是嘴在动。删掉不改变任何语义。
//     │      → 敢自动删。误伤代价≈0。
//     │
//     └─ 二级：话语标记（就是、然后、这个、其实…）
//            它有语法作用，有时候还承担实词义：
//              "他就是杜甫"      ← "就是"是判断动词，删了句子散架
//              "这就是我想说的"  ← 同上
//              "那个年代的人"    ← "那个"是指示代词，删了变成"年代的人"
//            → 默认**只标注、不自动删**。交给人在界面上勾。
//
//   这个二分避免了一个很常见的翻车：
//   网上很多"自动去口水词"工具一刀切，把"然后"全删了，
//   结果句子读起来像电报，用户还得手动救回来 —— 反而多花时间。
//
// 【默认误伤为零的设计】
//   一级词我挑得很保守 —— 只收那些**几乎不可能作为实词出现**的发声。
//   宁可漏掉几个，也不误删一个字。
//   漏掉的代价是"还要手动补一刀"，误删的代价是"内容没了"。
//
// 【用户可以自己加】
//   每个人有口头禅（有人爱说"你会发现"，有人爱说"我个人觉得"）。
//   UserFillers 就是留给用户的口子 —— 加进去的按一级处理（自动删）。
//
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut.Core.Engine;

/// <summary>一个词算哪类填充。</summary>
public enum FillerKind
{
    /// <summary>正常内容，不是填充。</summary>
    None = 0,

    /// <summary>纯口气词（嗯、呃）——可直接删。</summary>
    Hesitation = 1,

    /// <summary>话语标记（就是、然后）——删了可能让句子不通，默认只标注。</summary>
    DiscourseMarker = 2,

    /// <summary>用户自己加的口头禅 —— 按"用户说了算"，可直接删。</summary>
    UserDefined = 3,
}

/// <summary>一次匹配的命中结果。</summary>
public readonly struct FillerMatch
    {
    /// <summary>命中的字数（多少个字要被处理）。</summary>
    public int Length { get; init; }

    /// <summary>属于哪一类。</summary>
    public FillerKind Kind { get; init; }

    /// <summary>基础置信度（0~1）。检测器还会按位置再调整。</summary>
    public double Confidence { get; init; }

    /// <summary>命中的是词典里哪一条（写进审核备注里，方便用户排查）。</summary>
    public string Entry { get; init; }
}

/// <summary>中文填充词库。</summary>
public static class FillerLexicon
{
    // ============================================================
    // 一级：纯口气词
    // ============================================================
    //
    // 【收录标准】这个词基本只作为"发声"出现，不承担实词义。
    //
    // 单字的尤其小心。"啊"可以出现在"天啊/是啊"里，
    // 但在**口播独白**语境下几乎都是语气词，给 0.85（低于默认的自动删阈值 0.88 一点，
    // 让它需要检测器给个位置加成才动）。

    private static readonly Dictionary<string, double> Hesitations = new()
    {
        // —— 鼻音、拖音（最典型的一类，几乎不可能有实义）——
        ["嗯"] = 0.95,
        ["嗯嗯"] = 0.96,
        ["呃"] = 0.95,
        ["呃呃"] = 0.96,
        ["唔"] = 0.93,
        ["额"] = 0.93,     // 转写常把"呃"写成"额"，两个都收
        ["诶"] = 0.90,
        ["唉"] = 0.88,

        // —— 元音拖音（可能有轻微歧义，置信度给低一档）——
        ["啊"] = 0.85,
        ["哦"] = 0.85,
        ["噢"] = 0.85,
        ["哎"] = 0.85,
        ["嗨"] = 0.82,

        // —— 多字连发（连发就是明确的大喘气，比单次更确定）——
        ["啊啊"] = 0.90,
        ["哦哦"] = 0.90,
        ["哎哎"] = 0.90,

        // —— 整句全是语气词的典型形态 ——
        ["那个那个"] = 0.92,
        ["这个这个"] = 0.92,
        ["就是说"] = 0.88,   // 作为独立词组时基本是口头禅
    };

    // ============================================================
    // 二级：话语标记（默认只标注不自动删）
    // ============================================================
    //
    // 这些在满足下面任一情况时其实是实词：
    //   "他就是李白"、"这就是问题"、"那个皇帝"、"然后我们就走了"（叙述顺序）
    // 所以基础置信度压到 0.5 以下 —— 低于自动删阈值，只在界面上标出来。

    private static readonly Dictionary<string, double> DiscourseMarkers = new()
    {
        ["就是"] = 0.50,
        ["就是说"] = 0.55,     // 比单个"就是"更偏口头禅
        ["也就是说"] = 0.55,
        ["这个"] = 0.50,
        ["那个"] = 0.50,
        ["然后"] = 0.55,
        ["接下来"] = 0.55,
        ["其实"] = 0.50,
        ["反正"] = 0.50,
        ["基本上"] = 0.50,
        ["事实上"] = 0.50,
        ["怎么说呢"] = 0.60,
        ["怎么说"] = 0.55,
        ["对吧"] = 0.60,
        ["是吧"] = 0.60,
        ["对不对"] = 0.60,
        ["你知道吗"] = 0.60,
        ["大家都知道"] = 0.55,
        ["你会发现"] = 0.55,
        ["你会发现啊"] = 0.60,
        ["我觉得"] = 0.55,
        ["我个人觉得"] = 0.60,
        ["我个人认为"] = 0.60,
        ["所以说"] = 0.50,
        ["总而言之"] = 0.55,
        ["换句话说"] = 0.55,
        ["比方说"] = 0.55,
        ["比如说"] = 0.55,
        ["我跟你讲"] = 0.60,
        ["我跟你说"] = 0.60,
        ["你看"] = 0.50,
        ["你想想看"] = 0.55,
        ["那么"] = 0.50,
        ["好那么"] = 0.60,
        ["好吧"] = 0.55,
        ["所以基本上"] = 0.55,
        ["怎么说好呢"] = 0.60,
    };

    // ============================================================
    // 用户自定义
    // ============================================================

    /// <summary>
    /// 用户自己加的口头禅。加进来的按一级处理（自动删）——
    /// 用户的判断优先于我的保守策略。
    /// 线程安全考虑：这是个静态集合，界面改它的时候走 AddUserFiller / ClearUserFillers。
    /// </summary>
    public static HashSet<string> UserFillers { get; } = new(StringComparer.Ordinal);

    /// <summary>加一条用户口头禅。会做归一化（去标点、转小写）。</summary>
    public static void AddUserFiller(string word)
    {
        string norm = TextSimilarity.Normalize(word);
        if (norm.Length > 0) UserFillers.Add(norm);
    }

    /// <summary>清空用户词表。</summary>
    public static void ClearUserFillers() => UserFillers.Clear();

    // ============================================================
    // 匹配
    // ============================================================

    /// <summary>一级词表有没有这个词。</summary>
    public static bool IsHesitation(string normalized) => Hesitations.ContainsKey(normalized);

    /// <summary>二级词表有没有这个词。</summary>
    public static bool IsDiscourseMarker(string normalized) => DiscourseMarkers.ContainsKey(normalized);

    /// <summary>
    /// 在已归一化的字符串里，从 from 位置开始做**最长**匹配。
    ///
    /// 为什么必须取最长：
    ///   "就是说" 如果只往前看两字会命中"就是"（0.50，不敢删），
    ///   而实际它是"就是说"（0.55 或更高）。
    ///   不取最长会让判定忽高忽低。
    /// </summary>
    /// <returns>没命中返回 Length = 0 的结构。</returns>
    public static FillerMatch MatchLongest(string normalized, int from)
    {
        if (string.IsNullOrEmpty(normalized) || from < 0 || from >= normalized.Length)
            return new FillerMatch { Length = 0, Kind = FillerKind.None, Confidence = 0, Entry = "" };

        // 用户词表优先 —— 用户说删就删
        int userLen = MatchIn(UserFillers, normalized, from, out string userEntry);
        if (userLen > 0)
            return new FillerMatch { Length = userLen, Kind = FillerKind.UserDefined, Confidence = 0.92, Entry = userEntry };

        int maxLen = Math.Min(6, normalized.Length - from);   // 词表最长条目不超过 6 字
        int bestLen = 0;
        double bestConf = 0;
        FillerKind bestKind = FillerKind.None;
        string bestEntry = "";

        for (int len = maxLen; len >= 1; len--)
        {
            string cand = normalized.Substring(from, len);

            if (Hesitations.TryGetValue(cand, out double hc))
            {
                // 一级命中就可以收了 —— 一级优先级高于二级
                return new FillerMatch { Length = len, Kind = FillerKind.Hesitation, Confidence = hc, Entry = cand };
            }

            if (DiscourseMarkers.TryGetValue(cand, out double dc))
            {
                if (bestKind != FillerKind.DiscourseMarker || dc > bestConf)
                {
                    bestLen = len; bestConf = dc; bestKind = FillerKind.DiscourseMarker; bestEntry = cand;
                }
            }
        }

        return new FillerMatch
        {
            Length = bestLen,
            Kind = bestKind,
            Confidence = bestConf,
            Entry = bestEntry,
        };
    }

    /// <summary>在一个字符串集合里做最长匹配（用于用户词表）。</summary>
    private static int MatchIn(HashSet<string> set, string normalized, int from, out string entry)
    {
        entry = "";
        if (set.Count == 0) return 0;
        int maxLen = Math.Min(8, normalized.Length - from);
        for (int len = maxLen; len >= 1; len--)
        {
            string cand = normalized.Substring(from, len);
            if (set.Contains(cand)) { entry = cand; return len; }
        }
        return 0;
    }
}
