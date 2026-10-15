// ============================================================
// FinalWaveformBar.cs - 成片波形条（第③步分镜页底部那条）
// ============================================================
//
// 【和精剪页那条波形有什么不同】
//   精剪页那条是**编辑用**的：一块一块，能拖、能裁、能选中。
//   这条是**对照用**的：整条成片一笔画到底，中间没有块的分隔 ——
//   它的用处是"我听着到哪一句了"，以及"点一下跳过去"。
//   所以这里不画块边界，也不响应任何编辑手势，只认"点/拖 = 定位"。
//
// 【为什么单独写，而不是复用 WaveformControl】
//   WaveformControl 的一切都建立在"块"上（命中测试、裁切、选中、拖块）。
//   要让它画出"没有块"的样子，就得给它传一个伪装成一块的项目 ——
//   那时它内部那套块逻辑全成了负担，还会在边界上冒出意料外的行为。
//   这条控件总共一百多行，画的东西反而更贴需求，所以单独写。
// ============================================================

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteCut.Workbench.Views;

public class FinalWaveformBar : FrameworkElement
{
    private float[] _peaks = Array.Empty<float>();
    private bool _dragging;

    /// <summary>成片总时长（秒）。</summary>
    public double TotalSec { get; set; }

    /// <summary>播放头位置（秒）。外部改完要自己 InvalidateVisual。</summary>
    public double PlayheadSec { get; set; }

    /// <summary>还没有波形数据时显示的字。</summary>
    public string Placeholder { get; set; } = "正在拼成片音频…";

    /// <summary>点/拖波形条时触发，参数是目标秒数。</summary>
    public event Action<double>? SeekRequested;

    public FinalWaveformBar()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    /// <summary>设置波形峰值（0~1）。没拿到数据时传空数组，控件会画占位文字。</summary>
    public void SetPeaks(float[] peaks, double totalSec)
    {
        _peaks = peaks ?? Array.Empty<float>();
        TotalSec = totalSec;
        InvalidateVisual();
    }

    /// <summary>移动播放头（会重画）。</summary>
    public void SetPlayhead(double sec)
    {
        PlayheadSec = sec;
        InvalidateVisual();
    }

    // ========================================================
    // 画
    // ========================================================

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var bg = (SolidColorBrush)TryFindResource("BgInput") ?? new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x30));
        var border = (SolidColorBrush)TryFindResource("Border") ?? new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38));
        var wave = (SolidColorBrush)TryFindResource("BrushWave") ?? new SolidColorBrush(Color.FromRgb(0x6B, 0xB3, 0xFF));
        var dim = (SolidColorBrush)TryFindResource("TextFaint") ?? new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E));
        var head = (SolidColorBrush)TryFindResource("BrushPlayhead") ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x66));

        dc.DrawRoundedRectangle(bg, new Pen(border, 1), new Rect(0.5, 0.5, w - 1, h - 1), 3, 3);

        // 没数据：写一行字，别让人以为是坏了
        if (_peaks.Length == 0)
        {
            var ft = new FormattedText(Placeholder, System.Globalization.CultureInfo.CurrentCulture,
                                       FlowDirection.LeftToRight,
                                       new Typeface("Microsoft YaHei"), 11, dim, 1.0);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            DrawPlayhead(dc, w, h, head);
            return;
        }

        // ---- 刻度：每 30 秒一条竖线 + 一个时间标签 ----
        // 成片 6 分钟也就十来条，密度正好；再密就成噪点了。
        double step = PickStep(TotalSec, w);
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, dim.Color.R, dim.Color.G, dim.Color.B)), 1);
        for (double t = 0; t <= TotalSec; t += step)
        {
            double x = SecToX(t, w);
            dc.DrawLine(tickPen, new Point(x, h - 12), new Point(x, h - 1));
            var ft = new FormattedText(Fmt(t), System.Globalization.CultureInfo.CurrentCulture,
                                       FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"),
                                       9.5, dim, 1.0);
            dc.DrawText(ft, new Point(Math.Min(x + 2, w - ft.Width - 1), h - 13));
        }

        // ---- 波形：一整条，中间对称上下 ----
        double mid = (h - 13) / 2;
        double halfH = mid - 2;
        double barW = w / _peaks.Length;
        var brush = new SolidColorBrush(wave.Color);

        // 一个 Path 一次画完，比每个柱子 DrawRectangle 快得多（柱子数 = 像素宽）
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < _peaks.Length; i++)
            {
                double x = i * barW;
                double amp = Math.Max(_peaks[i], 0f) * halfH;
                if (amp < 0.4) amp = 0.4;          // 静音段也留一丝，不画成断的
                ctx.BeginFigure(new Point(x, mid - amp), true, true);
                ctx.LineTo(new Point(x, mid + amp), false, false);
                ctx.LineTo(new Point(x + Math.Max(barW - 0.35, 0.6), mid + amp), false, false);
                ctx.LineTo(new Point(x + Math.Max(barW - 0.35, 0.6), mid - amp), false, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);

        DrawPlayhead(dc, w, h, head);
    }

    private void DrawPlayhead(DrawingContext dc, double w, double h, SolidColorBrush head)
    {
        double x = SecToX(PlayheadSec, w);
        dc.DrawLine(new Pen(head, 1.5), new Point(x, 0), new Point(x, h));

        // 顶部一个小三角，让人一眼看出红线在哪（细线在波形里容易看丢）
        var tri = new StreamGeometry();
        using (var ctx = tri.Open())
        {
            ctx.BeginFigure(new Point(x - 4, 0), true, true);
            ctx.LineTo(new Point(x + 4, 0), false, false);
            ctx.LineTo(new Point(x, 6), false, false);
        }
        tri.Freeze();
        dc.DrawGeometry(head, null, tri);
    }

    /// <summary>挑一个刻度间隔：目标是屏上 8~14 条，太密太疏都不好看。</summary>
    private static double PickStep(double totalSec, double widthPx)
    {
        if (totalSec <= 0) return 30;
        double target = totalSec / 11.0;                       // 想要大约 11 条
        double[] choices = { 5, 10, 15, 30, 60, 120, 300, 600 };
        foreach (double c in choices)
            if (c >= target) return c;
        return choices[^1];
    }

    private double SecToX(double sec, double w)
    {
        if (TotalSec <= 0) return 0;
        double x = sec / TotalSec * w;
        return x < 0 ? 0 : (x > w ? w : x);
    }

    private double XToSec(double x, double w) => w <= 0 ? 0 : Math.Clamp(x / w * TotalSec, 0, TotalSec);

    private static string Fmt(double sec)
    {
        int m = (int)(sec / 60);
        int s = (int)(sec - m * 60);
        return $"{m}:{s:D2}";
    }

    // ========================================================
    // 点/拖 = 定位
    // ========================================================

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (TotalSec <= 0) return;

        _dragging = true;
        CaptureMouse();
        Focus();
        SeekRequested?.Invoke(XToSec(e.GetPosition(this).X, ActualWidth));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || TotalSec <= 0) return;
        SeekRequested?.Invoke(XToSec(e.GetPosition(this).X, ActualWidth));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        // 拖到控件外面松手时收不到 MouseUp —— 这里兜一下，不然会一直"粘"着鼠标
        if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
    }
}
