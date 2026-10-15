// ============================================================
// WritingDocumentStore.cs - 文档的读写
// ============================================================
//
// 【这一层负责三件事】
//   ① 按「路径」读 .md → WritingDocument
//   ② 按「路径」把 WritingDocument 原子写回 .md
//   ③ 读/写"本机视图状态"（view-state.json：预览开关）
//
// 【与旧 ScriptStore 的区别】
//   旧版依赖 <see cref="LiteCut.Workbench.Project.ProjectRef"/> 算路径，
//   本层改为**按传入的路径读写** —— 它不知道"项目"是什么。
//   将来独立成写作软件，传入任意文件路径即可，整个文件夹直接带走。
//
// 【★ 与 Obsidian 的关系：单向、显式】
//   打开时  读一次 .md → WritingDocument
//   保存时  由文档整份重写 .md
//   程序开着时你在 Obsidian 改了   ★ 不管，下次打开才读
//
// 【★ 写盘一律无 BOM】（R-13 的教训，别用 Encoding.UTF8）
//   .md 用户要在 Obsidian 里看，带 BOM 会让某些解析器报错。
// ============================================================

using System;
using System.IO;
using System.Text;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench.WritingDesk;

/// <summary>
/// Markdown 文档的读写。所有碰 .md 的代码都必须走这里。
/// </summary>
public static class WritingDocumentStore
{
    // ========================================================
    // 读
    // ========================================================

    /// <summary>
    /// 按路径读 .md → 文档。文件不存在当空稿（新稿子的正常路径，不该报错）。
    /// </summary>
    public static WritingDocument Load(string path)
    {
        var doc = new WritingDocument { HasFile = File.Exists(path) };
        if (!doc.HasFile) return doc;

        doc.Text = File.ReadAllText(path, Encoding.UTF8);
        doc.SavedAt = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        return doc;
    }

    // ========================================================
    // 写
    // ========================================================

    /// <summary>
    /// 按路径把文档**整份重写**成 .md（不是追加、不是改一段）。
    /// 原子写：先写 .tmp 再换名 —— 半截文件丢稿是不可接受的损失。
    /// </summary>
    public static void Save(string path, WritingDocument doc)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        AtomicWriteText(path, doc.Text ?? "");
        doc.HasFile = true;
        doc.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>原子写 + 无 BOM。</summary>
    private static void AtomicWriteText(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, ContractJson.Utf8NoBom);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    // ========================================================
    // 本机视图状态（view-state.json）
    // ========================================================
    // 只存"本机 UI 状态"（预览开关），不随文档迁移。
    // 它跟 .md 是两回事：.md 是稿子，会去 Obsidian、会独立成软件；
    // 这个 json 只是"这台机器上次怎么看这篇稿子"的备忘。

    private sealed class ViewState
    {
        public bool PreviewOpen { get; set; }
    }

    /// <summary>读视图状态文件；读不出来/不存在返回默认（预览关）。</summary>
    public static bool LoadPreviewState(string statePath)
    {
        if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath)) return false;
        var s = ContractJson.ReadFile<ViewState>(statePath);
        return s?.PreviewOpen ?? false;
    }

    /// <summary>写视图状态文件；失败静默（本机备忘，写不上不影响用）。</summary>
    public static void SavePreviewState(string statePath, bool previewOpen)
    {
        if (string.IsNullOrEmpty(statePath)) return;
        try
        {
            string? dir = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            ContractJson.WriteFile(statePath, new ViewState { PreviewOpen = previewOpen });
        }
        catch
        {
            // 本机备忘，写不上不影响用
        }
    }
}
