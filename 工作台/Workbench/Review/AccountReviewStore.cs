// ============================================================
// AccountReviewStore.cs - ★ V1.15.0 账号快照（手填的那几个账号级数）
// ============================================================
//
// 【它和 ReviewStore 的分工 —— 一句话】
//   ReviewStore        = **一支片**的数据（<项目>\review\review.json）
//   AccountReviewStore = **一个号**的数据（%APPDATA%\翊瑄的工作台\account-review.json）
//
// 【为什么账号数据不挂在任何项目底下】
//   "粉丝总量 2.4 万"不是某一支片的属性，是**整个号在某个时刻的状态**。
//   挂在某个项目下，会出现"删了那个项目，账号数据跟着没了"这种事。
//   所以它放在 AppData 里，跟项目完全解耦 —— 这跟"复盘不绑定归档"是同一条思路。
//
// 【为什么做成"多平台 × 多次快照"，而不是就存 4 个数】
//   ① 严格说"账号"= **某个平台上的号**：抖音号和视频号是两个号，粉丝数各是各的；
//   ② 存成快照（一条 = 某平台在某一天的一组数）之后，
//      将来画"在涨还是在平"的曲线**不用回头改数据结构**（B2 期直接用）。
//   这一条照抄 ReviewStore 的判断，两个文件长得一样才好维护。
//
// 【B1 期界面只显示"每个平台最新的一份"】
//   历史快照先存着、先不显示（曲线是 B2 期的事）。
//
// 【写盘铁律】一律无 BOM（R-13）—— 走 ContractJson.WriteFile，它已经是这个口径。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench.Review;

/// <summary>account-review.json 的根。</summary>
public sealed class AccountReviewFile
{
    /// <summary>结构版本。加字段不用动它（老文件读出来是默认值）。</summary>
    public int Version { get; set; } = 1;

    /// <summary>所有快照。一条 = 一个平台在某一天的一组账号数据。</summary>
    public List<AccountRecord> Records { get; set; } = new();
}

/// <summary>一次账号快照（手填）。</summary>
public sealed class AccountRecord
{
    public string Id { get; set; } = "";

    /// <summary>平台：抖音 / 视频号 / B站 / 小红书 / …（自由填）</summary>
    public string Platform { get; set; } = "抖音";

    /// <summary>这份数据是哪天统计的（yyyy-MM-dd）。</summary>
    public string StatsDate { get; set; } = "";

    // ---- 手填的四个数（就这四个，不许再加：能自动汇总的一律不让手填）----

    /// <summary>粉丝总量</summary>
    public long Fans { get; set; }

    /// <summary>账号总播放</summary>
    public long TotalPlay { get; set; }

    /// <summary>账号总点赞</summary>
    public long TotalLike { get; set; }

    /// <summary>近 30 天涨粉</summary>
    public long Fans30d { get; set; }

    /// <summary>备注（可选）</summary>
    public string Note { get; set; } = "";

    public string SavedAt { get; set; } = "";
}

/// <summary>
/// 把"手填的几个数"汇总成界面直接能显示的一份。
///
/// 【为什么要有这个中间对象，而不是界面自己去 aggregate】
///   界面上有四张卡 + 一个统计日，它们必须来自**同一次汇总**，
///   否则会出现"粉丝总量是抖音的、统计日是全部里最晚的"这种自相矛盾。
///   汇总这件事只在这里做一次，界面只负责显示。
/// </summary>
public sealed class AccountSnapshot
{
    /// <summary>有没有任何数据（没有 = 界面显示"—"，而不是显示 0）。</summary>
    public bool Has { get; set; }

    public long Fans { get; set; }
    public long TotalPlay { get; set; }
    public long TotalLike { get; set; }
    public long Fans30d { get; set; }

    /// <summary>这份汇总对应的统计日（取参与汇总的那些快照里最晚的一天）。</summary>
    public string StatsDate { get; set; } = "";

    /// <summary>参与汇总的平台个数（0 = 没数据，1 = 单个平台）。</summary>
    public int PlatformCount { get; set; }

    /// <summary>下面这行字的来源说明，界面直接显示（避免"算出来的被当成看到的"）。</summary>
    public string SourceNote =>
        !Has ? "还没填过账号数据"
             : PlatformCount <= 1 ? "你手填的那一次"
                                  : $"{PlatformCount} 个平台各取最新一次，相加";
}

