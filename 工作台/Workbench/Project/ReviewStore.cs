// ============================================================
// ReviewStore.cs - ★ V1.13.0 复盘数据（自媒体效果）
// ============================================================
//
// 【这一环原来缺了什么】
//   「立项 → 撰稿 → 录音 → 成片 → 发布」全在公司里跑通了，
//   唯独最后一环「效果到底怎么样」断了 —— 出了片就没下文。
//   结果是：做过 3 支片，哪支数据好、为什么好，**记不住也查不到**。
//
// 【三条设计判断（2026-10-15 与用户敲定）】
//   ① **手动录入，不碰平台接口** —— 抖音创作者后台没有公开 API，
//      网页抓取要登录态、随时被改版打断。第一版纯手动，一个字段都不自动抓。
//   ② **数据落本地、跟着项目走** —— &lt;项目&gt;\review\review.json。
//      好处是归档之后它还在本地：硬盘不插着也能查、能填、能做可视化。
//      （硬盘只是"文档归纳箱"，不参与项目管理。）
//   ③ **结构做成"多平台 × 多次快照"** —— 一条记录 = 某平台在某一天的那个数。
//      这样 7 天/30 天的数能比，将来画趋势线直接就有数据，
//      不用等"要做可视化了"再回头改结构。
//
// 【和归档的关系：完全解耦】
//   用户明确要求："不一定随时硬盘插着，很可能是做几个项目后集体处理。
//   所以复盘不能只有归档之后才能做。"
//   → 所以这一页在**进行中**和**已归档**的项目里都能进、都能填。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench.Project;

/// <summary>一个项目的全部复盘数据（review.json 的根）。</summary>
public sealed class ReviewFile
{
    /// <summary>结构版本。加字段不用动它（老文件读出来是默认值）。</summary>
    public int Version { get; set; } = 1;

    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";

    /// <summary>
    /// ★ V1.15.0 **这支片的发布日**（yyyy-MM-dd）。一支片只有一个 —— 跟平台无关。
    ///
    /// 【为什么项目级要单独有一个，而不从快照里推】
    ///   ① 账号页那张"更新节奏"热力图的数据源**只有发布日**。
    ///      如果它只能从快照里推，那么"刚发完片、还没去抄数据"的那几天格子永远不亮 ——
    ///      而"我这周到底发了没有"恰恰是热力图唯一要说的事。
    ///   ② 作品列表要能**行内直接改日期**，那它就必须有一个"某一行 = 某个日期"的落点，
    ///      不能靠"去作品页新建一条快照"。
    ///   放在这个文件里（而不是 project.json）：复盘的属性全在一个文件里，
    ///   project.json 的结构不用动，也就没有"保存时漏搬字段"的风险。
    ///
    /// 【和快照里那个 PublishDate 的关系】
    ///   快照里的 PublishDate 是"这条数据对应的那次发布"，用来算"距发布几天"；
    ///   这里是**这支片的发布日**。规则：这里的填了就以这里为准，
    ///   没填就退回"所有快照里最早的那个 PublishDate"（见 EffectivePublishDate）——
    ///   老数据一条都不用改，热力图照样亮。
    /// </summary>
    public string PublishDate { get; set; } = "";

    /// <summary>所有快照。一条 = 一个平台在某一天的一组数据。</summary>
    public List<ReviewRecord> Records { get; set; } = new();
}

/// <summary>一次数据快照。</summary>
public sealed class ReviewRecord
{
    public string Id { get; set; } = "";

    /// <summary>平台：抖音 / 视频号 / B站 / 小红书 / …（自由填）</summary>
    public string Platform { get; set; } = "抖音";

    /// <summary>发布日（yyyy-MM-dd）</summary>
    public string PublishDate { get; set; } = "";

    /// <summary>这份数据是哪天统计的（yyyy-MM-dd）</summary>
    public string StatsDate { get; set; } = "";

    /// <summary>距发布几天（7 / 30 …）。手填，用来给趋势线做横轴。</summary>
    public int DaysAfter { get; set; }

