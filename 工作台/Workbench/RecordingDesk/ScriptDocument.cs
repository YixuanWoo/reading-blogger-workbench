// ============================================================
// ScriptDocument.cs - 稿子 → 「章节 / 段落 / 句子」三层结构
// ============================================================
//
// 【为什么录音台要自己拆一遍句子，而不是让写作台给】
//   录音台要的是"可点击、可编号、可被录音区间引用"的句子 —— 一句话就是一个单位：
//     · 点它 = 从这里开始念
//     · Shift 点它 = 把选区拉到这里（可以跨自然段）
//     · 录完一条音，绑的就是"第 12–14 句"这个范围
//   写作台的 Markdown 是给人写的原文，没有这层编号。
//   所以这里做一次**只读**拆解：script.md 一个字都不改，拆出来的只是本页的坐标系。
//
// 【为什么句子编号要连续、跨段落不间断】
//   录音记录里只存 "第几句到第几句"。如果编号按段落各自从 1 开始，
//   跨段落的选区就没法用一个区间表达 —— 而"两段内容连贯、一口气念"正是
//   用户明确要的用法。所以 SENTS 是整个稿子拍平后的唯一编号。
//
// 【拆句规则（贴中文朗读习惯）】
//   句末标点：。！？；…（以及紧跟其后的收尾引号/括号）
//   不拆：逗号、顿号、冒号、破折号 —— 这些是气口，不是句号。
//   一行 = 一个自然段；# 开头 = 章节标题（只显示、不进句子编号）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LiteCut.Workbench.RecordingDesk;

/// <summary>稿子里的一句话（录音台的最小引用单位）。</summary>
public sealed class ScriptSentence
{
    /// <summary>全稿唯一编号（0 起）。★ 录音记录里存的就是它，别中途重排。</summary>
    public int Index { get; init; }

    /// <summary>句子原文（含句末标点，不含 Markdown 标记）。</summary>
    public string Text { get; init; } = "";

    /// <summary>属于哪个自然段（Paras 里的下标）。</summary>
    public int Para { get; init; }

    /// <summary>正文字数（不含空白）。用来估时长、算语速。</summary>
    public int Chars { get; init; }
}

/// <summary>稿子里的一个块：要么是章节标题，要么是一个自然段。</summary>
public sealed class ScriptPara
{
    public bool IsChapter { get; init; }

    /// <summary>章节标题文字（仅 IsChapter=true 时有值）。</summary>
    public string Chapter { get; init; } = "";

    /// <summary>这一段里的句子（章节块为空）。</summary>
    public List<ScriptSentence> Sentences { get; } = new();
}

/// <summary>一整份稿子的拆解结果。不可变，重新读稿就整份换掉。</summary>
public sealed class ScriptDocument
{
    /// <summary>来源文件（&lt;项目&gt;\script.md）。</summary>
    public string SourcePath { get; private set; } = "";

    /// <summary>按顺序排好的块（章节 / 段落）。</summary>
    public List<ScriptPara> Paras { get; } = new();

    /// <summary>拍平后的全部句子（唯一编号就是这里的下标）。</summary>
    public List<ScriptSentence> Sentences { get; } = new();

    /// <summary>读稿失败时的原因（成功了就是空的）。</summary>
    public string LoadError { get; private set; } = "";

    /// <summary>稿子指纹：长度 + 最后修改时间。用来发现"稿子改过了，录音范围可能对不上"。</summary>
    public string Stamp { get; private set; } = "";

    public bool IsEmpty => Sentences.Count == 0;

    /// <summary>第 a 句到第 b 句（含两端）一共多少字。</summary>
    public int CharsBetween(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        int n = 0;
        for (int i = Math.Max(0, lo); i <= hi && i < Sentences.Count; i++)
            n += Sentences[i].Chars;
        return n;
    }

    /// <summary>把第 a–b 句拼成一段文字（提词区显示用）。</summary>
    public string TextBetween(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        var sb = new StringBuilder();
        for (int i = Math.Max(0, lo); i <= hi && i < Sentences.Count; i++)
            sb.Append(Sentences[i].Text);
        return sb.ToString();
    }

