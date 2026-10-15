// ============================================================
// CadenceCard.xaml.cs - ★ V1.15.0 主图卡：更新节奏
// ============================================================
//
// 【它只做两件事】
//   ① 把一串发布日交给 CadenceStats 算，② 把算出来的东西摆到界面上。
//   它**不读盘、不认识项目** —— 数据由账号页递进来（SetDates）。
//   这样将来"主图换成别的内容"时，这个文件可以直接被另一个卡片替换掉。
//
// 【时间窗】
//   13 周（一季度） / 26 周（半年，默认） / 52 周（一年）。
//   26 周是刻意的起点：新号攒不满一年，一上来就铺 52 周左边会空一大块；
//   26 周刚好铺满、格子也够大。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiteCut.Workbench.Review;

public partial class CadenceCard : UserControl
{
    private sealed record RangeItem(int Weeks, string Label)
    {
        public override string ToString() => Label;
    }

    private IReadOnlyList<DateTime> _dates = new List<DateTime>();
    private int _weeks = 26;

    public CadenceCard()
    {
        InitializeComponent();

        PickRange.ItemsSource = new List<RangeItem>
        {
            new(13, "近 13 周"),
            new(26, "近 26 周"),
            new(52, "近一年"),
        };
        PickRange.SelectedIndex = 1;   // 默认半年

        BuildLegend();
        Reload();
    }

    /// <summary>账号页递进来的数据（所有作品的发布日）。</summary>
    public void SetDates(IReadOnlyList<DateTime> dates)
    {
        _dates = dates ?? new List<DateTime>();
        Reload();
    }

    /// <summary>现在看的是多少周（外面想知道时读它）。</summary>
    public int Weeks => _weeks;

    // ========================================================
    // 算 + 摆
    // ========================================================

    private void Reload()
    {
        var r = CadenceStats.Compute(_dates, _weeks, DateTime.Today);

        Chart.SetData(r.PerDay, r.Weeks, r.FirstDay, r.Today);

        TxtCount.Text = r.Count > 0 ? r.Count + " 支" : "—";
        LblCount.Text = $"近 {r.Weeks} 周发布";
        FootCount.Text = r.ActiveDays > 0 ? $"落在 {r.ActiveDays} 天里" : "";

        TxtStreak.Text = r.LongestStreak > 0 ? r.LongestStreak + " 天" : "—";
        LblStreak.Text = "最长连发";
        FootStreak.Text = r.LongestStreak > 0 ? "连续每天都有发" : "";

        TxtGap.Text = r.CurrentGap >= 0 ? r.CurrentGap + " 天" : "—";
        LblGap.Text = "当前空档";
        FootGap.Text = r.CurrentGap > 0 ? $"最近一次 {r.LastPublish}" : "";

        // 一条记录都没有 → 给一句"怎么让它亮起来"的提示。
        // ★ 这句话只说"怎么做"，不带"你该多发了"的味道。
        bool empty = _dates.Count == 0;
        TxtHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TxtHint.Text = "还没有发布记录 —— 在下面的作品列表里填上「发布日」（或点「已发布」），格子就亮了。";
    }

    private void PickRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PickRange.SelectedItem is RangeItem it)
        {
            _weeks = it.Weeks;
            Reload();
        }
    }

    /// <summary>图例：少 ▢▢▢▢ 多（颜色跟格子完全一致的四个小方块）。</summary>
    private void BuildLegend()
    {
        Legend.Children.Add(new TextBlock
        {
            Text = "少",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x61, 0x69)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });

        foreach (string hex in new[] { "#FF131820", "#FF8A6A1E", "#FFC99A2E", "#FFE8B54A" })
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();

            Legend.Children.Add(new Rectangle
            {
                Width = 10,
                Height = 10,
                RadiusX = 2,
                RadiusY = 2,
                Fill = b,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
            });
        }

        Legend.Children.Add(new TextBlock
        {
            Text = "多",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x61, 0x69)),
            VerticalAlignment = VerticalAlignment.Center,
        });
    }
}
