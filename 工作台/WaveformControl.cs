// ============================================================
// WaveformControl.cs - 时间轴波形控件（块模型）
//
// 这个控件负责：
//   1. 画：把时间轴上的所有块画成矩形（带素材名、选中高亮）
//   2. 交互：
//      - 点块 = 选中
//      - 拖块中间 = 移动块位置
//      - 拖块左右边缘 = 裁切头尾
//      - 拖空白 = 框选多块
//      - 拖播放线 = 移动播放位置
//      - 滚轮 = 缩放
//
// 它不负责声音、不负责文件，只负责"显示 + 鼠标交互"。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiteCut.Core.Timeline;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using LiteCut.Workbench.Theming;

namespace LiteCut;

/// <summary>
/// 时间轴波形控件：继承 FrameworkElement，自绘块和播放线。
/// </summary>
public class WaveformControl : FrameworkElement
{
    // ---------- 数据 ----------
    private TimelineProject? _project;

    // ★ V1.18.0 主题切换：时间轴是纯代码画的（OnRender），XAML 资源帮不上忙，
    //   收到主题变更通知后 InvalidateVisual 重画一遍即可（OnRender 里每次现取主题笔）。
    private bool _themeHooked;

    private void HookTheme()
    {
        if (_themeHooked) return;
        _themeHooked = true;
        Loaded += (_, _) => Theme.Changed += OnThemeChanged;
        Unloaded += (_, _) => Theme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged() => InvalidateVisual();

    // ---------- 波形峰值缓存：文件路径 → (采样率, 峰值数组) ----------
    // 每个峰值代表一小段时间内的最大振幅，用来画波形
    private readonly Dictionary<string, (int sampleRate, float[] peaks)> _peakCache = new();

    // ---------- 视图状态 ----------
    private double _secPerPixel = 0.05; // 缩放：每像素多少秒（0.05 = 20像素/秒）
    private double _viewStartSec;       // 视口最左边是第几秒

    // ---------- 鼠标交互状态 ----------
    private enum DragMode { None, Playhead, ClipBody, ClipLeft, ClipRight, RubberBand }
    private DragMode _dragMode = DragMode.None;
    private TimelineClip? _dragClip;   // 正在拖的块
    private double _dragOffsetSec;      // 拖块时，鼠标按下位置和块起点的偏移
    private Point _pressPoint;          // 按下时的屏幕坐标
    private double _pressTimelineSec;   // 按下时的时间轴位置

    // ---------- 拖动整组（多块一起拖）----------
    //
    // 【为什么要单独记一份"原位"】
    //   每次鼠标移动都"从原位 + 总位移"重算，而不是"在上一次的结果上再加一点"。
    //   累加会因为浮点误差慢慢漂移，而且一旦被墙夹住就再也退不回来了。
    private readonly List<TimelineClip> _dragGroup = new();
    private readonly Dictionary<TimelineClip, double> _dragOriginStart = new();

    // ---------- 碰撞判定结果（拖动过程中实时更新，供绘制用）----------
    private Placement _placement;                 // 当前落点：合法 / 顶住 / 塞不下
    private bool _dragChanged;                    // 这次拖动是否真的改了内容（决定要不要记撤销）
    private TimelineClip? _blockedClip;           // 被顶住的那块（在它的边缘画红色渐变）
    private BlockedEdge _blockedEdge = BlockedEdge.None;

    // 鼠标按下时点的是哪一块 / 有没有按住 Ctrl。
    // 用途：松手时如果压根没拖动，就把选中集收成这一块（见 ApplyBodyClickRelease）。
    private TimelineClip? _pendingClickClip;
    private bool _pendingClickAdditive;

    /// <summary>被顶住的是哪一边（对应块被墙贴着的那条边）</summary>
    private enum BlockedEdge { None, Left, Right }

    /// <summary>块允许的最短时长（秒）—— 再短就没法用鼠标操作了</summary>
    private const double MinClipSec = 0.1;

    /// <summary>拖动时"顶住"提示的渐变宽度（像素）</summary>
    private const double BlockGlowPx = 34;

    // ---------- 裁切时的固定边界（按下鼠标那一刻算好，全程不变）----------
    //
    // 【为什么要提前定死】
    //   裁切会改这个块自己的源入点/源出点，而"源素材里的前一段/后一段"
    //   是按当前值算出来的。边裁边算会让边界自己变得，一拉就能把邻段的内容抢过来。
    private double _trimOrigTimelineStart;   // 裁切开始前的块起点
    private double _trimOrigSourceStart;     // 裁切开始前的源入点
    private double _trimOrigSourceEnd;       // 裁切开始前的源出点
    private double _trimSrcLow;              // 被拖的那条边在"源素材坐标"里的下限
    private double _trimSrcHigh;             // 上限

    // ---------- 对外事件 ----------
    /// <summary>播放位置变化（MainWindow 据此让音频引擎跳转）</summary>
    public event EventHandler<PlaybackPositionEventArgs>? PlaybackPositionChanged;

    /// <summary>选中变化（MainWindow 据此刷新按钮状态）</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>块位置/裁切变化（MainWindow 据此触发撤销记录）</summary>
    public event EventHandler? ClipChanged;

    /// <summary>
    /// 拖动落点被拒绝（空档塞不下）—— 整组已弹回原位。
    /// 界面据此提示一句"这里放不下"。
    /// </summary>
    public event EventHandler? DragRejected;

    /// <summary>视口变化（MainWindow 据此更新滚动条位置）</summary>
    public event EventHandler? ViewportChanged;

    // ============================================================
    // 对外方法
    // ============================================================

    /// <summary>绑定时间轴项目</summary>
    public void SetProject(TimelineProject project)
    {
        _project = project;
        _project.Changed += (s, e) => InvalidateVisual();
        _project.SelectionChanged += (s, e) =>
        {
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        InvalidateVisual();
    }

    /// <summary>轻量刷新</summary>
    public void Refresh() => InvalidateVisual();

    /// <summary>外部通知播放位置变化</summary>
    public void NotifyPlaybackPosition(double sec)
    {
        if (_project != null)
        {
            _project.PlayheadSec = sec;
            InvalidateVisual();
        }
    }

    /// <summary>公开：X 坐标 → 时间轴秒数</summary>
    public double XToSecPublic(double x) => XToSec(x);

    /// <summary>公开：获取当前视口信息（MainWindow 用来更新滚动条）</summary>
    public (double viewStart, double secPerPixel, double viewWidth) GetViewport()
    {
        return (_viewStartSec, _secPerPixel, ActualWidth);
    }

    /// <summary>公开：设置视口起点（滚动条拖动时用）</summary>
    public void SetViewStart(double sec)
    {
        _viewStartSec = Math.Max(0, sec);
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 公开：一次性设置视口起点和缩放（还原项目存档时用）。
    /// 不用 FitToContent —— 那样会把用户上次的缩放级别冲掉。
    /// </summary>
    public void SetViewport(double viewStartSec, double secPerPixel)
    {
        _secPerPixel = Math.Clamp(secPerPixel <= 0 ? 0.05 : secPerPixel, 0.01, 2.0);
        _viewStartSec = Math.Max(0, viewStartSec);
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 视图跟随播放线：播放线跑出可视范围时，滚动视图把它拉回屏幕中间。
    /// 只在"播放中"由 MainWindow 在播放位置变化时调用；拖动播放线不触发，
    /// 避免未播放时视图跟着红线乱动。
    /// </summary>
    public void FollowPlayhead()
    {
        if (_project == null) return;
        double w = ActualWidth;
        if (w <= 0) return;

        double playheadX = SecToX(_project.PlayheadSec);
        if (playheadX < 0 || playheadX > w)
        {
            // 把播放线移到屏幕中间
            _viewStartSec = _project.PlayheadSec - w * _secPerPixel / 2.0;
            _viewStartSec = Math.Max(0, _viewStartSec);
            ViewportChanged?.Invoke(this, EventArgs.Empty);  // 通知更新滚动条
        }
        InvalidateVisual();
    }

    /// <summary>自动缩放到合适级别（让所有素材刚好在屏幕里显示）</summary>
    public void FitToContent()
    {
        if (_project == null || _project.Clips.Count == 0 || ActualWidth <= 0) return;
        double totalDuration = _project.TotalDurationSec;
        if (totalDuration <= 0) return;
        // 留 10% 边距
        _secPerPixel = totalDuration / (ActualWidth * 0.9);
        _secPerPixel = Math.Max(0.01, Math.Min(2.0, _secPerPixel));
        _viewStartSec = _project.StartSec;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    // 坐标换算
    // ============================================================

    /// <summary>时间轴秒数 → 屏幕 X</summary>
    private double SecToX(double sec)
    {
        return (sec - _viewStartSec) / _secPerPixel;
    }

    /// <summary>屏幕 X → 时间轴秒数</summary>
    private double XToSec(double x)
    {
        return _viewStartSec + x * _secPerPixel;
    }

    // ============================================================
    // 布局：占满剩余空间
    // ============================================================

    protected override Size MeasureOverride(Size availableSize)
    {
        return availableSize;
    }

    // ============================================================
    // 绘制
    // ============================================================

    protected override void OnRender(DrawingContext dc)
    {
        HookTheme();

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 1. 背景
        dc.DrawRectangle(GetBrush("BrushBackground", Theme.ColorOf("BrushBackground")),
                         null, new Rect(0, 0, w, h));

        if (_project == null)
        {
            // 没项目：画个提示
            var font = new Typeface("Segoe UI");
            var textBrush = GetBrush("BrushText", Theme.ColorOf("BrushText"));
            var ft = new FormattedText("从左侧素材库拖素材到这里开始剪辑",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, font, 14, textBrush, 1.0);
            dc.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
            return;
        }

        // 2. 画所有块
        double rulerHeight = 20; // 顶部时间刻度高度
        double blockTop = rulerHeight + 4;
        double blockHeight = h - blockTop - 8;

        foreach (var clip in _project.Clips)
        {
            double x1 = SecToX(clip.TimelineStartSec);
            double x2 = SecToX(clip.TimelineEndSec);

            // 完全在视野外就跳过
            if (x2 < 0 || x1 > w) continue;

            // 块背景色：正常=蓝色 / 选中=金色 / 拖动落点无效=警告红
            bool dropInvalid = _placement.State == PlacementState.TooNarrow
                               && _dragGroup.Contains(clip);
            // ★ V1.18.0：块颜色走主题（浅色主题下这三种块色都压暗过一档，白字仍看得清）
            Color blockColor = dropInvalid
                ? Theme.ColorOf("WaveBlockBad")
                : clip.IsSelected
                    ? Theme.ColorOf("WaveBlockSel")
                    : Theme.ColorOf("WaveBlockNormal");
            var blockBrush = new SolidColorBrush(blockColor);
            var borderPen = new Pen(Theme.Brush("WaveBlockBorder"), 1.5);

            // 画块矩形
            double left = Math.Max(0, x1);
            double right = Math.Min(w, x2);
            dc.DrawRectangle(blockBrush, borderPen, new Rect(left, blockTop, right - left, blockHeight));

            // 在块里面画波形
            DrawClipWaveform(dc, clip, left, right, blockTop, blockHeight);

            // 块上的素材名标签。
            //
            // 【为什么不是每个块都画】时间轴上短块很多（用户项目 184 块里 40 块短于 1 秒），
            // 缩放级别一低一块只有几个像素宽，而名字文字有 80 像素长 —— 十几个块的
            // 白字叠在同一段区域，看起来就是一道莫名其妙的"白色涂痕"（2026-09-29 用户截图报的）。
            // 所以两条规矩：
            //   ① 块宽放不下名字（< 48px）就不画，除非这块被选中（选中时要能认出它是谁）；
            //   ② 文字永远裁剪在本块矩形里，再宽也不许溢出压到邻居头上。
            if (right - left >= 48 || clip.IsSelected)
            {
                var font = new Typeface("Segoe UI");
                var labelBrush = Theme.Brush("WaveBlockLabel");
                string label = clip.SourceName;
                if (label.Length > 20) label = label.Substring(0, 18) + "..";
                var ft = new FormattedText(label,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, font, 11, labelBrush, 1.0);

                dc.PushClip(new RectangleGeometry(new Rect(left, blockTop, right - left, blockHeight)));
                dc.DrawText(ft, new Point(left + 4, blockTop + 3));
                dc.Pop();
            }

            // 「顶住」提示：贴着墙的那条边画一层红色渐变 —— 一眼看出"这儿到头了"。
            // 只在被顶的那条边真的落在可视范围内时才画，避免块拖出屏幕后
            // 渐变被画在视口边缘上，看着像是"屏幕边到头了"。
            if (ReferenceEquals(clip, _blockedClip) && _blockedEdge != BlockedEdge.None)
            {
                bool atRight = _blockedEdge == BlockedEdge.Right;
                double edgeX = atRight ? x2 : x1;
                if (edgeX >= 0 && edgeX <= w)
                    DrawBlockedEdgeGlow(dc, left, right, blockTop, blockHeight, atRight);
            }
        }

        // 3. 播放红线 + 顶部手柄
        // 注意：这里不再自动滚动视图。视图跟随播放线由 FollowPlayhead() 负责，
        // 且只在"播放中"由 MainWindow 调用——未播放时红线跑出屏幕就在屏幕外，
        // 不会强行拽动视图。
        double playheadX = SecToX(_project.PlayheadSec);

        {
            var playPen = new Pen(GetBrush("BrushPlayhead", Theme.ColorOf("BrushPlayhead")), 1.5);
            dc.DrawLine(playPen, new Point(playheadX, 0), new Point(playheadX, h));

            // 顶部小矩形手柄
            var handleBrush = GetBrush("BrushPlayhead", Theme.ColorOf("BrushPlayhead"));
            dc.DrawRectangle(handleBrush, null, new Rect(playheadX - 3, 0, 6, 8));
        }

        // 4. 时间刻度
        DrawTimeRuler(dc, w, rulerHeight);
    }

    private void DrawTimeRuler(DrawingContext dc, double w, double rulerHeight)
    {
        double secPerPixel = _secPerPixel;

        // 选合适的刻度间隔
        double[] candidates = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
        double stepSec = 300;
        foreach (double c in candidates)
        {
            if (c * secPerPixel >= 70) { stepSec = c; break; }
        }

        var textBrush = GetBrush("BrushText", Theme.ColorOf("BrushText"));
        var linePen = new Pen(GetBrush("BrushBorder", Theme.ColorOf("BrushBorder")), 1);
        var font = new Typeface("Segoe UI");

        double startSec = Math.Floor(_viewStartSec / stepSec) * stepSec;
        double endSec = _viewStartSec + w * secPerPixel + stepSec;

        for (double s = startSec; s <= endSec; s += stepSec)
        {
            double x = SecToX(s);
            if (x < 0 || x > w) continue;

            dc.DrawLine(linePen, new Point(x, rulerHeight - 4), new Point(x, rulerHeight));
            string label = FormatTick(s);
            var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                                       FlowDirection.LeftToRight, font, 10, textBrush, 1.0);
            dc.DrawText(ft, new Point(x + 3, 1));
        }
    }

    private static string FormatTick(double totalSec)
    {
        int m = (int)(totalSec / 60);
        int s = (int)(totalSec % 60);
        return $"{m:00}:{s:00}";
    }

    /// <summary>
    /// 在块"被顶住"的那条边上画一层红色渐变 + 一道亮边。
    /// 灵感来自达芬奇：拖到头的那个方向边缘会泛红，不用看数字也知道到边界了。
    /// </summary>
    private static void DrawBlockedEdgeGlow(DrawingContext dc, double left, double right,
                                            double top, double height, bool atRight)
    {
        double span = right - left;
        if (span < 2 || height <= 0) return;

        double glow = Math.Min(BlockGlowPx, span * 0.6);
        if (glow < 2) return;

        var fade = Color.FromArgb(0, 0xE5, 0x48, 0x4D);    // 完全透明
        var hot = Color.FromArgb(235, 0xE5, 0x48, 0x4D);   // 亮红

        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(atRight ? 0 : 1, 0.5),
            EndPoint = new Point(atRight ? 1 : 0, 0.5),
        };
        grad.GradientStops.Add(new GradientStop(fade, 0.0));
        grad.GradientStops.Add(new GradientStop(hot, 1.0));

        double x = atRight ? right - glow : left;
        dc.DrawRectangle(grad, null, new Rect(x, top, glow, height));

        // 最外侧那道实边，让"到头"的界线更硬
        var bar = new SolidColorBrush(hot);
        dc.DrawRectangle(bar, null, atRight
            ? new Rect(right - 2, top, 2, height)
            : new Rect(left, top, 2, height));
    }

    // ============================================================
    // 拖动整组 / 碰撞求解
    // ============================================================
    //
    // 交互规则（和用户确认过的口径）：
    //   ① 撞到别的块 → 顶住；鼠标继续越过那块的另一侧边缘 → 直接跳到鼠标位置
    //   ② 落点合法 → 完全跟随鼠标，拖到哪显示到哪，松手就放那儿
    //   ③ 空档装不下这一整组 → 整组变警告红，松手弹回原位
    //   ④ 片尾之后是无限长空档 → 随便放
    //   ⑤ 裁切（拉长/缩短）同样不许盖住别的块
    //
    // 真正的几何计算在 LiteCut.Core 的 TimelineCollision 里（纯逻辑、可独立测试），
    // 这里只负责把界面数据翻译成区间、把结果写回块。

    /// <summary>把块翻译成碰撞模块用的区间</summary>
    private static TimeRange RangeOf(TimelineClip c) => new(c.TimelineStartSec, c.TimelineEndSec);

    /// <summary>除开指定这些块之外，时间轴上其他块的占用区间</summary>
    private List<TimeRange> Obstacles(IEnumerable<TimelineClip> exclude)
    {
        var set = new HashSet<TimelineClip>(exclude);
        var list = new List<TimeRange>();
        if (_project == null) return list;

        foreach (var c in _project.Clips)
        {
            if (set.Contains(c)) continue;
            list.Add(RangeOf(c));
        }
        return list;
    }

    /// <summary>
    /// 按下鼠标时确定"这次要拖的是哪一组"，并把整组原位记下来。
    ///
    /// 【这里就是"多选拖不动"的根因修复点】
    ///   旧逻辑：只要没按 Ctrl，点中一个"已经处于选中状态"的块，就先把选中集
    ///   清成只剩它一个 —— 于是你框选了三块，一按下去选中集就塌成一块，
    ///   拖的自然也只有鼠标下那一块。
    ///   新逻辑：点中"已在选中集合里"的块 → 保持整组不变，整组一起拖。
    /// </summary>
    private void BeginDragGroup()
    {
        _dragGroup.Clear();
        _dragOriginStart.Clear();
        _dragChanged = false;
        _blockedClip = null;
        _blockedEdge = BlockedEdge.None;
        _placement = default;

        if (_project == null) return;

        foreach (var c in _project.Clips)
        {
            if (!c.IsSelected) continue;
            _dragGroup.Add(c);
            _dragOriginStart[c] = c.TimelineStartSec;
        }

        // 兜底：万一某块既没被选中又没进组（不该发生）
        if (_dragGroup.Count == 0 && _dragClip != null)
        {
            _dragGroup.Add(_dragClip);
            _dragOriginStart[_dragClip] = _dragClip.TimelineStartSec;
        }
    }

    /// <summary>整组里"最靠某一边"的那块（画顶住渐变的落点）</summary>
    private TimelineClip? EdgeClipOfGroup(bool rightMost)
    {
        TimelineClip? best = null;
        foreach (var c in _dragGroup)
        {
            if (best == null)
            {
                best = c;
                continue;
            }
            if (rightMost ? c.TimelineEndSec > best.TimelineEndSec
                          : c.TimelineStartSec < best.TimelineStartSec)
                best = c;
        }
        return best;
    }

    /// <summary>把整组拖到鼠标指向的位置（每次鼠标移动都从原位重算）</summary>
    private void DragGroupTo(double mouseSec)
    {
        if (_project == null || _dragGroup.Count == 0) return;

        // 整组当前的总跨度（含组内空隙 —— 一组块是刚性的，内部间距不会被挤扁）
        double gStart = double.MaxValue, gEnd = double.MinValue;
        foreach (var c in _dragGroup)
        {
            double s = _dragOriginStart[c];
            gStart = Math.Min(gStart, s);
            gEnd = Math.Max(gEnd, s + c.SourceDurationSec);
        }

        var moving = new TimeRange(gStart, gEnd);
        double desired = mouseSec - _pressTimelineSec;
        var obstacles = Obstacles(_dragGroup);

        // 历史工程里可能已经存着重叠的块。这种情况下不做空档求解 ——
        // 否则一按下去整组会被"吸"到某个离得很远的空档里，观感像块飞了。
        bool legacyOverlap = obstacles.Any(o =>
            o.Start < moving.End - TimelineCollision.Eps &&
            o.End > moving.Start + TimelineCollision.Eps);

        Placement p;
        if (legacyOverlap)
        {
            double ns = Math.Max(0, moving.Start + desired);
            p = new Placement { Delta = ns - moving.Start, State = PlacementState.Free };
        }
        else
        {
            p = TimelineCollision.ResolveMove(moving, obstacles, desired);
        }

        foreach (var c in _dragGroup)
            c.TimelineStartSec = _dragOriginStart[c] + p.Delta;

        _placement = p;
        _dragChanged = Math.Abs(p.Delta) > 1e-9;

        // 顶住谁了 → 记下来给绘制用
        _blockedClip = null;
        _blockedEdge = BlockedEdge.None;
        if (p.State == PlacementState.Blocked)
        {
            if (p.TouchedRight) { _blockedEdge = BlockedEdge.Right; _blockedClip = EdgeClipOfGroup(rightMost: true); }
            else if (p.TouchedLeft) { _blockedEdge = BlockedEdge.Left; _blockedClip = EdgeClipOfGroup(rightMost: false); }
        }

        InvalidateVisual();
    }

    /// <summary>
    /// 拖块左右边缘 = 裁切。除了"源素材本身有多长"，
    /// 还多一道限制：不许盖到时间轴上别的块。
    /// </summary>
    private void TrimTo(double mouseSec, bool leftEdge)
    {
        if (_project == null || _dragClip == null) return;

        var clip = _dragClip;
        double origLen = _trimOrigSourceEnd - _trimOrigSourceStart;
        if (origLen <= 0) return;

        // 鼠标指到时间轴哪个位置，这条边就该到哪个位置 —— 严格跟手。
        //
        // 【这里曾经有个真 bug】右边缘的"时间轴 → 源素材"映射用错了基准
        //   （写成了 源出点 + (鼠标位置 − 块起点)），换算回来的目标位置比
        //   鼠标位置整整多出一整个块长。于是刚按住右边缘一动，块就"自己延长"，
        //   一下顶到右边邻居的左边缘（触发"顶住"红光），根本没法用。
        //
        // 正确映射只有一条（块内内容等长一一对应，裁切不改播放速度）：
        //   时间轴位置 ↔ 源位置：源位置 = 源起点 + (时间轴位置 − 块起点)
        // 左右两条边都用它，右边缘换算回来恰好等于鼠标位置。
        double newSrcEdge = _trimOrigSourceStart + (mouseSec - _trimOrigTimelineStart);

        // 第一道：源素材边界（含剪刀切出来的相邻段、素材总时长）
        double wantedSrcEdge = newSrcEdge;
        if (newSrcEdge < _trimSrcLow) newSrcEdge = _trimSrcLow;
        if (newSrcEdge > _trimSrcHigh) newSrcEdge = _trimSrcHigh;
        bool srcBlocked = Math.Abs(newSrcEdge - wantedSrcEdge) > 1e-9;

        // 换算回时间轴坐标（同一条映射反着用）
        double desiredSec = _trimOrigTimelineStart + (newSrcEdge - _trimOrigSourceStart);

        // 第二道：不许盖住别的块
        var originalSpan = new TimeRange(_trimOrigTimelineStart,
                                         _trimOrigTimelineStart + origLen);
        var obstacles = Obstacles(new[] { clip });

        TrimPlacement t = leftEdge
            ? TimelineCollision.ResolveTrimLeft(originalSpan, obstacles, desiredSec, MinClipSec)
            : TimelineCollision.ResolveTrimRight(originalSpan, obstacles, desiredSec, MinClipSec);

        if (leftEdge)
        {
            clip.TimelineStartSec = t.Value;
            clip.SourceStartSec = _trimOrigSourceStart + (t.Value - _trimOrigTimelineStart);
        }
        else
        {
            // 右边缘是计算属性（起点 + 源长度），所以只改源出点
            clip.SourceEndSec = _trimOrigSourceStart + (t.Value - _trimOrigTimelineStart);
        }

        _dragChanged = Math.Abs(t.Value -
            (leftEdge ? _trimOrigTimelineStart : _trimOrigTimelineStart + origLen)) > 1e-9;

        // 不管是被邻居顶住，还是已经顶到源素材的头/尾，都算"到头了" → 画红色渐变
        bool blocked = srcBlocked || t.Blocked;
        _blockedClip = blocked ? clip : null;
        _blockedEdge = blocked
            ? (leftEdge ? BlockedEdge.Left : BlockedEdge.Right)
            : BlockedEdge.None;
        _placement = default;

        InvalidateVisual();
    }

    /// <summary>按下鼠标时把裁切的固定边界全部算好（见字段注释）</summary>
    private void BeginTrim(bool leftEdge)
    {
        _dragGroup.Clear();
        _dragOriginStart.Clear();
        _dragChanged = false;
        _blockedClip = null;
        _blockedEdge = BlockedEdge.None;
        _placement = default;

        if (_project == null || _dragClip == null) return;

        var clip = _dragClip;
        _trimOrigTimelineStart = clip.TimelineStartSec;
        _trimOrigSourceStart = clip.SourceStartSec;
        _trimOrigSourceEnd = clip.SourceEndSec;

        if (leftEdge)
        {
            // 源入点下限 = 0 或"同一素材里前一段的源出点"（别把前段内容抢过来）
            double low = 0;
            var prev = _project.PrevClipInSource(clip);
            if (prev != null && prev.SourceEndSec > low) low = prev.SourceEndSec;
            _trimSrcLow = low;
            _trimSrcHigh = _trimOrigSourceEnd - MinClipSec;
        }
        else
        {
            // 源出点上限 = 素材总时长 或"同一素材里后一段的源入点"
            double high = clip.SourceTotalSec > 0 ? clip.SourceTotalSec : double.MaxValue;
            var next = _project.NextClipInSource(clip);
            if (next != null && next.SourceStartSec < high) high = next.SourceStartSec;
            _trimSrcLow = _trimOrigSourceStart + MinClipSec;
            _trimSrcHigh = high;
        }

        // 边界本身互相矛盾（比如已经贴到极限）时兜个底，别让上下限反过来
        if (_trimSrcHigh < _trimSrcLow) _trimSrcHigh = _trimSrcLow;
    }

    // ============================================================
    // 鼠标交互
    // ============================================================

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_project == null) return;

        Point pos = e.GetPosition(this);
        double sec = XToSec(pos.X);
        _pressPoint = pos;
        _pressTimelineSec = sec;
        _pendingClickClip = null;
        _pendingClickAdditive = false;

        double edgeTolSec = _secPerPixel * 8; // 边缘容差：8 像素

        // 1. 先判断是不是点在播放线附近
        if (Math.Abs(sec - _project.PlayheadSec) < edgeTolSec)
        {
            _dragMode = DragMode.Playhead;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 2. 命中测试：找块、判断是边缘还是内部
        var (clip, edge) = _project.HitTest(sec, edgeTolSec);
        if (clip != null)
        {
            // 防误判（修复）：视图缩小时，edgeTolSec（固定 8 像素换算成秒）会
            // 盖住大半个块——尤其剪刀切出来的短块，点"块中间"也会命中左/右边缘，
            // 拖块想移动却变成了裁切，块会跳走（但声音数据是对的）。
            // 限制：只有点离边缘不足"块宽 25%"才算边缘，其余按"拖块中间"处理。
            double clipWidth = clip.TimelineEndSec - clip.TimelineStartSec;
            if (clipWidth > 0)
            {
                double maxEdge = clipWidth * 0.25;
                if (edge == "left" && sec - clip.TimelineStartSec >= maxEdge) edge = "body";
                if (edge == "right" && clip.TimelineEndSec - sec >= maxEdge) edge = "body";
            }

            bool additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (edge == "left" || edge == "right")
            {
                _dragMode = edge == "left" ? DragMode.ClipLeft : DragMode.ClipRight;
                _dragClip = clip;

                // 裁切只作用于这一块，所以先把选中集收成它自己
                if (!additive || !clip.IsSelected)
                {
                    _project.ClearSelection();
                    clip.IsSelected = true;
                }
                _project.NotifySelectionChanged();
                BeginTrim(leftEdge: edge == "left");
            }
            else // body
            {
                _dragMode = DragMode.ClipBody;
                _dragClip = clip;
                _dragOffsetSec = sec - clip.TimelineStartSec;

                // 【多选拖动】选中规则在 TimelineProject 里（有独立测试盯着）：
                // 点中"已经在选中集合里"的块不会清空选中集，所以整组能一起拖。
                _project.ApplyBodyClickSelection(clip, additive);
                _pendingClickClip = clip;
                _pendingClickAdditive = additive;
                BeginDragGroup();
            }

            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 3. 点空白：开始框选（如果没按 Ctrl，先清选中）
        _dragMode = DragMode.RubberBand;
        bool clearFirst = (Keyboard.Modifiers & ModifierKeys.Control) == 0;
        if (clearFirst) _project.ClearSelection();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_project == null) return;
        if (_dragMode == DragMode.None) return;

        Point pos = e.GetPosition(this);
        double sec = XToSec(pos.X);

        switch (_dragMode)
        {
            case DragMode.Playhead:
                _project.PlayheadSec = Math.Max(0, sec);
                InvalidateVisual();
                PlaybackPositionChanged?.Invoke(this, new PlaybackPositionEventArgs
                {
                    PositionSec = sec
                });
                break;

            case DragMode.ClipBody:
                DragGroupTo(sec);
                break;

            case DragMode.ClipLeft:
                TrimTo(sec, leftEdge: true);
                break;

            case DragMode.ClipRight:
                TrimTo(sec, leftEdge: false);
                break;

            case DragMode.RubberBand:
                double start = Math.Min(_pressTimelineSec, sec);
                double end = Math.Max(_pressTimelineSec, sec);
                _project.SelectClipsInRange(start, end, additive: false);
                break;
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragMode != DragMode.None)
        {
            bool isEdit = _dragMode is DragMode.ClipBody or DragMode.ClipLeft or DragMode.ClipRight;

            // 落点无效（空档塞不下这一整组）→ 松手整组弹回原位，什么也不改。
            // 这就是"显示警告红、松手放不下"的实现。
            if (_dragMode == DragMode.ClipBody && _placement.State == PlacementState.TooNarrow)
            {
                foreach (var c in _dragGroup)
                    c.TimelineStartSec = _dragOriginStart[c];
                _dragChanged = false;
                DragRejected?.Invoke(this, EventArgs.Empty);
            }

            // 点一下没拖动 → 把选中集收成点中的那一块（框选/Ctrl+A 之后能缩回来）
            if (_dragMode == DragMode.ClipBody && _pendingClickClip != null)
                _project?.ApplyBodyClickRelease(_pendingClickClip, _pendingClickAdditive, _dragChanged);

            // 只有真的改动了才通知（否则每次点一下都塞一条空快照进撤销栈，
            // 用户按撤销会觉得"没反应"）
            if (isEdit && _dragChanged)
                ClipChanged?.Invoke(this, EventArgs.Empty);

            _dragMode = DragMode.None;
            _dragClip = null;
            _dragGroup.Clear();
            _dragOriginStart.Clear();
            _dragChanged = false;
            _blockedClip = null;
            _blockedEdge = BlockedEdge.None;
            _placement = default;
            _pendingClickClip = null;
            _pendingClickAdditive = false;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_project == null) return;

        double mouseX = e.GetPosition(this).X;
        double anchorSec = XToSec(mouseX);

        double factor = e.Delta > 0 ? 0.8 : 1.25;
        double newSecPerPixel = _secPerPixel * factor;

        // 缩放范围：最小 0.01秒/像素（放大），最大 2秒/像素（缩小）
        newSecPerPixel = Math.Max(0.01, Math.Min(2.0, newSecPerPixel));

        // 保持鼠标位置不动
        double newViewStart = anchorSec - mouseX * newSecPerPixel;
        // 钳制在可滚动范围内（0 ~ 总时长-可视宽度）：
        // 防止缩放后视口跑到滚动条范围之外，造成视口与滚动条/点击命中不同步（漂移/跳动）
        double totalDuration = _project.TotalDurationSec;
        double maxViewStart = Math.Max(0, totalDuration - ActualWidth * newSecPerPixel);
        _viewStartSec = Math.Max(0, Math.Min(newViewStart, maxViewStart));
        _secPerPixel = newSecPerPixel;

        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);  // 缩放后通知更新滚动条
        e.Handled = true;
    }

    // ============================================================
    // 画块里的波形
    // ============================================================

    /// <summary>
    /// 在块的矩形区域里画波形。
    /// 从缓存取这个素材的峰值数据，按块的时间范围画。
    /// </summary>
    /// <summary>
    /// 在块的矩形区域里画波形（只画可视范围内的部分）。
    /// </summary>
    private void DrawClipWaveform(DrawingContext dc, TimelineClip clip,
        double left, double right, double blockTop, double blockHeight)
    {
        if (!_peakCache.TryGetValue(clip.SourcePath, out var peakData))
        {
            peakData = LoadPeaks(clip.SourcePath);
            if (peakData.peaks == null || peakData.peaks.Length == 0) return;
            _peakCache[clip.SourcePath] = peakData;
        }

        int sampleRate = peakData.sampleRate;
        float[] peaks = peakData.peaks;
        int peaksPerSecond = sampleRate / 100;

        double midY = blockTop + blockHeight / 2.0;
        double amp = (blockHeight - 20) / 2.0;

        // 可视范围对应的时间轴位置
        double viewStart = _viewStartSec;
        double viewEnd = _viewStartSec + ActualWidth * _secPerPixel;

        // 块在时间轴上的位置
        double clipStart = clip.TimelineStartSec;
        double clipEnd = clip.TimelineEndSec;

        // 块的可见部分（时间轴坐标）
        double visibleStart = Math.Max(clipStart, viewStart);
        double visibleEnd = Math.Min(clipEnd, viewEnd);
        double visibleDuration = visibleEnd - visibleStart;
        if (visibleDuration <= 0) return;

        // 可见部分对应的源素材位置
        double srcStart = clip.SourceStartSec + (visibleStart - clipStart);
        double srcEnd = clip.SourceEndSec - (clipEnd - visibleEnd);

        // 可见部分在屏幕上的像素位置
        double screenStartX = SecToX(visibleStart);
        double screenEndX = SecToX(visibleEnd);
        double screenWidth = screenEndX - screenStartX;
        if (screenWidth < 1) return;

        var wavePen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1);

        // 逐像素画波形
        for (double x = 0; x < screenWidth; x += 1)
        {
            double t = x / screenWidth;  // 0.0 到 1.0
            double srcSec = srcStart + t * (srcEnd - srcStart);
            int peakIdx = (int)(srcSec * peaksPerSecond);
            if (peakIdx < 0 || peakIdx >= peaks.Length) continue;

            float peak = peaks[peakIdx];
            // 音量：波纹跟随这块的音量设置（dB → 线性倍数，0 dB = ×1.0）
            peak *= (float)Utils.DbToGain(clip.VolumeDb);
            double y1 = midY - peak * amp;
            double y2 = midY + peak * amp;

            double screenX = screenStartX + x;
            dc.DrawLine(wavePen, new Point(screenX, y1), new Point(screenX, y2));
        }
    }
    private static (int sampleRate, float[] peaks) LoadPeaks(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider source = reader;
            if (reader.WaveFormat.Channels > 1)
            {
                source = new StereoToMonoSampleProvider(reader);
            }

            int sampleRate = reader.WaveFormat.SampleRate;
            int samplesPerPeak = 100;
            var peaks = new List<float>();

            var buffer = new float[4096];
            int sinceLastPeak = 0;
            float maxAbs = 0;

            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    float abs = Math.Abs(buffer[i]);
                    if (abs > maxAbs) maxAbs = abs;
                    sinceLastPeak++;
                    if (sinceLastPeak >= samplesPerPeak)
                    {
                        peaks.Add(maxAbs);
                        maxAbs = 0;
                        sinceLastPeak = 0;
                    }
                }
            }
            if (sinceLastPeak > 0) peaks.Add(maxAbs);

            return (sampleRate, peaks.ToArray());
        }
        catch
        {
            return (0, Array.Empty<float>());
        }
    }

    // ============================================================
    // 小工具
    // ============================================================

    private Brush GetBrush(string resourceKey, Color fallback)
    {
        object? r = TryFindResource(resourceKey);
        return (r as Brush) ?? new SolidColorBrush(fallback);
    }
}
