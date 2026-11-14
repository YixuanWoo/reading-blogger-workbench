// ============================================================
// ProjectLabel.cs - ★ V1.20.0 项目标签（自动推导 + 手动说了算）
// ============================================================
//
// 【为什么要这一层】
//   以前工作台靠"某个固定目录里有没有成片"判断项目做到哪一步。
//   但真实情况经常是：视频交给了外部 Agent 做完，成片放在项目根目录、
//   甚至压根不在项目夹里 —— 于是工作台认为"还没出片"，归档按钮直接拒绝。
//
//   用户 2026-11-14 拍板的核心思路：
//     「标签是一等公民。软件按流程自动给个建议，但用户随时能手动改，
//       改了就以用户为准；绝对不许拿『指定文件夹里必须有个视频』卡人。」
//
// 【三条不可动摇的规矩】
//   ① 手动 > 自动。Label.Manual 非空时，自动推导结果一律不生效。
//   ② 自动只是"读磁盘事实算出来的当前建议值"，不写盘、不额外存一份状态，
//      所以流程往前走它自己会跟着变，不会出现"标签和进度对不上"。
//   ③ 登记的外部成片**只是一条路径**，工作台不复制、不移动、不删除、
//      也不要求它此刻一定存在（文件挪走了不影响项目和复盘）。
//
// 【为什么判据集中在这一个文件】
//   "这个项目算不算做完了"这种话只允许有一个口径。卡片徽标、标签弹窗、
//   归档门槛全调这里，免得三处各写一套、以后越改越乱（R-17/R-19 的教训）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteCut.Workbench.Project;

/// <summary>项目标签的取值。用常量中文串而不是枚举：将来加标签不用改存档格式。</summary>
public static class ProjectLabels
{
    public const string Writing = "文案中";
    public const string Recording = "录音中";
    public const string Editing = "剪辑中";
    public const string Done = "已完成";
    public const string Published = "已发布";
    public const string Archived = "已归档";

    /// <summary>
    /// 用户可以手动挑的标签。
    /// 「已归档」不在这个列表里 —— 它由归档动作本身决定，不是一句声明能给的。
    /// </summary>
    public static readonly string[] Selectable =
    {
        Writing, Recording, Editing, Done, Published,
    };

    /// <summary>流程顺序（从早到晚），弹窗里按这个排。</summary>
    public static readonly string[] All =
    {
        Writing, Recording, Editing, Done, Published, Archived,
    };

