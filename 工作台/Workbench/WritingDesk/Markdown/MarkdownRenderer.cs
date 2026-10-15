// ============================================================
// MarkdownRenderer.cs - markdown → WPF FlowDocument（渲染预览）
// ============================================================
//
// 【这一层负责什么】
//   把 markdown 原文渲染成可显示的 <see cref="FlowDocument"/>，
//   给「展开预览」面板用 —— 读者在预览里看到的就是成品效果。
//
// 【与目录提取的分工】
//   · 目录（标题层级）见 MarkdownOutline.cs。
//   · 本文件是"逐块渲染正文"，两者都只认纯文本，不依赖项目。
//
// 【覆盖的语法（常用子集，够写作台用）】
//   标题 #~######、段落、粗体/斜体/删除线、行内代码、围栏/缩进代码块、
//   无序/有序列表、引用、分割线、链接、转义。
//   表格、图片、数学公式：本期不渲染，原样当文本显示。
//
// 【配色】
//   直接复用 App.xaml 深色主题的色值（照抄 BgDeep/BgCard/Border/TextMain/
//   TextDim/TextFaint/Accent），跟工作台一个观感，不新造一套。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LiteCut.Workbench.WritingDesk.Markdown;

/// <summary>把 markdown 渲染成 <see cref="FlowDocument"/>。</summary>
public static class MarkdownRenderer
{
    // ========================================================
    // 配色（照抄 App.xaml 资源键的色值）
    // ========================================================
    private static readonly Brush Main = Argb("#FFE6E9EF");
    private static readonly Brush Dim = Argb("#FF8B93A1");
    private static readonly Brush Faint = Argb("#FF78828F");
    private static readonly Brush Accent = Argb("#FF3B82F6");
    private static readonly Brush Border = Argb("#FF2A3038");
    private static readonly Brush CodeBg = Argb("#FF232830");