    // ---- 八个基本数 ----
    public long Play { get; set; }            // 播放
    public double FinishRate { get; set; }    // 完播率（%）
    public long Like { get; set; }            // 点赞
    public long Follow { get; set; }          // 涨粉
    public long Favorite { get; set; }        // 收藏
    public long Comment { get; set; }         // 评论
    public long Share { get; set; }           // 转发
    public long HomeVisit { get; set; }       // 主页访问

    // ---- 三段式复盘结论 ----
    public string GoodAt { get; set; } = "";      // 这条讲对了什么
    public string Missed { get; set; } = "";      // 哪里没接住
    public string NextChange { get; set; } = "";  // 下次改什么

    public string SavedAt { get; set; } = "";

    // ---- 自动算出来的（不存盘，只给界面和报告用）----

    public long InteractionTotal => Like + Favorite + Comment + Share;

    public double FollowPerThousand => Play > 0 ? Follow * 1000.0 / Play : 0;

    public string Summary =>
        $"{Platform} · {StatsDate} · 播放 {Play:N0} · 点赞 {Like:N0} · 涨粉 {Follow:N0}";
}

/// <summary>复盘数据的读写 + 报告生成。</summary>
public static class ReviewStore
{
    /// <summary>读一个项目的复盘数据（没有就返回一份空的，不返回 null）。</summary>
    public static ReviewFile Load(ProjectRef r)
    {
        var f = ContractJson.ReadFile<ReviewFile>(r.ReviewPath);
        if (f == null)
            return new ReviewFile { ProjectId = r.Model.Id, ProjectName = r.Model.Name };

        f.Records ??= new List<ReviewRecord>();
        if (string.IsNullOrWhiteSpace(f.ProjectId)) f.ProjectId = r.Model.Id;
        if (string.IsNullOrWhiteSpace(f.ProjectName)) f.ProjectName = r.Model.Name;
        return f;
    }