    /// <summary>读稿 + 拆解。文件不存在 / 读不出来时不抛异常，返回空文档 + LoadError。</summary>
    public static ScriptDocument Load(string path)
    {
        var doc = new ScriptDocument { SourcePath = path };

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            doc.LoadError = "没有找到 script.md";
            return doc;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
            var fi = new FileInfo(path);
            doc.Stamp = fi.Length.ToString() + ":" + fi.LastWriteTimeUtc.Ticks.ToString();
        }
        catch (Exception ex)
        {
            doc.LoadError = "读不了 script.md：" + ex.Message;
            return doc;
        }

        int gi = 0;      // 全局句子号
        int pi = 0;      // 段落号（只有正文段占号，章节不占）

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line == "---" || line == "***" || line == "___") continue;   // 分隔线

            // ---- 章节标题 ----
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                string title = line.TrimStart('#').Trim();
                if (title.Length == 0) continue;
                doc.Paras.Add(new ScriptPara { IsChapter = true, Chapter = CleanInline(title) });
                continue;
            }

            // ---- 正文段落 ----
            string body = CleanLine(line);
            if (body.Length == 0) continue;

            var para = new ScriptPara { IsChapter = false };
            foreach (string s in SplitSentences(body))
            {
                var sent = new ScriptSentence
                {
                    Index = gi++,
                    Text = s,
                    Para = pi,
                    Chars = CountChars(s),
                };
                para.Sentences.Add(sent);
                doc.Sentences.Add(sent);
            }

            if (para.Sentences.Count > 0)
            {
                doc.Paras.Add(para);
                pi++;
            }
        }

        if (doc.Sentences.Count == 0)
            doc.LoadError = "script.md 里没有可念的正文";

        return doc;
    }

    // ========================================================
    // 文本清洗 / 拆句
    // ========================================================

    /// <summary>去掉行首的列表符号、引用符号等 Markdown 标记（念的时候不该念出来）。</summary>
    private static string CleanLine(string line)
    {
        // 引用
        while (line.StartsWith(">", StringComparison.Ordinal))
            line = line[1..].TrimStart();

        // 无序列表
        if (line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("* ", StringComparison.Ordinal) ||
            line.StartsWith("+ ", StringComparison.Ordinal))
            line = line[2..].TrimStart();

        // 有序列表 1. / 1)
        int k = 0;
        while (k < line.Length && char.IsDigit(line[k])) k++;
        if (k > 0 && k < line.Length && (line[k] == '.' || line[k] == ')') &&
            k + 1 < line.Length && line[k + 1] == ' ')
            line = line[(k + 2)..].TrimStart();

        return CleanInline(line);
    }

    /// <summary>去掉行内的强调/代码标记（**粗体**、*斜体*、`代码`、~~删除线~~）。</summary>
    private static string CleanInline(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '*' || c == '`' || c == '~' || c == '_')
            {
                // 下划线只去掉成对的（避免误删"__"以外的正常用法；中文里基本不出现）
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>按中文句末标点拆句。</summary>
    private static List<string> SplitSentences(string text)
    {
        var outList = new List<string>();
        var sb = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            sb.Append(c);

            bool end = c == '。' || c == '！' || c == '？' || c == '；' || c == '…' ||
                       c == '!' || c == '?' || c == ';';

            if (!end) continue;

            // 把紧跟的收尾引号 / 括号并进这一句（"他说：'好。'" 不该把引号甩到下一句开头）
            while (i + 1 < text.Length && IsTailPunctuation(text[i + 1]))
                sb.Append(text[++i]);

            string one = sb.ToString().Trim();
            if (one.Length > 0) outList.Add(one);
            sb.Clear();
        }

        string rest = sb.ToString().Trim();
        if (rest.Length > 0) outList.Add(rest);

        return outList;
    }

    private static bool IsTailPunctuation(char c)
        => c == '”' || c == '’' || c == '」' || c == '』' || c == '）' || c == ')' ||
           c == '》' || c == '】' || c == '…' || c == '。' || c == '！' || c == '？';

    /// <summary>正文字数（不数空白）。中文朗读估时用这个。</summary>
    public static int CountChars(string s)
    {
        int n = 0;
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) n++;
        return n;
    }
}
