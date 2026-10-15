// ============================================================
// CadenceHeatmap.xaml.cs - ★ V1.15.0 日打卡热力图（通用控件）
// ============================================================
//
// 【对外只有一件事】
//   SetData(每天几支, 几周, 今天)  → 画出来。别的什么都不管。
//
// 【网格怎么划】跟 CadenceStats 完全同一套：
//   最后一列 = 包含"今天"的那一周，每列从周一到周日，
//   往左一列就是往前一周。所以窗口天数 = 周数 × 7。
//
// 【格宽怎么定】
//   格宽 = （可用宽度 - 间隔 × (列数-1)）/ 列数
//   → 拖窗口、切 13/26/52 周都不用改样式，只改列数（"加近一年"不用新写一套画法）。
//
// 【悬停】
//   每格挂一个原生 ToolTip（"10-02 周三 · 2 支"）—— 不点也能看到是哪天。
//   按规划文档的口径：点格子暂时不做动作（B1 只做"点列表行进详情页"）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiteCut.Workbench.Controls;

public partial class CadenceHeatmap : UserControl
{
    // ---- 配色集中在这里，改主题只改这几行 ----
    private static readonly Brush FillEmpty = Freeze("#FF131820");
    private static readonly Brush StrokeEmpty = Freeze("#FF232A33");
    private static readonly Brush FillOne = Freeze("#FF8A6A1E");
    private static readonly Brush StrokeOne = Freeze("#FF6B5316");
    private static readonly Brush FillTwo = Freeze("#FFC99A2E");
    private static readonly Brush StrokeTwo = Freeze("#FFA87F22");
    private static readonly Brush FillMany = Freeze("#FFE8B54A");
    private static readonly Brush StrokeMany = Freeze("#FFC99A2E");
    private static readonly Brush TodayRing = Freeze("#FF9A9890");
    private static readonly Brush LabelBrush = Freeze("#FF6E6C66");

    private static readonly string[] WeekdayLabels = { "一", "二", "三", "四", "五", "六", "日" };

    /// <summary>左边星期几那一列占的宽度。</summary>
    private const double LeftGutter = 18;

    /// <summary>顶上月历那一行占的高度。</summary>
    private const double TopGutter = 15;

    private IReadOnlyDictionary<DateTime, int> _counts = new Dictionary<DateTime, int>();
    private int _weeks = 26;
    private DateTime _firstDay = DateTime.Today;
    private DateTime _today = DateTime.Today;

    public CadenceHeatmap()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
    }

    /// <summary>
    /// 铺数据并重画。
    /// </summary>
    /// <param name="perDay">每天发了几支（只含窗口内的日期）</param>
    /// <param name="weeks">窗口周数</param>
    /// <param name="firstDay">窗口第一格是哪一天（周一）</param>
    /// <param name="today">今天（最后那一列里，晚于它的格子不画）</param>
    public void SetData(IReadOnlyDictionary<DateTime, int> perDay, int weeks, DateTime firstDay, DateTime today)
    {
        _counts = perDay ?? new Dictionary<DateTime, int>();
        _weeks = Math.Max(1, weeks);
        _firstDay = firstDay.Date;
        _today = today.Date;
        Render();
    }

    // ========================================================
    // 画
    // ========================================================

    private void Render()
    {
        if (Board == null) return;

        double available = ActualWidth;
        if (double.IsNaN(available) || available < 60) available = 900;   // 还没布局完时的兜底宽度

        // 间隔跟着格宽走：格子小了就把缝也收窄，否则会显得"缝比格子还宽"
        double cell = ComputeCell(available);
        double gap = cell >= 11 ? 3 : (cell >= 8 ? 2 : 1);

        double gridW = _weeks * cell + (_weeks - 1) * gap;

        Board.Width = LeftGutter + gridW + 2;
        Board.Height = TopGutter + 7 * cell + 6 * gap + 2;

        Board.Children.Clear();

        DrawMonthLabels(cell, gap);

        for (int col = 0; col < _weeks; col++)
        {
            for (int row = 0; row < 7; row++)
            {
                var day = _firstDay.AddDays(col * 7 + row);

                // 左边那列星期几：只标 一 / 三 / 五 / 日（每格都标会糊成一团）
                if (col == 0 && (row % 2 == 0))
                    Board.Children.Add(MakeLabel(WeekdayLabels[row], 0,
                                                 TopGutter + row * (cell + gap) + cell / 2 - 6));

                if (day > _today) continue;   // 未来那几天不画（留着就是空的，看图的人自己明白）

                double x = LeftGutter + col * (cell + gap);
                double y = TopGutter + row * (cell + gap);

                Board.Children.Add(MakeCell(x, y, cell, day));
            }
        }
    }

    private double ComputeCell(double available)
    {
        // 先按"间隔 3"估一版；如果算出来格子偏小，间隔改小再算一次
        double cell = (available - LeftGutter - 2 - 3.0 * (_weeks - 1)) / _weeks;

        if (cell < 8)
            cell = (available - LeftGutter - 2 - 1.0 * (_weeks - 1)) / _weeks;

        if (cell > 22) cell = 22;      // 别让 13 周时格子大得像按钮
        if (cell < 3) cell = 3;
        return cell;
    }

    private Rectangle MakeCell(double x, double y, double cell, DateTime day)
    {
        int n = _counts.TryGetValue(day, out var c) ? c : 0;

        var rect = new Rectangle
        {
            Width = cell,
            Height = cell,
            RadiusX = Math.Min(2.5, cell / 4),
            RadiusY = Math.Min(2.5, cell / 4),
            Fill = n <= 0 ? FillEmpty : n == 1 ? FillOne : n == 2 ? FillTwo : FillMany,
            Stroke = n <= 0 ? StrokeEmpty : n == 1 ? StrokeOne : n == 2 ? StrokeTwo : StrokeMany,
            StrokeThickness = 1,
        };

        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);

        // 今天那一格：描一圈虚线（还没发就是空的 —— 空档就在眼皮底下）
        if (day == _today)
        {
            rect.Stroke = TodayRing;
            rect.StrokeThickness = 1.2;
            rect.StrokeDashArray = new DoubleCollection { 2, 1.6 };
        }

        rect.ToolTip = n <= 0
            ? $"{day:MM-dd} {WeekdayNames[(int)day.DayOfWeek]}　没发"
            : $"{day:MM-dd} {WeekdayNames[(int)day.DayOfWeek]}　{n} 支";

        rect.Cursor = Cursors.Arrow;
        return rect;
    }

    private static readonly string[] WeekdayNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    /// <summary>顶上标月份：只在"这一列的周一换了月"的地方标一个。</summary>
    private void DrawMonthLabels(double cell, double gap)
    {
        double lastX = -999;

        for (int col = 0; col < _weeks; col++)
        {
            var monday = _firstDay.AddDays(col * 7);
            var prev = col == 0 ? (DateTime?)null : _firstDay.AddDays((col - 1) * 7);

            bool newMonth = prev == null || prev.Value.Month != monday.Month;
            if (!newMonth) continue;

            double x = LeftGutter + col * (cell + gap);
            if (x - lastX < 26) continue;      // 挨太近就不标了，免得挤成一坨

            Board.Children.Add(MakeLabel(monday.Month + "月", x, 1));
            lastX = x;
        }
    }

    private static TextBlock MakeLabel(string text, double x, double y)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            Foreground = LabelBrush,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        return tb;
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