    /// <summary>写回 review.json。返回是否写成功。</summary>
    public static bool Save(ProjectRef r, ReviewFile f)
    {
        try
        {
            f.ProjectId = r.Model.Id;
            f.ProjectName = r.Model.Name;
            ContractJson.WriteFile(r.ReviewPath, f);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 生成一页人话的《复盘报告.md》。返回写到哪里了。
    ///
    /// 【为什么还要一份 .md】
    ///   数据结构是给"将来做可视化"用的（json），但人翻项目的时候想看的是话。
    ///   这份 md 就是"归档后仍然留在本地的那份报告"。
    /// </summary>
    public static string? WriteReport(ProjectRef r, ReviewFile f)
    {
        try
        {
            string text = BuildReport(r, f);
            Directory.CreateDirectory(r.ReviewDir);
            File.WriteAllText(r.ReviewReportPath, text, ContractJson.Utf8NoBom);
            return r.ReviewReportPath;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildReport(ProjectRef r, ReviewFile f)
    {
        var sb = new StringBuilder();
        var m = r.Model;

        sb.AppendLine($"# 复盘报告 · {m.Name}");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"> 立项时间：{m.CreatedAt:yyyy-MM-dd}");
        if (m.Archive != null)
        {
            sb.AppendLine($"> 归档时间：{m.Archive.ArchivedAt}");
            sb.AppendLine($"> 工程位置：`{m.Archive.TargetDir}`");
        }
        else
        {
            sb.AppendLine("> 归档时间：（还没归档）");
        }
        sb.AppendLine();

        if (f.Records.Count == 0)
        {
            sb.AppendLine("_还没有录入任何数据。去工作台的项目 →「复盘」页填一次，这里就会有内容。_");
            return sb.ToString();
        }

        // ---- 数据总表：一行一个快照，方便横向比 ----
        sb.AppendLine("## 数据快照");
        sb.AppendLine();
        sb.AppendLine("| 平台 | 发布日 | 统计日 | 距发布 | 播放 | 完播率 | 点赞 | 涨粉 | 收藏 | 评论 | 转发 | 主页访问 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var rec in f.Records.OrderByDescending(x => x.StatsDate))
        {
            sb.AppendLine($"| {rec.Platform} | {rec.PublishDate} | {rec.StatsDate} | {rec.DaysAfter} 天 | " +
                          $"{rec.Play:N0} | {rec.FinishRate:F1}% | {rec.Like:N0} | {rec.Follow:N0} | " +
                          $"{rec.Favorite:N0} | {rec.Comment:N0} | {rec.Share:N0} | {rec.HomeVisit:N0} |");
        }
        sb.AppendLine();

        // ---- 算出来的几个比值 ----
        sb.AppendLine("## 算出来的");
        sb.AppendLine();
        foreach (var rec in f.Records.OrderByDescending(x => x.StatsDate))
        {
            sb.AppendLine($"- **{rec.Platform} / {rec.StatsDate}**：互动总数 {rec.InteractionTotal:N0}，" +
                          $"每千播放涨粉 {rec.FollowPerThousand:F1}");
        }
        sb.AppendLine();

        // ---- 三段式结论 ----
        sb.AppendLine("## 复盘结论");
        sb.AppendLine();
        foreach (var rec in f.Records.OrderByDescending(x => x.StatsDate))
        {
            if (string.IsNullOrWhiteSpace(rec.GoodAt) &&
                string.IsNullOrWhiteSpace(rec.Missed) &&
                string.IsNullOrWhiteSpace(rec.NextChange))
                continue;

            sb.AppendLine($"### {rec.Platform} · {rec.StatsDate}");
            sb.AppendLine();
            sb.AppendLine("**这条讲对了什么**");
            sb.AppendLine();
            sb.AppendLine(Blank(rec.GoodAt));
            sb.AppendLine();
            sb.AppendLine("**哪里没接住**");
            sb.AppendLine();
            sb.AppendLine(Blank(rec.Missed));
            sb.AppendLine();
            sb.AppendLine("**下次改什么**");
            sb.AppendLine();
            sb.AppendLine(Blank(rec.NextChange));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "_（没写）_" : s.Trim();

    // ========================================================
    // 给界面用的小工具
    // ========================================================

    /// <summary>今天（yyyy-MM-dd）。</summary>
    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>解析日期字符串，认不了就返回 null。</summary>
    public static DateTime? TryParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, out d)) return d;
        return null;
    }

    /// <summary>按发布日算"距发布几天"。算不出来返回 0。</summary>
    public static int DaysBetween(string from, string to)
    {
        var a = TryParseDate(from);
        var b = TryParseDate(to);
        if (a == null || b == null) return 0;
        return Math.Max(0, (int)Math.Round((b.Value - a.Value).TotalDays));
    }

    // ========================================================
    // ★ V1.15.0 发布日（一支片只有一个）—— 账号页/作品列表都要用
    // ========================================================
    //
    // 全部走这两个方法，别在别处自己读字段：口径只有一处，
    // 否则迟早出现"列表显示一个日期、热力图按另一个日期点亮"。

    /// <summary>
    /// 这支片**实际生效的发布日**。
    /// 优先用项目级那个；没填就退回"所有快照里最早的一个 PublishDate"。
    /// 两边都没有 → 返回空串（= 还没发布 / 还没记）。
    /// </summary>
    public static string EffectivePublishDate(ProjectRef r)
    {
        var f = Load(r);
        if (!string.IsNullOrWhiteSpace(f.PublishDate)) return f.PublishDate.Trim();

        var earliest = f.Records
                        .Select(x => x.PublishDate)
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .OrderBy(d => d, StringComparer.Ordinal)
                        .FirstOrDefault();

        return earliest ?? "";
    }

    /// <summary>
    /// 改这支片的发布日（作品列表行内改 / 一键「已发布」都走这里）。
    /// 返回是否写成功；date 传空串 = 清掉。
    /// </summary>
    public static bool SetPublishDate(ProjectRef r, string date)
    {
        var f = Load(r);
        f.PublishDate = (date ?? "").Trim();
        return Save(r, f);
    }
}
