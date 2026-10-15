// ============================================================
// WorksBoard.cs - ★ V1.15.0 作品列表（账号页下面那张表）
// ============================================================
//
// 【它是什么】
//   把"项目列表"变成"作品列表"：一行 = 一支片，
//   数字由各项目自己的 review.json **自动汇总**出来，人一个字都不用填。
//
// 【合计口径 —— 和账号快照那边必须一模一样】
//   **每个平台各取它最新的那一份快照，再相加。**
//   只摆**能相加**的数（播放 / 点赞 / 涨粉）；
//   ★ 完播率是比率、加不了，硬加就是编数 —— 所以它**不进**这张表，
//     它只出现在作品页里。
//
// 【为什么算已归档的】
//   已归档的片子数据还在本地（<项目>\review 就是为这个留的）。
//   不算的话账号复盘会凭空少一块。
//
// 【"待填数据"是白捡的好处】
//   没录过数据的片子直接标出来 → 复盘待办自己就浮出来了，不用另外记一份。
//
// 【这里没有缓存】
//   只读几十个小 json，实测是毫秒级。加了缓存反而要维护失效时机，
//   而失效时机错了就是"改了数不刷新"这种最难查的问题。真慢再说。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench.Review;

/// <summary>作品列表里的一行。</summary>
public sealed class WorkRow : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>对应的项目（点进作品页要用它）。</summary>
    public ProjectRef Ref { get; init; } = new();

    public string Name => Ref.Model.Name;
    public bool IsArchived => Ref.IsArchived;

    /// <summary>参与合计的快照条数（0 = 待填数据）。</summary>
    public int RecordCount { get; set; }

    /// <summary>合计口径下的三个数。</summary>
    public long Play { get; set; }
    public long Like { get; set; }
    public long Follow { get; set; }

    /// <summary>这支片发过哪些平台（如 "抖音 + 视频号"）。</summary>
    public string Platforms { get; set; } = "";

    private string _publishDate = "";

    /// <summary>
    /// 这支片的发布日（yyyy-MM-dd）。**可写** —— 列表里行内直接改，
    /// 改完立刻落 review.json（热力图的格子跟着亮/灭）。
    ///
    /// 【为什么要 INotifyPropertyChanged】
    ///   行内那个输入框是双向绑定的。用户在框里敲了 "2026/10/2" 之后，
    ///   我们把它规范成 "2026-10-02" 存盘 —— 得让框里的字**跟着变成规范写法**，
    ///   否则"存的是 A、显示的是 B"，下次看到就以为没改成功。
    /// </summary>
    public string PublishDate
    {
        get => _publishDate;
        set
        {
            string v = value ?? "";
            if (v == _publishDate) return;
            _publishDate = v;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PublishDate)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>有没有录过数据。</summary>
    public bool HasData => RecordCount > 0;

    /// <summary>
    /// 这一行的稳定键 = 项目**文件夹名**。
    ///
    /// 【为什么不用 Model.Id 或 Model.Name】
    ///   Id：老档案里可能是空的，ProjectStore 兜底时每次扫描现发一个新 GUID（不稳定）；
    ///   Name：能改名。
    ///   文件夹名跟着项目走、唯一、且改名不动它 —— 正好当"这一行是谁"的外号。
    ///   调试遥控口按它点某一行，比按"第几条"稳得多（排序一变就不对了）。
    /// </summary>
    public string Key => System.IO.Path.GetFileName(Ref.Dir);

    /// <summary>给遥控口/测试用的稳定 Id（"work:open:<文件夹名>"）。</summary>
    public string OpenButtonId => "work:open:" + Key;

    /// <summary>给遥控口/测试用的稳定 Id（"work:date:<文件夹名>"）。</summary>
    public string DateBoxId => "work:date:" + Key;

    /// <summary>给遥控口/测试用的稳定 Id（"work:today:<文件夹名>"）。</summary>
    public string TodayButtonId => "work:today:" + Key;

    /// <summary>状态角标：没数据 → 待填数据（这是"复盘待办"的那一眼）。</summary>
    public string StatusText => !HasData ? "待填数据" : (IsArchived ? "已归档" : "进行中");

    public string PlayText => !HasData ? "—" : Num(Play);
    public string LikeText => !HasData ? "—" : Num(Like);
    public string FollowText => !HasData ? "—" : Num(Follow);
    public string PlatformsText => string.IsNullOrWhiteSpace(Platforms) ? "—" : Platforms;

    /// <summary>发布日没填就显式说"未填"，而不是留个空格让人以为是程序坏了。</summary>
    public string PublishText => string.IsNullOrWhiteSpace(PublishDate) ? "（未填）" : PublishDate;

    /// <summary>给排序用：有日期的排前、按日期倒序；没日期的排最后。</summary>
    internal string DateSortKey => string.IsNullOrWhiteSpace(PublishDate) ? "0000-00-00" : PublishDate;

    /// <summary>数字的人话格式（跟抖音后台那种。0 也显示 0，只有"没数据"才是 —）。</summary>
    public static string Num(long v)
    {
        if (v >= 100_000_000) return (v / 100_000_000.0).ToString("0.#") + "亿";
        if (v >= 10_000) return (v / 10_000.0).ToString("0.#") + "万";
        return v.ToString("N0");
    }
}

