// ============================================================
// RenderTasksFile.cs - 第④步留下的任务包 / 产物目录，谁都能读
// ============================================================
//
// 【为什么抽出来】
//   以前"读 04-render/render-tasks.json 还原镜头表"这段只写在渲染页里（private）。
//   组接页要拼片子，需要同一份数据 —— 再抄一遍就是两处各写一遍，
//   将来字段一改（比如这次新增的 durationSec），两边很容易走偏。
//   所以统一放这儿，渲染页和组接页都读它。
//
// 【为什么需要"退回读磁盘"这条路】
//   用户完全可能打开项目直接点进渲染页/组接页（镜头表是第③步的产物，在内存里）。
//   如果这时内存里的分镜表是空的，页面就成了一张白纸 ——
//   明明磁盘上 stages/04-render/render-tasks.json 里躺着镜头。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LiteCut.Workbench;

/// <summary>
/// 渲染任务包（第④步的产物）的读取与路径推导。
/// </summary>
public static class RenderTasksFile
{
    public const string FileName = "render-tasks.json";

    /// <summary>
    /// 从 stages/04-render 读回镜头表。读不到/坏了返回空表（页面显示空态）。
    /// </summary>
    public static List<ShotRow> Load(string stageDir)
    {
        var list = new List<ShotRow>();
        try
        {
            string p = Path.Combine(stageDir, FileName);
            if (!File.Exists(p)) return list;

            using var doc = JsonDocument.Parse(File.ReadAllText(p, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("tasks", out var arr)) return list;

            foreach (var t in arr.EnumerateArray())
            {
                double start = Num(t, "startSec");
                double end = Num(t, "endSec");
                // durationSec 是块级任务表新加的字段，更准（endSec 有时是算出来的）
                double dur = Num(t, "durationSec");
                if (dur > 0.001) end = start + dur;

                var row = new ShotRow
                {
                    Index = t.TryGetProperty("index", out var i) ? i.GetInt32() : 0,
                    StartSec = start,
                    EndSec = end,
                    Text = Str(t, "script"),
                    Description = Str(t, "visualIdea"),
                    Motion = Str(t, "motion"),
                    Image = Str(t, "imageNeed"),
                    ImageFile = Str(t, "imageFile"),
                };
                if (row.Index > 0) list.Add(row);
            }

            // 配图实际文件：任务包里的 imageFile 常常是空的（回扫时没补写），
            // 而 agent-images.json 里有 —— 两边拼一下，"这镜用了哪张图"才显示得对。
            string ip = Path.Combine(stageDir, "agent-images.json");
            if (File.Exists(ip))
            {
                using var idoc = JsonDocument.Parse(File.ReadAllText(ip, Encoding.UTF8));
                var byIdx = new Dictionary<int, string>();
                foreach (var el in idoc.RootElement.EnumerateArray())
                {
                    int idx = el.TryGetProperty("index", out var i2) ? i2.GetInt32() : 0;
                    string f = Str(el, "file");
                    if (idx > 0 && f.Length > 0) byIdx[idx] = f;
                }
                foreach (var r in list)
                    if (byIdx.TryGetValue(r.Index, out var f) && r.ImageFile.Length == 0)
                        r.ImageFile = f;
            }
        }
        catch { /* 任务包坏了就当没有，页面显示空态 */ }

        return list.OrderBy(r => r.Index).ToList();
    }

    /// <summary>
    /// 渲染产物目录。
    /// 优先内存里的设置 → 项目里留的 render-spec.json（重开项目后照样认得）→ 按约定推。
    /// </summary>
    public static string ResolveOutputDir(string stageDir, string memoryDir,
                                          string engineRoot, string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(memoryDir)) return memoryDir;

        var saved = RenderSpec.Load(stageDir);
        if (saved != null && !string.IsNullOrWhiteSpace(saved.OutputDir)) return saved.OutputDir;

        // 兜底：按 2026-10-01 的约定推 —— <项目夹>\渲染产物\。
        // stageDir 是 <项目夹>\stages\04-render，往上两级就是项目夹。
        try
        {
            string? proj = Path.GetDirectoryName(Path.GetDirectoryName(stageDir));
            if (!string.IsNullOrWhiteSpace(proj)) return RenderSpec.RenderOutputDirOf(proj);
        }
        catch { }

        return Path.Combine(engineRoot ?? "",
                            "renders", Path.GetFileNameWithoutExtension(sourcePath ?? ""));
    }

    /// <summary>某个镜号的产物路径。</summary>
    public static string Mp4Of(string outputDir, int index) =>
        Path.Combine(outputDir, $"shot_{index:D3}.mp4");

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