/// <summary>账号快照的读写 + 汇总。所有碰 account-review.json 的代码都必须走这里。</summary>
public static class AccountReviewStore
{
    /// <summary>落点：%APPDATA%\翊瑄的工作台\account-review.json（跟项目解耦）。</summary>
    public static string FilePath => Path.Combine(AppConfig.DataDir, "account-review.json");

    /// <summary>读。读不到（第一次用 / 文件坏了）返回一份空的，绝不返回 null。</summary>
    public static AccountReviewFile Load()
    {
        var f = ContractJson.ReadFile<AccountReviewFile>(FilePath);
        if (f == null) return new AccountReviewFile();
        f.Records ??= new List<AccountRecord>();
        return f;
    }

    /// <summary>写回。返回是否写成功。顺手补上 Id / 保存时间。</summary>
    public static bool Save(AccountReviewFile f)
    {
        try
        {
            foreach (var r in f.Records)
            {
                if (string.IsNullOrWhiteSpace(r.Id)) r.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(r.SavedAt)) r.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            }

            Directory.CreateDirectory(AppConfig.DataDir);
            ContractJson.WriteFile(FilePath, f);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 汇总成界面上那四张卡。
    /// </summary>
    /// <param name="f">全部快照</param>
    /// <param name="platform">"" = 全部平台（各平台取最新一份再相加）；否则只看这一个平台</param>
    public static AccountSnapshot Build(AccountReviewFile f, string platform)
    {
        var picked = LatestPerPlatform(f, platform);
        if (picked.Count == 0) return new AccountSnapshot();

        return new AccountSnapshot
        {
            Has = true,
            Fans = picked.Sum(r => r.Fans),
            TotalPlay = picked.Sum(r => r.TotalPlay),
            TotalLike = picked.Sum(r => r.TotalLike),
            Fans30d = picked.Sum(r => r.Fans30d),
            StatsDate = picked.Select(r => r.StatsDate).Where(s => !string.IsNullOrWhiteSpace(s))
                              .OrderByDescending(s => s, StringComparer.Ordinal).FirstOrDefault() ?? "",
            PlatformCount = picked.Select(r => r.Platform).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        };
    }

    /// <summary>
    /// 每个平台各取**最新的一份**快照（口径和作品列表的合计完全一致 —— 这点必须一样，
    /// 否则上下两块的"最新"含义不同，数就对不上了）。
    /// </summary>
    public static List<AccountRecord> LatestPerPlatform(AccountReviewFile f, string platform)
    {
        var q = f.Records.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(platform))
            q = q.Where(r => string.Equals(r.Platform, platform, StringComparison.OrdinalIgnoreCase));

        return q.GroupBy(r => r.Platform ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.StatsDate, StringComparer.Ordinal)
                              .ThenByDescending(r => r.SavedAt, StringComparer.Ordinal)
                              .First())
                .ToList();
    }

    /// <summary>出现过哪些平台（给顶部「平台 ▾」用）。</summary>
    public static List<string> Platforms(AccountReviewFile f)
        => f.Records.Select(r => r.Platform)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

    /// <summary>
    /// 把界面上那四个数存成一条新快照（同平台同一天已有 → 覆盖那一条，不堆重复）。
    ///
    /// 【为什么"同平台同一天覆盖"】
    ///   一天之内改两次数（比如发现抄错了一位）不该在历史里留两条 ——
    ///   那就不是"某天的账号状态"，是"我按了几次保存"。
    /// </summary>
    public static bool SaveSnapshot(AccountReviewFile f, string platform, string statsDate,
                                    long fans, long totalPlay, long totalLike, long fans30d,
                                    string note)
    {
        platform = string.IsNullOrWhiteSpace(platform) ? "抖音" : platform.Trim();
        statsDate = string.IsNullOrWhiteSpace(statsDate) ? Today : statsDate.Trim();

        var exist = f.Records.FirstOrDefault(r =>
            string.Equals(r.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.StatsDate, statsDate, StringComparison.Ordinal));

        if (exist == null)
        {
            exist = new AccountRecord { Id = Guid.NewGuid().ToString("N") };
            f.Records.Add(exist);
        }

        exist.Platform = platform;
        exist.StatsDate = statsDate;
        exist.Fans = fans;
        exist.TotalPlay = totalPlay;
        exist.TotalLike = totalLike;
        exist.Fans30d = fans30d;
        exist.Note = note ?? "";
        exist.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        return Save(f);
    }

    /// <summary>今天（yyyy-MM-dd）。跟 ReviewStore.Today 同一个口径。</summary>
    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");
}
