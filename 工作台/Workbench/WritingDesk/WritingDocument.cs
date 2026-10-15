// ============================================================
// WritingDocument.cs - 文档模型
// ============================================================
//
// 【这一层是什么】
//   一篇 Markdown 文档在内存里的样子：原文 + 目录 + 派生统计。
//   本文件只定义"长什么样"，不负责读写（读写见 WritingDocumentStore.cs）。
//
// 【与旧 ScriptBlock 的区别】
//   旧写作台把稿子切成"回车段落块"（ScriptBlock[]，带永久 ID / 录音状态）。
//   本次改造后：**稿子就是一整篇 Markdown 原文**，不再切块。
//   · 回车 = 只是换行，不产生结构。
//   · 结构由标题（#~######）表达，导航跟着标题走。
//   · 录音相关的整套（永久 ID / audio / 待重录）已删除。
//
// 【不依赖项目】
//   本类只认"文本内容"，不认"项目"——将来独立成写作软件可直接带走。
// ============================================================

using System.Collections.Generic;
using LiteCut.Workbench.WritingDesk.Markdown;

namespace LiteCut.Workbench.WritingDesk;

/// <summary>
/// 一篇 Markdown 文档。落盘见"路径参数传入的 .md 文件"。
/// </summary>
public sealed class WritingDocument
{
    /// <summary>稿子原文（一整篇 markdown，唯一真相）。</summary>
    public string Text { get; set; } = "";

    /// <summary>上次落盘时间（人话，如 "2026-10-04 15:20"）。空 = 从没存过。</summary>
    public string SavedAt { get; set; } = "";

    /// <summary>磁盘上有没有文件（没存过就是 false）。</summary>
    public bool HasFile { get; set; }

    // ========================================================
    // 派生量（不落盘，纯算的）
    // ========================================================

    /// <summary>字数（去掉所有空白）。</summary>
    public int CharCount => CountChars(Text);

    /// <summary>句数（按中文句末标点切）。</summary>
    public int SentenceCount => CountSentences(Text);

    /// <summary>行数（按换行切）。</summary>
    public int LineCount => Text.Length == 0 ? 0 : Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;

    /// <summary>标题目录（按 #~###### 自动生成，真正的标题结构导航）。</summary>
    public List<OutlineNode> Outline => MarkdownOutline.Build(Text);

    /// <summary>有没有内容（空稿 = 没有正文）。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    // ========================================================
    // 工具
    // ========================================================

    /// <summary>字数：去掉所有空白。</summary>
    public static int CountChars(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 0;
        foreach (char c in s) if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    /// <summary>句数：按句末标点切，末尾不足一句也算一句。</summary>
    public static int CountSentences(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Split('。', '！', '？', '；', '…', '\n');
        int n = 0;
        foreach (var p in parts) if (!string.IsNullOrWhiteSpace(p)) n++;
        return n;
    }
}
