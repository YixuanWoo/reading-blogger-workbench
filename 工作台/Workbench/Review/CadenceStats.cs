// ============================================================
// CadenceStats.cs - ★ V1.15.0 「更新节奏」的算法（纯算术，不碰界面）
// ============================================================
//
// 【这一块回答什么】
//   三个数：**窗口内发了多少支 · 最长连发多少天 · 现在空了多久**，
//   外加"每天发了几支"（热力图的格子颜色靠它）。
//   全部由**发布日**算出来 —— 一个字都不用填。
//
// 【和界面的边界】
//   这里只吐数字和"哪天几支"；格子多大、什么颜色、怎么摆，是控件的事。
//   所以这个文件能被无头验证直接断言（不用起 WPF）。
//
// 【口径必须和热力图完全一致】
//   网格怎么划的（几列、每列哪七天）在这里定义一次，
//   控件只按这个结果画。两边各算一遍迟早会差一格。
//
// 【为什么"只陈述不评判"是硬规矩】
//   这三个数只会被原样显示 —— 不标红、不弹窗、不算"目标完成率"。
//   它是指南针，不是监工：一旦开始催，人就会讨厌打开这一页。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteCut.Workbench.Review;

/// <summary>「更新节奏」算出来的全部东西。</summary>
public sealed class CadenceResult
{
    /// <summary>时间窗（周）。</summary>
    public int Weeks { get; set; }

    /// <summary>窗口第一天（周一）。</summary>
    public DateTime FirstDay { get; set; }

    /// <summary>算的时候认定的"今天"。</summary>
    public DateTime Today { get; set; }

    /// <summary>每天发了几支（只含窗口内、且不晚于今天的天）。</summary>
    public IReadOnlyDictionary<DateTime, int> PerDay { get; set; } = new Dictionary<DateTime, int>();

    /// <summary>窗口内一共发了多少支。</summary>
    public int Count { get; set; }

    /// <summary>窗口内有发布的天数（一天多支只算一天）。</summary>
    public int ActiveDays { get; set; }

    /// <summary>最长连发（天）—— 连续多少天每天都有发布会算进去。</summary>
    public int LongestStreak { get; set; }

    /// <summary>当前空档（天）。-1 = 一次都没记过（界面显示"—"，不是显示"空了很久"）。</summary>
    public int CurrentGap { get; set; } = -1;

    /// <summary>最近一次发布的日期（yyyy-MM-dd）。空 = 没有记录。</summary>
    public string LastPublish { get; set; } = "";

    /// <summary>窗口结束那天的日期（周日）—— 界面要画到这一列，含还没到的日子。</summary>
    public DateTime LastDay => FirstDay.AddDays(Weeks * 7 - 1);
}

/// <summary>更新节奏的计算。全是纯函数，没有任何状态。</summary>
public static class CadenceStats
{
    /// <summary>这一天所在那一周的**周一**（我们按中国习惯，一周从周一算起）。</summary>
    public static DateTime MondayOf(DateTime d)
    {
        int back = ((int)d.DayOfWeek + 6) % 7;   // 周一=0, 周二=1 … 周日=6
        return d.Date.AddDays(-back);
    }

    /// <summary>
    /// 算一遍。
    /// </summary>
    /// <param name="dates">所有作品的发布日（可以有重复、可以超窗、可以晚于今天）</param>
    /// <param name="weeks">窗口周数（13 / 26 / 52）</param>
    /// <param name="today">把"今天"传进来而不是自己去 DateTime.Now —— 这样能重复验证</param>
    public static CadenceResult Compute(IEnumerable<DateTime> dates, int weeks, DateTime today)
    {
        if (weeks < 1) weeks = 1;
        today = today.Date;

        var lastMonday = MondayOf(today);
        var firstDay = lastMonday.AddDays(-(weeks - 1) * 7);

        var perDay = new Dictionary<DateTime, int>();
        int count = 0;

        foreach (var raw in dates)
        {
            var d = raw.Date;
            if (d < firstDay || d > today) continue;   // 超窗 / 未来（填错的）不算

            perDay[d] = perDay.TryGetValue(d, out var n) ? n + 1 : 1;
            count++;
        }

        // 最近一次发布：看**全部**日期里不晚于今天的那个（哪怕它超了窗，
        // "现在空了多久"也不该被窗口切断）
        var all = dates.Select(x => x.Date).Where(x => x <= today).ToList();
        string last = all.Count > 0 ? all.Max().ToString("yyyy-MM-dd") : "";

        return new CadenceResult
        {
            Weeks = weeks,
            FirstDay = firstDay,
            Today = today,
            PerDay = perDay,
            Count = count,
            ActiveDays = perDay.Count,
            LongestStreak = LongestStreak(perDay.Keys),
            CurrentGap = all.Count == 0 ? -1 : Math.Max(0, (int)(today - all.Max()).TotalDays),
            LastPublish = last,
        };
    }

    /// <summary>最长连发（天）：把有发布的日子排序，数最长的连续段。</summary>
    private static int LongestStreak(IEnumerable<DateTime> days)
    {
        var sorted = days.Distinct().OrderBy(d => d).ToList();
        if (sorted.Count == 0) return 0;

        int best = 1, cur = 1;
        for (int i = 1; i < sorted.Count; i++)
        {
            if ((sorted[i] - sorted[i - 1]).TotalDays == 1) cur++;
            else cur = 1;
            if (cur > best) best = cur;
        }
        return best;
    }

    /// <summary>
    /// 网格里的每一格对应哪一天（从最左列到最右列、每列从周一到周日）。
    /// 控件按这个顺序画格子；统计与显示共用同一份，不会错位。
    /// </summary>
    public static List<DateTime> GridDays(CadenceResult r)
    {
        var list = new List<DateTime>(r.Weeks * 7);
        for (int i = 0; i < r.Weeks * 7; i++) list.Add(r.FirstDay.AddDays(i));
        return list;
    }
}
