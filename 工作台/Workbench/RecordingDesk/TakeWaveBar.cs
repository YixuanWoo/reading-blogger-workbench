// ============================================================
// TakeWaveBar.cs - 录音波形条（一条音频的"长相"）
// ============================================================
//
// 【为什么自己画，不用现成的 WaveformControl】
//   项目里那个 WaveformControl 是给时间轴用的（跟着滚动、跟播放线联动）。
//   录音台只需要一件很朴素的事：把一条 wav 的峰值画成一排柱子，
//   左边亮（已播）、右边暗（没播）、中间一条竖线（播放头）。
//   自己画 60 行，比把时间轴控件改造成两种用途干净得多。
//
// 【怎么用】
//   Peaks = RecorderEngine.PeaksOf(path, 260)   ← 0~1 的峰值
//   Progress = 0.0 ~ 1.0                        ← 播放/录音进度
//   ShowPlayhead = true                         ← 录音或试听时才画那根红线
// ============================================================

using System;
using System.Windows;
using System.Windows.Media;

namespace LiteCut.Workbench.RecordingDesk;

/// <summary>一排竖柱组成的波形（中间对称展开）。</summary>
public sealed class TakeWaveBar : FrameworkElement
{
    private static readonly Brush BarIdle = Freeze(Color.FromRgb(0x33, 0x3A, 0x44));
    private static readonly Brush BarLive = Freeze(Color.FromRgb(0x8A, 0x93, 0xA1));
    private static readonly Brush BarPlayed = Freeze(Color.FromRgb(0xC9, 0xD2, 0xDE));
    private static readonly Brush CenterLine = Freeze(Color.FromRgb(0x24, 0x2A, 0x33));
    private static readonly Brush Head = Freeze(Color.FromRgb(0xE0, 0x5A, 0x4E));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private float[] _peaks = Array.Empty<float>();

    /// <summary>每个柱的幅度（0~1），从左到右＝从开头到结尾。</summary>
    public float[] Peaks
    {
        get => _peaks;
        set { _peaks = value ?? Array.Empty<float>(); InvalidateVisual(); }
    }

    /// <summary>进度 0~1。1 = 整条都"亮"。</summary>
    public double Progress
    {
        get => _progress;
        set { _progress = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }
    private double _progress = 1;

    /// <summary>要不要画播放头（录音中 / 试听中）。</summary>
    public bool ShowPlayhead
    {
        get => _head;
        set { _head = value; InvalidateVisual(); }
    }
    private bool _head;

    /// <summary>没有数据时整条压暗（"还没录"的样子）。</summary>
    public bool Dim
    {
        get => _dim;
        set { _dim = value; InvalidateVisual(); }
    }
    private bool _dim = true;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        double mid = h / 2;

        // 中线：没有音频时至少让人看到一条"音轨"
        dc.DrawRectangle(CenterLine, null, new Rect(0, mid - 0.5, w, 1));

        var peaks = _peaks;
        if (peaks.Length == 0) return;

        const double gap = 1.2;
        double barW = Math.Max(1.0, (w - gap * peaks.Length) / peaks.Length);
        double half = h * 0.44;

        for (int i = 0; i < peaks.Length; i++)
        {
            double x = i * (barW + gap);
            if (x > w) break;

            double amp = Math.Max(0.012, peaks[i]);
            double bh = Math.Max(1.0, amp * half);

            bool played = (double)i / peaks.Length <= _progress;
            Brush brush = _dim ? BarIdle : (played ? BarPlayed : BarLive);

            dc.DrawRectangle(brush, null, new Rect(x, mid - bh, barW, bh * 2));
        }

        if (_head && _progress > 0)
        {
            double px = _progress * w;
            dc.DrawRectangle(Head, null, new Rect(Math.Max(0, px - 0.6), 0, 1.2, h));
        }
    }
}