    /// <summary>渲染整篇 markdown。空输入返回空文档。</summary>
    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei, PingFang SC"),
            FontSize = 14,
            LineHeight = 24,
            Foreground = Main,
            PagePadding = new Thickness(8),
        };

        if (string.IsNullOrWhiteSpace(markdown)) return doc;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var b in Blockize(lines)) RenderBlock(doc.Blocks, b);

        return doc;
    }

    // ========================================================
    // 行 → 块（扫代码块 + 按空行分段 + 归类）
    // ========================================================

    private sealed record Block(string Kind, string Raw, int Level);

    private static List<Block> Blockize(string[] lines)
    {
        var blocks = new List<Block>();
        var body = new List<string>();

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];

            // 围栏代码块 ```…```
            if (Regex.IsMatch(line.Trim(), @"^```+"))
            {
                FlushBody(blocks, body); body.Clear();
                var sb = new StringBuilder();
                int j = i + 1;
                while (j < lines.Length && !Regex.IsMatch(lines[j].Trim(), @"^```+")) sb.AppendLine(lines[j++]);
                blocks.Add(new Block("code", sb.ToString(), 0));
                i = Math.Min(j, lines.Length);
                continue;
            }

            // 缩进 4 空格代码块（连续行）
            if (IsIndentCode(line))
            {
                FlushBody(blocks, body); body.Clear();
                var sb = new StringBuilder();
                while (i < lines.Length && IsIndentCode(lines[i]))
                {
                    string t = lines[i].StartsWith('\t') ? lines[i][1..] : lines[i][4..];
                    sb.AppendLine(t);
                    i++;
                }
                blocks.Add(new Block("code", sb.ToString(), 0));
                continue;
            }

            // ★ 标题：行级块（标准 CommonMark —— 标题本身就是一块，后面接正文**不需要空行**）。
            //   旧实现把标题塞进 body 等整块归类，标题后紧跟正文（无空行）就被并成段落、
            //   `#` 原样显示（而 MarkdownOutline 逐行扫能认出来 → 目录有、渲染没有，就是这次的事故）。
            var hm = Regex.Match(line.Trim(), @"^(#{1,6})[ \t]+(.+)$");
            if (hm.Success)
            {
                FlushBody(blocks, body);
                blocks.Add(new Block("h", hm.Groups[2].Value, hm.Groups[1].Value.Length));
                i++;
                continue;
            }

            // ★ 分割线：行级块（同理由，`---` 后紧跟内容也各自成块）
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                FlushBody(blocks, body);
                blocks.Add(new Block("hr", "", 0));
                i++;
                continue;
            }

            body.Add(line);

            // 一段在"空行"或"下一行是代码块/行尾"处结束
            bool nextBlank = i + 1 >= lines.Length || string.IsNullOrWhiteSpace(lines[i + 1]);
            bool nextCode = i + 1 < lines.Length && (IsIndentCode(lines[i + 1]) || Regex.IsMatch(lines[i + 1].Trim(), @"^```+"));
            if (string.IsNullOrWhiteSpace(line) || nextBlank || nextCode)
            {
                FlushBody(blocks, body); body.Clear();
            }
            i++;
        }
        FlushBody(blocks, body);
        return blocks;
    }

    private static bool IsIndentCode(string s)
        => s.Length >= 4 && (s[0] == ' ' || s[0] == '\t');

    private static void FlushBody(List<Block> blocks, List<string> body)
    {
        if (body.Count == 0) return;
        var b = Classify(body);
        if (b != null) blocks.Add(b);
        body.Clear();
    }

    /// <summary>把一组连续行归类成一种块。（★ 标题/分割线已在 Blockize 里逐行切走，这里不再处理）</summary>
    private static Block? Classify(List<string> lines)
    {
        string joined = string.Join("\n", lines).Trim();
        if (joined.Length == 0) return null;

        if (lines[0].TrimStart().StartsWith('>'))
        {
            var quoted = new List<string>();
            foreach (var l in lines)
            {
                string t = l.TrimStart();
                quoted.Add(t.StartsWith('>') ? t[1..].TrimStart() : t);
            }
            return new Block("quote", string.Join("\n", quoted), 0);
        }

        if (Regex.IsMatch(lines[0].Trim(), @"^[-*+]\s+.+$")) return new Block("list", joined, 0);
        if (Regex.IsMatch(lines[0].Trim(), @"^\d+[.)]\s+.+$")) return new Block("olist", joined, 0);

        return new Block("p", joined, 0);
    }

    // ========================================================
    // 块 → FlowDocument 元素
    // ========================================================

    private static void RenderBlock(BlockCollection blocks, Block b)
    {
        switch (b.Kind)
        {
            case "h": RenderHeading(blocks, b.Raw, b.Level); break;
            case "hr":
                blocks.Add(new Paragraph(new Run(new string('─', 40)))
                { Foreground = Border, Margin = new Thickness(0, 8, 0, 8) });
                break;
            case "quote": RenderQuote(blocks, b.Raw); break;
            case "list": RenderList(blocks, b.Raw, ordered: false); break;
            case "olist": RenderList(blocks, b.Raw, ordered: true); break;
            case "code": RenderCode(blocks, b.Raw); break;
            default: blocks.Add(MakeParagraph(b.Raw).Also(p => p.Margin = new Thickness(0, 4, 0, 6))); break;
        }
    }

    /// <summary>按行内语法建 Paragraph（把 inlines 逐条 Add，WPF 的 Paragraph 没有集合构造器）。</summary>
    private static Paragraph MakeParagraph(string text)
    {
        var p = new Paragraph { Foreground = Main };
        foreach (var inline in InlineRuns(text)) p.Inlines.Add(inline);
        return p;
    }

    private static void RenderHeading(BlockCollection blocks, string text, int level)
    {
        var p = MakeParagraph(text);
        p.Margin = new Thickness(0, 10, 0, 6);
        p.FontWeight = FontWeights.SemiBold;
        p.Foreground = Main;
        p.FontSize = level switch { 1 => 22, 2 => 19, 3 => 16.5, _ => 14.5 };
        blocks.Add(p);
    }

    private static void RenderQuote(BlockCollection blocks, string text)
    {
        var p = MakeParagraph(text);
        p.Margin = new Thickness(12, 4, 0, 6);
        p.Foreground = Dim;
        p.BorderBrush = Accent;
        p.BorderThickness = new Thickness(3, 0, 0, 0);
        p.Padding = new Thickness(10, 2, 0, 2);
        blocks.Add(p);
    }

    private static void RenderList(BlockCollection blocks, string raw, bool ordered)
    {
        var list = new List { Margin = new Thickness(0, 2, 0, 4) };
        int counter = 1;

        foreach (var ln in raw.Split('\n'))
        {
            string t = ln.Trim();
            string? content = null;

            if (!ordered)
            {
                var m = Regex.Match(t, @"^[-*+]\s+(.+)$");
                if (m.Success) content = m.Groups[1].Value;
            }
            else
            {
                var m = Regex.Match(t, @"^\d+[.)]\s+(.+)$");
                if (m.Success) content = m.Groups[1].Value;
            }
            if (content == null) continue;

            // 用序号做可视前缀（有序）
            string prefix = ordered ? $"{counter}.  " : "";
            var itemPara = MakeParagraph(prefix + content);
            itemPara.Margin = new Thickness(0);
            var item = new ListItem(itemPara) { Margin = new Thickness(0, 2, 0, 2) };
            list.ListItems.Add(item);
            counter++;
        }

        // 嵌套（缩进更深）本期做"视觉扁平"：所有项都进同一 List。
        // 要完整嵌套树，渲染器会复杂很多；写作台导航用 TOC 已够，列表扁平不影响内容。
        blocks.Add(list);
    }

    private static void RenderCode(BlockCollection blocks, string raw)
    {
        var p = new Paragraph
        {
            Background = CodeBg,
            BorderBrush = Border, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 6, 0, 6),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Foreground = Main,
        };
        p.Inlines.Add(new Run(raw.TrimEnd('\n')));
        blocks.Add(p);
    }

    // ========================================================
    // 行内（代码/粗体/斜体/删除线/链接）
    // ========================================================

    private static IEnumerable<Inline> InlineRuns(string text)
    {
        text = Regex.Replace(text, @"\\([*_~`\[\]])", "$1");   // 转义保护

        var result = new List<Inline>();
        // 先切行内代码 `…`
        var codeRe = new Regex(@"`([^`]+)`");
        int pos = 0;
        foreach (Match cm in codeRe.Matches(text))
        {
            if (cm.Index > pos) result.AddRange(InlineRich(text[pos..cm.Index]));
            result.Add(new Run(cm.Groups[1].Value)
            { Background = CodeBg, FontFamily = new FontFamily("Consolas"), Foreground = Accent });
            pos = cm.Index + cm.Length;
        }
        if (pos < text.Length) result.AddRange(InlineRich(text[pos..]));
        if (result.Count == 0) result.Add(new Run(text));
        return result;
    }

    /// <summary>粗体 → 斜体 → 删除线 → 链接，逐层剥。</summary>
    private static IEnumerable<Inline> InlineRich(string text)
    {
        var boldRe = new Regex(@"(\*\*|__)(.+?)\1", RegexOptions.Singleline);
        var itRe = new Regex(@"(?<!\*)(\*|_)([^*_]+?)\1(?!\*)", RegexOptions.Singleline);
        var strikeRe = new Regex(@"~~([^~]+)~~");

        var result = new List<Inline>();
        int pos = 0;

        // 删除线最外层
        foreach (Match sm in strikeRe.Matches(text))
        {
            if (sm.Index > pos) result.AddRange(BoldItalic(text[pos..sm.Index]));
            var r = new Run(sm.Groups[1].Value)
            { TextDecorations = TextDecorations.Strikethrough, Foreground = Dim };
            result.Add(r);
            pos = sm.Index + sm.Length;
        }
        if (pos < text.Length) result.AddRange(BoldItalic(text[pos..]));
        if (result.Count == 0) result.Add(new Run(text));
        return result;
    }

    private static IEnumerable<Inline> BoldItalic(string text)
    {
        var boldRe = new Regex(@"(\*\*|__)(.+?)\1", RegexOptions.Singleline);
        var result = new List<Inline>();
        int pos = 0;
        foreach (Match bm in boldRe.Matches(text))
        {
            if (bm.Index > pos) result.AddRange(Italic(text[pos..bm.Index]));
            result.Add(new Bold(new Run(bm.Groups[2].Value)));
            pos = bm.Index + bm.Length;
        }
        if (pos < text.Length) result.AddRange(Italic(text[pos..]));
        if (result.Count == 0) result.Add(new Run(text));
        return result;
    }

    private static IEnumerable<Inline> Italic(string text)
    {
        var itRe = new Regex(@"(?<!\*)(\*|_)([^*_]+?)\1(?!\*)");
        var result = new List<Inline>();
        int pos = 0;
        foreach (Match im in itRe.Matches(text))
        {
            if (im.Index > pos) result.Add(Link(text[pos..im.Index]));
            result.Add(new Italic(new Run(im.Groups[2].Value)));
            pos = im.Index + im.Length;
        }
        if (pos < text.Length) result.Add(Link(text[pos..]));
        if (result.Count == 0) result.Add(Link(text));
        return result;
    }

    /// <summary>链接 [文字](url) → Hyperlink（可点击，外部链接交界面层处理）。</summary>
    private static Inline Link(string text)
    {
        var m = Regex.Match(text, @"\[([^\]]*)\]\(([^)]*)\)");
        if (!m.Success) return new Run(text);

        var h = new Hyperlink(new Run(m.Groups[1].Value))
        {
            Foreground = Accent,
            NavigateUri = new Uri(m.Groups[2].Value, UriKind.RelativeOrAbsolute),
            ToolTip = m.Groups[2].Value,
        };
        h.RequestNavigate += (s, e) => { /* 外部链接：由界面层决定是否用系统浏览器打开 */ };
        return h;
    }

    private static Brush Argb(string argb)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
}

/// <summary>就地改对象的小助手（避免为一行赋值写完整构造器）。</summary>
internal static class ObjectHelper
{
    public static T Also<T>(this T obj, Action<T> action)
    {
        action(obj);
        return obj;
    }
}
