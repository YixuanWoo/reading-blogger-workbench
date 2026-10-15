// ============================================================
// MarkdownOutline.cs - markdown → 文章目录树
// ============================================================
//
// 【这一层负责什么】
//   从 markdown 原文里把「标题」抠出来，按层级排成一棵树，
//   给左侧导航栏当目录（TOC）用 —— 真正的"标题结构导航"。
//
// 【与渲染预览的分工】
//   · 本文件只管"标题层级 / 顺序 / 锚点"，不管怎么显示正文。
//   · 渲染预览见 MarkdownRenderer.cs。
//   两个都只依赖纯文本输入，不依赖"项目"或任何 WPF 窗口。
//
// 【识别规则（★ 严格标准 CommonMark，不迁就习惯）】
//   · `#`~`######` 开头、且 `#` 后**必须有空白**（空格/Tab）的行 = 标题。
//   · ★ `#第三方`（无空格）**不是标题** —— 标准 Markdown 就这样：GitHub /
//     Obsidian / Typora / VS Code 全都不认。这里放宽 = 换环境就不通，
//     所以严格按标准来，标题一律写 `# 文字`（养成标准语法习惯）。
//   · `# 标题` 前允许空格（缩进不算进层级，Markdown 惯例）。
//   · 行内其它语法（粗体/链接）会去掉标记，只留可读文字。
//   · 纯 `#####`（后面没文字）不算标题。
//   · Setext 式（下一行 === / ---）本期不认 —— 太容易跟分割线混淆，
//     导航不是渲染，宁可少认也不误认。
//
// 【锚点约定】
//   锚点 = 标题文字去除标记后的纯文本，URL 化（空格→-、小写、去符号），
//   用于点击目录跳转到对应位置。同一文档内标题文字重复时
//   依次追加 -2、-3（跟 GitHub 同款习惯）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiteCut.Workbench.WritingDesk.Markdown;

/// <summary>
/// 目录树上的一个标题节点。只描述"结构"，不关心画在哪。
/// </summary>
public sealed class OutlineNode
{
    /// <summary>层级（1 = #，2 = ## …）。</summary>
    public int Level { get; set; }

    /// <summary>标题文字（已去掉 `**`/链接等标记，纯可读文本）。</summary>
    public string Text { get; set; } = "";

    /// <summary>该标题在原文本中的行号（0 起，用于跳转定位）。</summary>
    public int LineIndex { get; set; }

    /// <summary>去重后的锚点（用于预览滚动定位）。</summary>
    public string Anchor { get; set; } = "";

    /// <summary>直接子节点。</summary>
    public List<OutlineNode> Children { get; set; } = new();
}

/// <summary>
/// 从 markdown 原文提取标题目录。
/// 输入原始 markdown，输出一棵按文档顺序排列的标题树。
/// </summary>
public static class MarkdownOutline
{
    // ATX 标题：行首（允许前导空格）紧跟 1~6 个 #，**必须**后接空白再跟文字（标准 CommonMark）。
    // 用 ^[ \t]{0,3}(#{1,6})[ \t]+(.+)$ —— 前导最多 3 空格，避免误抓代码块里的 #。
    private static readonly Regex Atx = new(
        @"^[ \t]{0,3}(#{1,6})[ \t]+(.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 提取整篇文档的标题目录。没有标题返回空树。
    /// </summary>
    public static List<OutlineNode> Build(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new List<OutlineNode>();

        var raw = new List<OutlineNode>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Atx.Match(lines[i]);
            if (!m.Success) continue;

            int level = m.Groups[1].Value.Length;
            string text = CleanText(m.Groups[2].Value.Trim());

            // 跳过纯符号标题（如 #### 这种无文字的行）—— 那不是标题。
            if (string.IsNullOrEmpty(text)) continue;

            string anchor = AnchorOf(text);
            anchor = DedupeAnchor(used, anchor);

            raw.Add(new OutlineNode
            {
                Level = level,
                Text = text,
                LineIndex = i,
                Anchor = anchor,
            });
        }

        return ToTree(raw);
    }

    /// <summary>
    /// 把目录树按文档顺序展平成一行行标题（含全部层级）。
    /// ★ 目录控件必须用它，不能只拿 Build 的根节点列表 —— 那会把二级以下
    ///    （挂在 Children 里）全部漏掉（V1.12.2 的教训：TOC 只剩一级标题）。
    /// </summary>
    public static List<OutlineNode> Flatten(List<OutlineNode> nodes)
    {
        var result = new List<OutlineNode>();
        void Walk(List<OutlineNode> list)
        {
            foreach (var n in list)
            {
                result.Add(n);
                Walk(n.Children);
            }
        }
        Walk(nodes);
        return result;
    }

    /// <summary>按顺序把扁平标题列表垒成树（跳级时挂到最近更高层）。</summary>
    private static List<OutlineNode> ToTree(List<OutlineNode> raw)
    {
        var root = new List<OutlineNode>();
        // 栈里存"当前层级的父容器"；栈顶是最近一个比当前 level 更浅的容器。
        // 用元组 (level, list) 存，方便回溯。
        var stack = new Stack<(int level, List<OutlineNode> list)>();

        foreach (var node in raw)
        {
            // 弹出所有层级 >= 当前标题的栈帧（保持栈严格递减）。
            while (stack.Count > 0 && stack.Peek().level >= node.Level)
                stack.Pop();

            if (stack.Count == 0)
            {
                root.Add(node);
                stack.Push((node.Level, node.Children));
            }
            else
            {
                var parent = stack.Peek().list;
                parent.Add(node);
                stack.Push((node.Level, node.Children));
            }
        }
        return root;
    }

    /// <summary>去掉行内标记，只留可读文字。</summary>
    private static string CleanText(string s)
    {
        // 粗体/斜体/删除线/行内代码/链接，都剥掉标记层。
        s = Regex.Replace(s, @"\*\*|__|~~|`|\*|_", "");
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");   // [文字](链接) → 文字
        s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1");  // ![图](链接) → 图
        s = Regex.Replace(s, @"<[^>]+>", "");                    // 行内 HTML
        return s.Trim();
    }

    /// <summary>文字 → 锚点（小写、去符号、空格转 -）。</summary>
    private static string AnchorOf(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool prevDash = false;
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevDash = false;
            }
            else if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        string a = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(a) ? "sec" : a;
    }

    /// <summary>同文档内标题文字重复时，依次追加 -2、-3…。</summary>
    private static string DedupeAnchor(HashSet<string> used, string baseAnchor)
    {
        if (used.Add(baseAnchor)) return baseAnchor;
        int n = 2;
        while (!used.Add($"{baseAnchor}-{n}")) n++;
        return $"{baseAnchor}-{n}";
    }
}