/// <summary>作品列表的装配。纯函数（读盘，但不碰界面）。</summary>
public static class WorksBoard
{
    /// <summary>
    /// 铺出作品列表。
    /// </summary>
    /// <param name="platform">
    /// "" = 全部平台：一支片把所有平台的数合起来（一行 = 一支片）。
    /// 非空 = 只看那个平台：**只列在那个平台有记录的片子**（没在那儿发过，列出来是噪声）。
    /// </param>
    public static List<WorkRow> Build(string platform)
    {
        bool all = string.IsNullOrWhiteSpace(platform);
        var rows = new List<WorkRow>();

        foreach (var r in ProjectStore.LoadAll())
        {
            var f = ReviewStore.Load(r);

            var picked = all
                ? f.Records.GroupBy(x => x.Platform ?? "", StringComparer.OrdinalIgnoreCase)
                          .Select(g => g.OrderByDescending(x => x.StatsDate, StringComparer.Ordinal)
                                        .ThenByDescending(x => x.SavedAt, StringComparer.Ordinal)
                                        .First())
                          .ToList()
                : f.Records.Where(x => string.Equals(x.Platform, platform, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(x => x.StatsDate, StringComparer.Ordinal)
                           .ThenByDescending(x => x.SavedAt, StringComparer.Ordinal)
                           .Take(1)
                           .ToList();

            // 筛了某个平台、而这片没在那个平台发过 → 不列
            if (!all && picked.Count == 0) continue;

            rows.Add(new WorkRow
            {
                Ref = r,
                RecordCount = picked.Count,
                Play = picked.Sum(x => x.Play),
                Like = picked.Sum(x => x.Like),
                Follow = picked.Sum(x => x.Follow),
                Platforms = string.Join(" + ", picked.Select(x => x.Platform)
                                                     .Where(p => !string.IsNullOrWhiteSpace(p))
                                                     .Distinct(StringComparer.OrdinalIgnoreCase)),
                PublishDate = ReviewStore.EffectivePublishDate(r),
            });
        }

        return rows.OrderByDescending(x => x.DateSortKey, StringComparer.Ordinal)
                   .ThenByDescending(x => x.Ref.Model.UpdatedAt)
                   .ThenBy(x => x.Name, StringComparer.Ordinal)
                   .ToList();
    }

    /// <summary>
    /// 所有作品的发布日（热力图的数据源）。解析不出来的直接跳过 ——
    /// 热力图宁可少一格，也不能因为一个手误的日期整体报错。
    /// </summary>
    public static List<DateTime> PublishDates()
    {
        var dates = new List<DateTime>();

        foreach (var r in ProjectStore.LoadAll())
        {
            var d = ReviewStore.TryParseDate(ReviewStore.EffectivePublishDate(r));
            if (d != null) dates.Add(d.Value);
        }

        return dates;
    }

    /// <summary>
    /// 所有出现过的平台（账号快照里的 + 各作品快照里的），给顶部「平台 ▾」用。
    /// 两边都要看：账号页填过但作品还没填的平台，也该能筛。
    /// </summary>
    public static List<string> Platforms()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in AccountReviewStore.Platforms(AccountReviewStore.Load()))
            set.Add(p);

        foreach (var r in ProjectStore.LoadAll())
            foreach (var rec in ReviewStore.Load(r).Records)
                if (!string.IsNullOrWhiteSpace(rec.Platform))
                    set.Add(rec.Platform.Trim());

        return set.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
