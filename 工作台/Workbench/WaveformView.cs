// ============================================================
// WaveformView.cs - 波形显示器（自己画，不用现成控件）
// ============================================================
//
// 【为什么不直接用 DrawingVisual / 现成图表库】
//   需求很特殊：要把"保留的"和"要剪掉的"用不同颜色画在同一条波形上，
//   还要能点哪儿跳哪儿、还要显示播放头。现成图表库做不到这些，
//   或者要引入几百 KB 的依赖。自己画反而是最省事的路。
//
// 【性能的关键：预计算峰值】
//   608 秒音频 @16kHz = 973 万个采样点。屏幕上最多 2000 个像素。
//   每次重绘都遍历 973 万点肯定卡死（一次 100ms 以上）。
//
//   所以：**解码后立刻算一次每像素的峰值，存起来；重绘只读那 2000 个数**。
//   这跟你的箱线图思想一样 —— 一个像素柱子里可能有 8000 个采样，
//   屏幕上只需要知道它们的最高点在哪，不需要知道每一个分别是多少。
//
// 【重绘成本】
//   算完之后只画 2000 个矩形，一次渲染 < 2ms，播放头可以 60fps 流畅跑。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiteCut.Core.Engine;

namespace LiteCut.Workbench;

/// <summary>
/// 波形显示控件。支持：
///   · 保留段 / 删除段 分色
///   · 播放头
///   · 点击定位（触发 SeekRequested）
///   · 每 30 秒一条时间刻度
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    // ---- 数据 ----
    private PcmData? _pcm;
    private List<(double Start, double End)> _keep = new();

    // ---- 峰值缓存：_peaks[i] = 第 i 个像素柱的最大绝对值 ----
    private float[] _peaks = Array.Empty<float>();
    private int _peakCount;
    private double _secPerPeak;

    // ---- 播放头 ----
    private double _playheadSec = -1;

    /// <summary>播放头位置（秒）。负数表示不显示。</summary>
    public double PlayheadSec
    {
        get => _playheadSec;
        set { _playheadSec = value; InvalidateVisual(); }
    }

    /// <summary>用户点击波形请求跳转到某个时间。</summary>
    public event EventHandler<double>? SeekRequested;

    // ---- 配色（深色主题）----
    private static readonly Color Back = Color.FromRgb(0x18, 0x1B, 0x20);
    private static readonly Color Grid = Color.FromRgb(0x28, 0x2D, 0x35);
    private static readonly Color KeepFill = Color.FromRgb(0x3B, 0x82, 0xF6);   // 蓝：保留
    private static readonly Color DropFill = Color.FromRgb(0xEF, 0x44, 0x44);   // 红：剪掉
    private static readonly Color Head = Color.FromRgb(0xFF, 0xFF, 0xFF);       // 白：播放头

    /// <summary>
    /// 背景色，拖动或自适应布局时需要。
    /// </summary>
    public WaveformView()
    {
        // 注意：FrameworkElement 不带 Background 属性（那是 Control/Panel 才有），
        // 背景色在 OnRender 里用 DrawRectangle 画。
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
    }

    // ========================================================
    // 数据入口
    // ========================================================

    /// <summary>
    /// 喂数据。keep 是"要保留的区间列表"，不在其中的一律画成红色。
    /// </summary>
    /// <param name="pcm">波形数据</param>
    /// <param name="keep">保留区间。传 null 表示全部保留（还没分析时）</param>
    public void SetData(PcmData? pcm, List<(double Start, double End)>? keep)
    {
        _pcm = pcm;
        _keep = keep ?? new List<(double, double)>();
        _peaks = Array.Empty<float>();
        _playheadSec = -1;
        RecomputePeaks();
        InvalidateVisual();
    }

    /// <summary>
    /// 只更新配色（用户在列表里改了 keep/drop），波形数据不用重算。
    /// </summary>
    public void SetKeepRanges(List<(double Start, double End)>? keep)
    {
        _keep = keep ?? new List<(double, double)>();
        InvalidateVisual();
    }

    // ========================================================
    // 大小变化时重算
    // ========================================================

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        RecomputePeaks();
        InvalidateVisual();
    }

    /// <summary>
    /// 把整段音频压成"每像素一个峰值"。这是整个控件能流畅的关键。
    /// </summary>
    private void RecomputePeaks()
    {
        if (_pcm == null || _pcm.Samples.Length == 0 || ActualWidth < 4)
        {
            _peaks = Array.Empty<float>();
            _peakCount = 0;
            return;
        }

        // 每个柱子占 2 像素：一像素画波形，留一像素间隔，
        // 视觉上比"一根挨一根"更像专业剪辑软件，也更好看出节拍。
        _peakCount = Math.Max(1, (int)(ActualWidth / 2));
        _peaks = new float[_peakCount];

        int total = _pcm.Samples.Length;
        double samplesPerPeak = (double)total / _peakCount;
        _secPerPeak = _pcm.DurationSec / _peakCount;

        //samplesPerPeak 可能是小数，用浮点累加边界避免整数除法带来的漂移
        for (int i = 0; i < _peakCount; i++)
        {
            int from = (int)(i * samplesPerPeak);
            int to = i == _peakCount - 1 ? total : (int)((i + 1) * samplesPerPeak);
            if (to <= from) to = Math.Min(total, from + 1);

            float mx = 0f;
            for (int k = from; k < to; k++)
            {
                float v = _pcm.Samples[k];
                if (v < 0) v = -v;
                if (v > mx) mx = v;
            }
            _peaks[i] = mx;
        }
    }

    // ========================================================
    // 绘制
    // ========================================================

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        // ---- 背景 ----
        dc.DrawRectangle(new SolidColorBrush(Back), null, new Rect(0, 0, w, h));

        if (_pcm == null || _peaks.Length == 0)
        {
            DrawHintText(dc, w, h, "还没有加载音频");
            return;
        }

        double mid = h / 2.0;
        double usableH = h * 0.86;

        // ---- 时间刻度：每 30 秒一条竖线 ----
        var gridPen = new Pen(new SolidColorBrush(Grid), 1);
        double totalSec = _pcm.DurationSec;
        for (double t = 0; t <= totalSec; t += 30)
        {
            double x = SecToX(t, w);
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        }
        // 中轴线
        dc.DrawLine(gridPen, new Point(0, mid), new Point(w, mid));

        // ---- 波形柱 ----
        // 逐段（而非逐柱）判断颜色：先把 keep 区间换算成像素范围，
        // 落在外面的一律红色。比每根柱子二分查找快得多。
        bool[]? isKeep = BuildKeepMask(_peakCount);

        for (int i = 0; i < _peakCount; i++)
        {
            float peak = _peaks[i];
            if (peak < 0.0005f) continue;   // 完全静音的地方不画，留白更清楚

            double barH = Math.Min(1.0, peak) * usableH;
            if (barH < 1) barH = 1;

            double x = i * 2.0;
            Color c = (isKeep == null || isKeep[i]) ? KeepFill : DropFill;
            // 用 0.75 的透明度让密集波形看起来不那么"实心"，更像专业软件
            c.A = 0xC8;

            dc.DrawRectangle(new SolidColorBrush(c), null,
                             new Rect(x, mid - barH / 2, 1.6, barH));
        }

        // ---- 图例 ----
        DrawLegend(dc, w);

        // ---- 播放头 ----
        if (_playheadSec >= 0)
        {
            double hx = SecToX(_playheadSec, w);
            dc.DrawLine(new Pen(new SolidColorBrush(Head), 1.5),
                        new Point(hx, 0), new Point(hx, h));
        }
    }

    /// <summary>
    /// 预先算出每个柱子属于 keep 还是 drop。
    /// O(柱子数 + 区间数)，比每个柱子二分查找（O(n log n)）简单且够快。
    /// </summary>
    private bool[]? BuildKeepMask(int count)
    {
        if (_keep.Count == 0) return null;   // 没给 keep 列表 = 全部当保留

        var mask = new bool[count];
        foreach (var (start, end) in _keep)
        {
            int i0 = Math.Max(0, (int)(start / _secPerPeak));
            int i1 = Math.Min(count - 1, (int)Math.Ceiling(end / _secPerPeak));
            for (int i = i0; i <= i1; i++) mask[i] = true;
        }
        return mask;
    }

    private void DrawHintText(DrawingContext dc, double w, double h, string text)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                                   FlowDirection.LeftToRight,
                                   new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal,
                                                FontWeights.Normal, FontStretches.Normal),
                                   14, new SolidColorBrush(Color.FromRgb(0x66, 0x6B, 0x74)), 1.0);
        dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
    }

    private void DrawLegend(DrawingContext dc, double w)
    {
        // 右上角画两个小色块 + 文字，说明蓝=保留、红=剪掉
        double x = w - 96, y = 8;
        dc.DrawRectangle(new SolidColorBrush(KeepFill), null, new Rect(x, y, 10, 10));
        DrawText(dc, "保留", x + 14, y - 2, 11, KeepFill);
        dc.DrawRectangle(new SolidColorBrush(DropFill), null, new Rect(x + 46, y, 10, 10));
        DrawText(dc, "剪掉", x + 60, y - 2, 11, DropFill);
    }

    private static void DrawText(DrawingContext dc, string s, double x, double y,
                                 double size, Color c)
    {
        var ft = new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
                                   FlowDirection.LeftToRight,
                                   new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal,
                                                FontWeights.Normal, FontStretches.Normal),
                                   size, new SolidColorBrush(c), 1.0);
        dc.DrawText(ft, new Point(x, y));
    }

    // ========================================================
    // 坐标换算 & 交互
    // ========================================================

    private double SecToX(double sec, double width) =>
        _pcm == null || _pcm.DurationSec <= 0 ? 0 : (sec / _pcm.DurationSec) * width;

    private double XToSec(double x, double width) =>
        _pcm == null || width <= 0 ? 0 : (x / width) * _pcm.DurationSec;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_pcm == null) return;
        double sec = XToSec(e.GetPosition(this).X, ActualWidth);
        SeekRequested?.Invoke(this, Math.Clamp(sec, 0, _pcm.DurationSec));
    }

    /// <summary>
    /// 拖动也能定位（按住左键划过）。
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_pcm == null) return;
        double sec = XToSec(e.GetPosition(this).X, ActualWidth);
        SeekRequested?.Invoke(this, Math.Clamp(sec, 0, _pcm.DurationSec));
    }
}
