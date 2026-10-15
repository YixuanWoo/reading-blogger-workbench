// ============================================================
// Program.cs - 写作台改造的无头断言（临时验证）
// ============================================================
// 直接调用生产代码（MarkdownOutline / WritingDocument /
// WritingDocumentStore / MarkdownRenderer），跑一遍关键行为。
//
// 用法： cd 工具\写作台验证 && dotnet run -c Release
// 退出码 0 = 全通过，1 = 有失败项。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteCut.Workbench.WritingDesk;
using LiteCut.Workbench.WritingDesk.Markdown;

namespace WritingDeskVerify;

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.WriteLine($"  ✗ {name}"); }
    }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("== ① 标题目录提取（MarkdownOutline.Build） ==");
        var outline = MarkdownOutline.Build("""
            # 第一章

            正文。

            ## 第一节

            ### 小节A

            ## 第二节

            # 第二章

            ## 第一节
            """);
        // 目录是树，计数要扁平化（子标题挂在父标题的 Children 里）
        var flat = Flatten(outline).ToList();
        Check("一级标题=2", flat.Count(n => n.Level == 1) == 2);
        Check("二级标题=3", flat.Count(n => n.Level == 2) == 3);
        Check("三级标题=1", flat.Count(n => n.Level == 3) == 1);
        Check("顺序：第一章在前", outline[0].Text == "第一章" && outline[^1].Text == "第二章");
        Check("第一章含2个二级子节", outline[0].Children.Count == 2);
        Check("第一章首子节=第一节", outline[0].Children[0].Text == "第一节");
        Check("行号正确（第一章在第0行）", outline[0].LineIndex == 0);

        // 去重锚点（注意：## 是 # 的子节点，要扁平化后取）
        var dup = Flatten(MarkdownOutline.Build("# 标题\n\n## 标题\n")).ToList();
        Check("重复标题锚点去重", dup.Count == 2 && dup[0].Anchor != dup[1].Anchor);

        // ★ 标准 CommonMark：# 后必须有空格。`#第三方`（无空格）**不是**标题，
        //   与 GitHub / Obsidian / Typora 行为一致 —— 换环境也能通。
        var noSpace = Flatten(MarkdownOutline.Build("#第三方\n\n## 第二节\n")).ToList();
        Check("无空格标题（#第三方）不是标题", noSpace.Count == 1 && noSpace[0].Text == "第二节");
        Check("标准写法（# 文字）仍识别", Flatten(MarkdownOutline.Build("# 第三方\n")).First().Text == "第三方");

        // ★ V1.11.3：标题后**直接接正文（无空行）**也必须识别 —— 标题是行级块
        var noBlank = Flatten(MarkdownOutline.Build("# 第三方\n正文\n")).ToList();
        Check("标题后紧跟正文仍识别标题", noBlank.Count == 1 && noBlank[0].Text == "第三方");

        // ★ V1.12.2：Flatten 必须把二级以下标题也展平出来（TOC 不能只剩一级）
        var flatAll = MarkdownOutline.Flatten(outline);
        Check("Flatten 含二级标题", flatAll.Count(n => n.Level == 2) == 3);
        Check("Flatten 含三级标题", flatAll.Count(n => n.Level == 3) == 1);
        Check("Flatten 保持文档顺序", flatAll.Select(n => n.Text).SequenceEqual(
            new[] { "第一章", "第一节", "小节A", "第二节", "第二章", "第一节" }));

        Console.WriteLine("== ② 文档模型统计（WritingDocument） ==");
        var doc = new WritingDocument { Text = "第一句。第二句！第三句？\n\n下一段。\n" };
        Check("字数=16（去空白）", doc.CharCount == 16);
        Check("句数=4", doc.SentenceCount == 4);
        Check("行数=4", doc.LineCount == 4);
        Check("标题目录自动生成", doc.Outline.Count == 0);   // 这句没标题，应为0

        Console.WriteLine("== ③ md 往返读写（WritingDocumentStore） ==");
        string dir = Path.Combine(Path.GetTempPath(), "wd_verify_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string mdPath = Path.Combine(dir, "script.md");
        var src = new WritingDocument { Text = "# 标题\n\n正文，含中文。\n" };
        WritingDocumentStore.Save(mdPath, src);

        var bytes = File.ReadAllBytes(mdPath);
        Check("文件已生成", File.Exists(mdPath));
        Check("无 BOM（首字节非 0xEF）", bytes.Length > 0 && bytes[0] != 0xEF);

        var loaded = WritingDocumentStore.Load(mdPath);
        Check("往返内容一致", loaded.Text == src.Text);
        Check("SavedAt 已记录", !string.IsNullOrEmpty(loaded.SavedAt));
        Directory.Delete(dir, true);

        Console.WriteLine("== ④ Markdown 渲染关键语法（MarkdownRenderer.Render） ==");
        var sample = "# 标题\n\n**加粗** 和 *斜体* 和 ~~删除~~\n\n- 项一\n- 项二\n\n> 引用\n\n```csharp\nvar x = 1;\n```\n";
        var fd = MarkdownRenderer.Render(sample);
        Check("渲染出 FlowDocument", fd != null);
        Check("文档有块", fd != null && fd.Blocks.Count > 0);
        // 至少包含一个标题段落
        Check("有标题块", fd != null && fd.Blocks.OfType<System.Windows.Documents.Paragraph>()
              .Any(p => p.FontSize >= 14.5));
        // ★ 标准 CommonMark：# 后必须有空格，#第三方 不渲染成标题（跟 GitHub/Obsidian 一致）
        var fd2 = MarkdownRenderer.Render("#第三方\n\n# 正文\n");
        Check("无空格标题不渲染成标题块", fd2 != null && fd2.Blocks.OfType<System.Windows.Documents.Paragraph>()
              .Count(p => p.FontSize >= 14.5) == 1);   // 只有"# 正文"是标题

        // ★ V1.11.3：标题后**直接接正文（无空行）**也必须渲染成标题（标题是行级块）
        var fd3 = MarkdownRenderer.Render("# 第三方\n正文\n");
        Check("标题后紧跟正文渲染成标题块", fd3 != null && fd3.Blocks.OfType<System.Windows.Documents.Paragraph>()
              .Any(p => p.FontSize >= 14.5));

        Console.WriteLine("== ⑤ UI 冒烟：WritingDeskView 能实例化（XAML 资源/绑定无错） ==");
        try
        {
            // 触发 App 资源加载，确保 XAML 里引用的 StaticResource 键都存在
            var app = new LiteCut.App();
            app.InitializeComponent();
            var view = new LiteCut.Workbench.WritingDesk.WritingDeskView();
            Check("WritingDeskView 实例化成功", view != null);
            // 注入空宿主（不弹窗）后重读不应崩
            var doc2 = new WritingDocument { Text = "# 标题\n\n正文\n" };
            Check("文档模型可用", doc2.Outline.Count == 1);

            // ★ V1.12.4：宿主接口新增的沉浸式收起默认实现 —— 空宿主调用是 no-op，不崩
            IWritingDeskHost bareHost = new BareHost();
            Check("宿主接口默认不收起导航", !bareHost.NavigationCollapsed);
            bareHost.SetNavigationCollapsed(true);   // 默认实现：什么都不做，不崩
            Check("默认实现调用后仍不收起", !bareHost.NavigationCollapsed);
        }
        catch (Exception ex)
        {
            Check($"UI 冒烟抛异常：{ex.Message}", false);
        }

        Console.WriteLine($"\n== 结果：{_pass} 通过，{_fail} 失败 ==");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>把目录树扁平化成有序列表（含子节点），方便按层级计数。</summary>
    static IEnumerable<OutlineNode> Flatten(IEnumerable<OutlineNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    /// <summary>只实现接口、不干任何事的空宿主：验证 IWritingDeskHost 新增成员的默认实现。</summary>
    private sealed class BareHost : IWritingDeskHost
    {
    }
}