    /// <summary>把存档里读到的标签规整一下：不认识的值当作"没手动设过"。</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return All.Contains(s.Trim()) ? s.Trim() : "";
    }

    /// <summary>
    /// 这个项目当前该显示哪个标签。★ 手动优先。
    /// 唯一例外：已归档是**事实状态**（用户主动点了归档），归档期间显示「已归档」；
    /// 手动标签原样留着，取回工程后自动恢复 —— 不丢用户的选择。
    /// </summary>
    public static string Current(ProjectRef r)
    {
        if (r.IsArchived) return Archived;

        string manual = Normalize(r.Model.Label?.Manual);
        return manual.Length > 0 ? manual : Auto(r);
    }

    /// <summary>当前标签是用户手动定的，还是软件按流程推的。</summary>
    public static bool IsManual(ProjectRef r) =>
        !r.IsArchived && Normalize(r.Model.Label?.Manual).Length > 0;

    /// <summary>手动设定的时间（人话，用于弹窗显示）。</summary>
    public static string ManualSetAt(ProjectRef r) => r.Model.Label?.ManualSetAt ?? "";

    /// <summary>
    /// 归档门槛用的唯一判据：这支片算不算"交付了"。
    /// ★ 这里**不检查任何成片文件** —— 用户手动说完成了，就是完成了。
    /// </summary>
    public static bool IsDelivered(ProjectRef r)
    {
        string cur = Current(r);
        return cur == Done || cur == Published || cur == Archived;
    }

    /// <summary>
    /// 按磁盘事实推出来的建议标签。只是建议 —— 手动标签在 <see cref="Current"/> 里优先。
    /// 顺序是从"最靠后"往回判，先命中哪个算哪个。
    /// </summary>
    public static string Auto(ProjectRef r)
    {
        if (r.IsArchived) return Archived;
        if (HasPublishDate(r)) return Published;
        if (HasFilm(r)) return Done;
        if (HasEdit(r)) return Editing;
        if (HasAudio(r)) return Recording;
        return Writing;
    }

    /// <summary>给弹窗看的一句人话：这个自动标签是凭什么给的。</summary>
    public static string AutoReason(ProjectRef r)
    {
        try
        {
            if (r.IsArchived) return "项目已归档，标签由归档状态决定";

            string pub = ReviewStore.EffectivePublishDate(r);
            if (!string.IsNullOrWhiteSpace(pub)) return $"复盘里记了发布日 {pub}";

            int films = LocalFilms(r).Count;
            if (films > 0) return $"项目里有 {films} 个成片（任意位置都算，不限固定目录）";
            if ((r.Model.Label?.ExternalFilms?.Count ?? 0) > 0)
                return "登记了外部成片路径（工作台不检查它是否存在）";

            if (r.Model.FineCut.Clips.Count > 0 || r.Model.Shots.Rows.Count > 0)
                return "精剪时间轴 / 分镜表已经有内容";
            if (HasStageFiles(r)) return "渲染或组接目录里已经有产物";
            if (HasAudio(r)) return "录音母带或转写稿已经就位";
            if (HasScript(r)) return "稿子已经写好，还没开始录音";
            return "还没看到流程产物，默认从文案中开始";
        }
        catch
        {
            return "读流程产物时出了点问题，先按默认标签显示";
        }
    }

    // ========================================================
    // 下面全是"看磁盘事实"的只读探针
    // ========================================================

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".avi" };

    private static bool HasPublishDate(ProjectRef r)
    {
        try { return !string.IsNullOrWhiteSpace(ReviewStore.EffectivePublishDate(r)); }
        catch { return false; }
    }

    /// <summary>
    /// 有没有成片线索。★ 故意放宽：任意位置都算，绝不限定 stages\05-assemble / export。
    /// 外部登记的路径也算 —— 只认"用户说过有这么个东西"，不检查文件在不在。
    /// </summary>
    private static bool HasFilm(ProjectRef r)
    {
        if (LocalFilms(r).Count > 0) return true;
        if (r.Model.Label?.ExternalFilms?.Count > 0) return true;

        string last = r.Model.Output.LastExportPath ?? "";
        if (last.Length > 0)
        {
            try { if (File.Exists(last)) return true; } catch { }
        }
        return false;
    }

    /// <summary>项目夹里能找到的成片（相对路径）。清单展示用，不是门槛。</summary>
    public static List<string> LocalFilms(ProjectRef r)
    {
        var list = new List<string>();
        try
        {
            foreach (string sub in new[] { r.AssembleDir, r.ExportDir })
            {
                if (!Directory.Exists(sub)) continue;
                foreach (string f in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    if (VideoExts.Contains(Path.GetExtension(f)))
                        list.Add(Path.GetRelativePath(r.Dir, f));
            }

            // ★ 视频交出去做、拿回来随手放项目根目录 —— 这是最常见的情况。
            //   只扫根目录一层，不递归（递归会把素材夹里的东西也算成成片）。
            foreach (string f in Directory.EnumerateFiles(r.Dir, "*", SearchOption.TopDirectoryOnly))
                if (VideoExts.Contains(Path.GetExtension(f)))
                {
                    string rel = Path.GetFileName(f);
                    if (!list.Contains(rel)) list.Add(rel);
                }
        }
        catch { /* 目录读不了就当没有 */ }

        return list;
    }

    private static bool HasEdit(ProjectRef r)
    {
        try
        {
            if (r.Model.FineCut.Clips.Count > 0) return true;
            if (r.Model.Shots.Rows.Count > 0 || r.Model.Shots.DraftRows.Count > 0) return true;
            return HasStageFiles(r);
        }
        catch { return false; }
    }

    private static bool HasStageFiles(ProjectRef r)
    {
        foreach (string sub in new[] { r.RenderOutputDir, r.RenderStageDir, r.AssembleDir })
        {
            try
            {
                if (Directory.Exists(sub) && Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Any())
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static bool HasAudio(ProjectRef r)
    {
        try
        {
            if (File.Exists(Path.Combine(r.Dir, "素材", "录音", "vo.wav"))) return true;
            if (File.Exists(Path.Combine(r.TranscriptDir, "transcript.json"))) return true;

            string src = r.Model.Source.AudioPath ?? "";
            if (src.Length > 0 && File.Exists(src)) return true;
        }
        catch { }
        return false;
    }

    private static bool HasScript(ProjectRef r)
    {
        try
        {
            string p = Path.Combine(r.Dir, "script.md");
            return File.Exists(p) && new FileInfo(p).Length > 0;
        }
        catch { return false; }
    }
}
