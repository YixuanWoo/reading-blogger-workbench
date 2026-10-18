// ============================================================
// RecordingDeskView.xaml.cs - 录音台（V1.16.2 三栏重构）
// ============================================================
//
// 【布局】左：文案选择区 / 中：提词 / 右：已录列表 / 下：录音台
//
// 【一条录音 = 一块】（用户 2026-10-18 拍板）
//   · 块的标题只写「音频 N · 14.3s」，不写"第几段第几句"
//   · 块内所有句子同一个底色 —— Shift 只延长/缩短这一块，
//     不会出现"一段高亮 + 尾巴上一块光标"的怪样子
//   · 四种状态：绿=已录 / 蓝=选中 / 金=待确认 / 灰=没录
//   · 块与块之间留空，按"录音绑定的范围"自然隔开
//
// 【左右联动】点左栏已录块 ⇄ 右栏对应的「音频 N」同时高亮
//
// 【状态机】
//   Idle ──空格──► Recording ──空格──► Candidate（金色，待确认）
//                    Esc=扔掉          │
//         回车 ◄───────────────────────┘  绑定在这一刻才生效
//   · 候选态 Esc = 整条扔掉（wav+台账行一起清，旧绑定不动）
//   · 候选态空格 = 这条不要了重录（同上，然后重录）
//   · 回放只是背景音：回放中可点句子改范围，回车 = 停回放+绑定
//   · EditBind（✎ 改绑）：范围可调，回车保存 / Esc 取消
//
// 【绑定后自动推进】回车绑完 → 选区自动落到"下一句还没录的"，中栏提词跟着换
//
// 【为什么不自动开始下一段】
//   录完一段要听、要想、要喘口气。光标给你摆好，想继续自己按空格。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiteCut.Workbench.Diagnostics;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.RecordingDesk;

using Path = System.IO.Path;

namespace LiteCut.Workbench.Views;

/// <summary>录音台：左选文案、中提词、右已录、下录音台。</summary>
public partial class RecordingDeskView : UserControl, INavPage
{
    private enum Mode { Idle, Recording, Playing, Candidate, EditBind }

    /// <summary>左栏里一段连续相同的状态（渲染成一块）。</summary>
    private enum RunKind { Plain, Recorded, Selected, SelTake, Pending }

    private sealed record Run(RunKind Kind, int A, int B, RecordingTake? Take);

    /// <summary>一步撤销的快照（只保留最近一次）。</summary>
    private sealed class UndoSnap
    {
        public string TakeId = "";
        public int PrevA, PrevB;
        public bool WasConfirm;

        /// <summary>★ 这一笔是"删除"：把被删的那条整个记下来，撤销 = 原样放回。</summary>
        public bool WasDelete;
        public RecordingTake? Deleted;
        public int DeletedIndex;
        public string MovedWav = "";       // wav 被挪到 _deleted 后的绝对路径（撤销时搬回来）

        public List<string> DeadIds = new();
    }

    // ========================================================
    // 状态
    // ========================================================

    private Mode _mode = Mode.Idle;

    private string _projectDir = "";
    private ScriptDocument _doc = new();
    private RecordingStore? _store;
    private readonly RecorderEngine _engine = new();

    /// <summary>句子号 → 已采用它的录音编号。</summary>
    private Dictionary<int, string> _done = new();

    private int _cursor;
    private int? _selA, _selB;
    private int? _pendingA, _pendingB;
    private bool _rangeWasGuessed;

    private RecordingTake? _pendingTake;   // 刚录完、还没确认的那条
    private RecordingTake? _editTake;      // ✎ 正在改绑定的那条
    private RecordingTake? _activeTake;    // 当前"看着"的那条（波形/试听用）

    private string _recId = "";
    private string _recPath = "";

    private Mode _modeBeforePlay = Mode.Idle;
    private bool _suppressPlaybackEnd;

    private bool _loading;

    private readonly DispatcherTimer _uiTimer;
    private readonly Dictionary<string, float[]> _peakCache = new();

    /// <summary>句子号 → 它所在的块（滚动定位用）。</summary>
    private readonly Dictionary<int, FrameworkElement> _blocks = new();

    private UndoSnap? _undo;

    /// <summary>★ 删除要按两次（防手滑）：第一次只是"上膛"，4 秒不点自动收回。</summary>
    private string _armDeleteId = "";

    /// <summary>右栏每条的 ✕ 按钮（上膛时只改它自己，不重建列表、不跳滚动）。</summary>
    private readonly Dictionary<string, Button> _delBtns = new();
    private readonly DispatcherTimer _armTimer;

    // ========================================================
    // 配色
    // ========================================================

    private static readonly Brush InkNormal = Freeze(0xB9, 0xC1, 0xCC);

    private static readonly Brush RecBg = Freeze(0x15, 0x22, 0x1A);
    private static readonly Brush RecBorder = Freeze(0x23, 0x3A, 0x2B);
    private static readonly Brush RecFg = Freeze(0xA9, 0xC9, 0xB6);
    private static readonly Brush RecHead = Freeze(0x7F, 0xBF, 0x98);

    private static readonly Brush SelBg = Freeze(0x1B, 0x33, 0x50);
    private static readonly Brush SelBorder = Freeze(0x2F, 0x5B, 0x8C);
    private static readonly Brush SelFg = Freeze(0xCF, 0xE4, 0xF8);
    private static readonly Brush SelHead = Freeze(0x9E, 0xC5, 0xF0);

    private static readonly Brush PendBg = Freeze(0x2B, 0x24, 0x15);
    private static readonly Brush PendBorder = Freeze(0xD4, 0xAF, 0x37);
    private static readonly Brush PendFg = Freeze(0xF6, 0xEC, 0xD6);
    private static readonly Brush PendHead = Freeze(0xE8, 0xC4, 0x6A);

    private static readonly Brush BarIdle = Freeze(0x39, 0x41, 0x4C);
    private static readonly Brush BarRec = Freeze(0xE0, 0x5A, 0x4E);
    private static readonly Brush BarBlue = Freeze(0x4C, 0x8F, 0xE0);
    private static readonly Brush BarGreen = Freeze(0x2C, 0x8A, 0x5A);

    private static readonly Brush TagGreenFg = Freeze(0x86, 0xD6, 0xA5);
    private static readonly Brush TagGreenBg = Freeze(0x15, 0x22, 0x19);
    private static readonly Brush TagYellowFg = Freeze(0xE8, 0xC4, 0x6A);
    private static readonly Brush TagYellowBg = Freeze(0x2A, 0x22, 0x12);
    private static readonly Brush TagRedFg = Freeze(0xEB, 0x8E, 0x84);
    private static readonly Brush TagRedBg = Freeze(0x2A, 0x17, 0x15);
    private static readonly Brush TagBlueFg = Freeze(0x9E, 0xC5, 0xF0);
    private static readonly Brush TagBlueBg = Freeze(0x16, 0x26, 0x3A);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ========================================================
    // 构造
    // ========================================================

    public RecordingDeskView()
    {
        InitializeComponent();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _uiTimer.Tick += UiTimer_Tick;

        _armTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _armTimer.Tick += (_, _) => DisarmDelete();

        _engine.PlaybackEnded += (_, _) => Dispatcher.BeginInvoke(new Action(OnPlaybackEnded));

        Loaded += (_, _) => Focus();
        Unloaded += (_, _) => StopEverything();
        PreviewMouseDown += (_, _) => { if (!IsKeyboardFocusWithin) Focus(); };
    }

    // ========================================================
    // INavPage
    // ========================================================

    public void OnEnter()
    {
        EnsureLoaded();
        Focus();
    }

    public void OnLeave() => StopEverything();

    private void StopEverything()
    {
        if (_engine.IsRecording) StopRecording(discard: false, quiet: true);
        if (_engine.IsPlaying) StopPlayback();
        _uiTimer.Stop();
    }

    // ========================================================
    // 载入项目
    // ========================================================

    private void EnsureLoaded()
    {
        var proj = WorkbenchWindow.Session.Current;
        if (proj == null)
        {
            ShowEmpty("没有打开项目。先从「进行中」打开一个项目。");
            return;
        }

        bool sameProject = string.Equals(_projectDir, proj.Dir, StringComparison.OrdinalIgnoreCase);
        string scriptPath = Path.Combine(proj.Dir, "script.md");

        if (sameProject && _store != null)
        {
            var probe = ScriptDocument.Load(scriptPath);
            if (probe.Stamp == _doc.Stamp)
            {
                Focus();
                return;
            }
        }

        _projectDir = proj.Dir;
        _doc = ScriptDocument.Load(scriptPath);
        _store = RecordingStore.Load(proj.Dir);

        _loading = true;
        try
        {
            ChkAutoPlay.IsChecked = _store.Data.AutoPlay;
            ChkAutoGuess.IsChecked = _store.Data.AutoGuess;

            if (_doc.IsEmpty)
            {
                ShowEmpty(string.IsNullOrEmpty(_doc.LoadError)
                    ? "script.md 是空的 —— 去写作台写几段，或者把稿子贴进去保存。"
                    : _doc.LoadError);
                return;
            }

            EmptyHost.Visibility = Visibility.Collapsed;
            RefreshDevices();
        }
        finally
        {
            _loading = false;
        }

        _pendingTake = null;
        _editTake = null;
        _activeTake = null;
        _undo = null;
        _rangeWasGuessed = false;
        _pendingA = _pendingB = null;

        RebuildDoneMap();

        // 选区落在"下一句还没录的"
        _selA = _selB = NextUnrecorded(0);
        _cursor = _selA ?? 0;

        SetMode(Mode.Idle);
        PaintAll();
        ShowEmptyWave();

        if (!string.IsNullOrEmpty(_store.Data.ScriptStamp) &&
            _store.Data.ScriptStamp != _doc.Stamp)
        {
            SetVerdict("y", "稿子改过了", "句子可能重排过 —— 老录音的范围对不上，建议检查一下");
        }
        else if (_done.Count > 0)
        {
            SetVerdict("", "", $"已经录好 {_done.Count} / {_doc.Sentences.Count} 句 —— 接着往下念就行");
        }
        else
        {
            SetVerdict("", "", "左栏点一句 → 空格开始录；录完回车绑定，选区会自动跳到下一句");
        }

        ScrollTo(_cursor);
        UpdateDocStat();

        DebugLog.Write("record", "录音台载入", new
        {
            project = proj.Model.Name,
            sentences = _doc.Sentences.Count,
            takes = _store.Data.Takes.Count,
        });
    }

    private void ShowEmpty(string why)
    {
        EmptyHost.Visibility = Visibility.Visible;
        TxtEmptyWhy.Text = why;
        ScriptPanel.Children.Clear();
        TakeList.Children.Clear();
    }

    // ========================================================
    // 编号 / 排序
    // ========================================================

    /// <summary>所有活录音，按稿子顺序（起始句 → 结束句 → 录制时间）。</summary>
    private List<RecordingTake> OrderedAll()
    {
        if (_store == null) return new List<RecordingTake>();
        return _store.Data.Takes
            .Where(t => !t.Dead)
            .OrderBy(t => t.A).ThenBy(t => t.B).ThenBy(t => t.CreatedAt)
            .ToList();
    }

    /// <summary>已确认的录音（右侧列表用；待确认的不进列表）。</summary>
    private List<RecordingTake> OrderedConfirmed()
        => OrderedAll().Where(t => !ReferenceEquals(t, _pendingTake)).ToList();

    /// <summary>「音频 N」的 N —— 只按"已确认"的录音数，待确认的不占号。</summary>
    private int TakeNumber(RecordingTake? t)
    {
        if (t == null) return 0;
        var all = OrderedConfirmed();
        for (int i = 0; i < all.Count; i++)
            if (ReferenceEquals(all[i], t)) return i + 1;
        return 0;
    }

    private string TakeName(RecordingTake? t)
    {
        if (t == null) return "音频 ?";
        if (ReferenceEquals(t, _pendingTake)) return "新的一条";   // 还没确认，不占号
        int n = TakeNumber(t);
        return n <= 0 ? "音频 ?" : "音频 " + n;
    }

    /// <summary>覆盖这一句的已确认录音（没有 = null）。</summary>
    private RecordingTake? TakeAt(int index)
        => OrderedConfirmed().FirstOrDefault(t => t.Covers(index));

    /// <summary>找一条"范围和选区完全一致"的已确认录音（用于把它整块画成蓝色）。</summary>
    private RecordingTake? TakeForRange(int a, int b)
        => OrderedConfirmed().FirstOrDefault(t => t.A == a && t.B == b);

    private int? NextUnrecorded(int from)
    {
        int n = _doc.Sentences.Count;
        for (int i = Math.Clamp(from, 0, Math.Max(0, n - 1)); i < n; i++)
            if (!_done.ContainsKey(i)) return i;
        for (int i = 0; i < n; i++)
            if (!_done.ContainsKey(i)) return i;
        return null;
    }

    // ========================================================
    // 左栏：按"块"渲染
    // ========================================================

    private List<Run> BuildRuns()
    {
        int n = _doc.Sentences.Count;
        int lo = 0, hi = -1;
        if (_selA.HasValue && _selB.HasValue)
        {
            lo = Math.Min(_selA.Value, _selB.Value);
            hi = Math.Max(_selA.Value, _selB.Value);
        }

        var selTake = hi >= lo ? TakeForRange(lo, hi) : null;

        var runs = new List<Run>();
        for (int i = 0; i < n; i++)
        {
            RunKind kind;
            RecordingTake? take = null;

            if (_pendingTake != null && _pendingTake.Covers(i))
            {
                kind = RunKind.Pending;
                take = _pendingTake;
            }
            else if (hi >= lo && i >= lo && i <= hi)
            {
                if (selTake != null) { kind = RunKind.SelTake; take = selTake; }
                else kind = RunKind.Selected;
            }
            else if (_done.TryGetValue(i, out var id))
            {
                take = _store?.Data.Takes.FirstOrDefault(t => t.Id == id);
                kind = take != null ? RunKind.Recorded : RunKind.Plain;
            }
            else kind = RunKind.Plain;

            if (runs.Count > 0 && runs[^1].Kind == kind
                && ReferenceEquals(runs[^1].Take, take) && runs[^1].B == i - 1)
            {
                runs[^1] = runs[^1] with { B = i };
            }
            else runs.Add(new Run(kind, i, i, take));
        }
        return runs;
    }

    private void BuildScriptPanel()
    {
        double offset = ScriptScroll.VerticalOffset;
        ScriptPanel.Children.Clear();
        _blocks.Clear();

        foreach (var run in BuildRuns())
            ScriptPanel.Children.Add(MakeBlock(run));

        ScriptScroll.ScrollToVerticalOffset(offset);
    }

    private FrameworkElement MakeBlock(Run run)
    {
        Brush bg, border, fg, headFg;
        switch (run.Kind)
        {
            case RunKind.Recorded: bg = RecBg; border = RecBorder; fg = RecFg; headFg = RecHead; break;
            case RunKind.SelTake:
            case RunKind.Selected: bg = SelBg; border = SelBorder; fg = SelFg; headFg = SelHead; break;
            case RunKind.Pending: bg = PendBg; border = PendBorder; fg = PendFg; headFg = PendHead; break;
            default: bg = Brushes.Transparent; border = Brushes.Transparent; fg = InkNormal; headFg = InkNormal; break;
        }

        var stack = new StackPanel();

        if (run.Kind != RunKind.Plain)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

            string title;
            switch (run.Kind)
            {
                case RunKind.Pending:
                    title = TakeName(run.Take) + " · 待确认";
                    break;
                case RunKind.Selected:
                    title = "选中";
                    break;
                default:
                    title = TakeName(run.Take);
                    break;
            }

            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = headFg,
                VerticalAlignment = VerticalAlignment.Center,
            });

            string meta;
            if (run.Take != null && run.Kind != RunKind.Selected)
            {
                meta = run.Take.DurSec.ToString("F1") + "s";
                if (run.Take.PeakDb < -50) meta += " · 无声音";
            }
            else
            {
                int chars = _doc.CharsBetween(run.A, run.B);
                double rate = _store?.LearnedRate(_doc) ?? 4.3;
                meta = $"{run.B - run.A + 1} 句 · 约 {Math.Max(2, (int)Math.Round(chars / rate))} 秒";
            }

            head.Children.Add(new TextBlock
            {
                Text = meta,
                FontSize = 10,
                Foreground = run.Kind == RunKind.Pending ? PendHead : Freeze(0x6E, 0x77, 0x83),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            });

            if (run.Take != null)
            {
                var play = new TextBlock
                {
                    Text = "▸",
                    FontSize = 12,
                    Foreground = headFg,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = "试听这一条",
                };
                var captured = run.Take;
                play.MouseLeftButtonDown += (_, e) => { e.Handled = true; PlayTake(captured); };
                head.Children.Add(play);
            }

            if (run.Take != null)
            {
                var captured = run.Take;
                head.MouseLeftButtonDown += (_, e) =>
                {
                    SelectTake(captured);
                    e.Handled = true;
                };
            }

            stack.Children.Add(head);
        }

        for (int i = run.A; i <= run.B; i++)
        {
            int idx = i;
            var line = new TextBlock
            {
                Text = _doc.Sentences[i].Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 22,
                Foreground = fg,
                Cursor = Cursors.Hand,
                Tag = i,
                Margin = new Thickness(0, 0, 0, 2),
            };
            line.MouseLeftButtonDown += (_, e) => { Sentence_Click(idx); e.Handled = true; };
            stack.Children.Add(line);
        }

        var block = new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = run.Kind == RunKind.Plain ? new Thickness(0) : new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = run.Kind == RunKind.Plain ? new Thickness(6, 3, 6, 3) : new Thickness(9, 7, 9, 8),
            Margin = new Thickness(0, 0, 0, run.Kind == RunKind.Plain ? 2 : 7),
            Child = stack,
        };

        for (int i = run.A; i <= run.B; i++) _blocks[i] = block;
        return block;
    }

    // ========================================================
    // 选区 / 点击
    // ========================================================

    private static bool ShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private void Sentence_Click(int idx)
    {
        if (_mode == Mode.Recording) return;   // 录音中不改：范围在开录那一刻就定了

        // 候选态 / 改绑态（含被回放盖着的候选）：点=改终点，Shift+点=改起点
        bool rangeEdit = _mode == Mode.Candidate || _mode == Mode.EditBind
            || (_mode == Mode.Playing && _modeBeforePlay is Mode.Candidate or Mode.EditBind);

        if (rangeEdit && _selA.HasValue)
        {
            if (ShiftDown) _selA = idx;
            else _selB = idx;
            _selA = Math.Min(_selA!.Value, _selB ?? idx);
            _selB = Math.Max(_selA.Value, _selB ?? idx);
            _cursor = _selA.Value;
            _rangeWasGuessed = false;

            if (_mode is Mode.Candidate or Mode.EditBind) ApplyRangeLive();
            else { PaintAll(); ScrollTo(idx); }
            return;
        }

        // 正常态
        if (ShiftDown && _selA.HasValue)
        {
            _selB = idx;
            _selA = Math.Min(_selA!.Value, idx);
            _selB = Math.Max(_selA.Value, idx);
            _cursor = _selA.Value;
            _activeTake = null;
        }
        else
        {
            var take = TakeAt(idx);
            if (take != null)
            {
                // 点绿块里任意一句 = 整条选中（接着按空格就是重录这一条）
                _selA = take.A;
                _selB = take.B;
                _cursor = take.A;
                _activeTake = take;
            }
            else
            {
                _selA = idx;
                _selB = idx;
                _cursor = idx;
                _activeTake = null;
            }
        }

        PaintAll();
        if (_activeTake != null) ShowTakeWave(_activeTake);
        else ShowEmptyWave();
        ScrollTo(idx);
    }

    /// <summary>候选态 / 改绑态下改了范围：立即写进这一条（不动别的绑定）。</summary>
    private void ApplyRangeLive()
    {
        if (!_selA.HasValue || !_selB.HasValue) return;

        int a = Math.Min(_selA.Value, _selB.Value);
        int b = Math.Max(_selA.Value, _selB.Value);

        var target = _mode == Mode.EditBind ? _editTake : _pendingTake;
        if (target != null)
        {
            target.A = a;
            target.B = b;
            _store!.Save();
        }

        PaintAll();
        ScrollTo(a);
    }

    private void BtnClearSel_Click(object sender, RoutedEventArgs e)
    {
        if (_mode is Mode.Recording or Mode.Candidate or Mode.EditBind) return;
        if (_mode == Mode.Playing && _modeBeforePlay is Mode.Candidate or Mode.EditBind) return;

        _selA = _selB = null;
        _activeTake = null;
        PaintAll();
        ShowEmptyWave();
        SetVerdict("", "", "已清除选择 —— 直接按空格也能录，录完按语速猜范围");
    }

    private void SelectTake(RecordingTake take)
    {
        if (_mode == Mode.Recording) return;
        if (_mode == Mode.Playing) StopPlayback();
        if (_mode is Mode.Candidate or Mode.EditBind) return;   // 先把手里这条处理完

        _activeTake = take;
        _selA = take.A;
        _selB = take.B;
        _cursor = take.A;

        SetMode(Mode.Idle);
        ShowTakeWave(take);
        PaintAll();
        ScrollTo(take.A);

        SetVerdict("b", TakeName(take),
            "空格 = 重录这一条 · P = 试听 · 点右栏 ✎ = 改绑定");
    }

    // ========================================================
    // 中栏：提词
    // ========================================================

    private void PaintScript()
    {
        if (_doc.IsEmpty)
        {
            TxtCur.Text = "";
            TxtPrev.Text = "";
            TxtNext.Text = "";
            return;
        }

        int n = _doc.Sentences.Count;
        int lo, hi;

        if (_selA.HasValue && _selB.HasValue)
        {
            lo = Math.Clamp(Math.Min(_selA.Value, _selB.Value), 0, n - 1);
            hi = Math.Clamp(Math.Max(_selA.Value, _selB.Value), 0, n - 1);
        }
        else
        {
            lo = Math.Clamp(_cursor, 0, n - 1);
            int para = _doc.Sentences[lo].Para;
            hi = lo;
            while (hi + 1 < n && _doc.Sentences[hi + 1].Para == para && hi - lo < 3) hi++;
            if (hi == lo && hi + 1 < n) hi++;
        }

        string text = _doc.TextBetween(lo, hi);
        int chars = ScriptDocument.CountChars(text);

        TxtCur.Text = text;
        TxtCur.FontSize = chars <= 60 ? 30 : chars <= 100 ? 27 : chars <= 150 ? 24 : 21;
        TxtCur.LineHeight = TxtCur.FontSize * 1.7;

        TxtPrev.Text = lo > 0 ? _doc.Sentences[lo - 1].Text : "";
        TxtNext.Text = hi < n - 1 ? _doc.Sentences[hi + 1].Text : "";
        TxtPrev.Visibility = TxtPrev.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtNext.Visibility = TxtNext.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 信息行：只说"这是哪条音频"，不再报第几段第几句
        string who;
        if (_mode == Mode.EditBind && _editTake != null) who = TakeName(_editTake) + " · 改绑定";
        else if (_mode == Mode.Candidate && _pendingTake != null) who = TakeName(_pendingTake) + " · 待确认";
        else if (_activeTake != null && _selA.HasValue && _selB.HasValue
                 && _activeTake.A == Math.Min(_selA.Value, _selB.Value)
                 && _activeTake.B == Math.Max(_selA.Value, _selB.Value))
            who = TakeName(_activeTake);
        else who = "下一段";

        TxtSegNow.Text = who;

        double rate = _store?.LearnedRate(_doc) ?? 4.3;
        int est = Math.Max(2, (int)Math.Round(chars / rate));
        TxtSegMeta.Text = $"{chars} 字 · 约 {est} 秒";

        switch (_mode)
        {
            case Mode.Recording:
                SetBadge("● 正在录", TagRedFg, TagRedBg);
                CurBar.Background = BarRec;
                break;
            case Mode.Playing:
                SetBadge("▶ 回放中", TagBlueFg, TagBlueBg);
                CurBar.Background = BarBlue;
                break;
            case Mode.Candidate:
                SetBadge(_rangeWasGuessed ? "猜的范围 · 可改" : "待确认 · 可改", TagYellowFg, TagYellowBg);
                CurBar.Background = Freeze(0xD4, 0xAF, 0x37);
                break;
            case Mode.EditBind:
                SetBadge("✎ 改绑定 · 回车保存", TagBlueFg, TagBlueBg);
                CurBar.Background = BarBlue;
                break;
            default:
                if (_selA.HasValue) SetBadge("已选中 · 待录", TagBlueFg, TagBlueBg);
                else SetBadge("待录", Freeze(0x8B, 0x93, 0xA1), Freeze(0x23, 0x2A, 0x33));
                CurBar.Background = _selA.HasValue ? BarBlue : BarIdle;
                break;
        }
    }

    private void SetBadge(string text, Brush fg, Brush bg)
    {
        TxtBadge.Text = text;
        TxtBadge.Foreground = fg;
        BadgeHost.Background = bg;
    }

    private void ScrollTo(int index)
    {
        if (_blocks.TryGetValue(index, out var block))
        {
            try { block.BringIntoView(); } catch { }
        }
    }

    // ========================================================
    // 右栏：已录列表
    // ========================================================

    private void BuildTakeList()
    {
        if (TakeList == null || _store == null) return;

        TakeList.Children.Clear();
        _delBtns.Clear();
        var list = OrderedConfirmed();
        TxtTakeCount.Text = list.Count + " 条";

        if (list.Count == 0)
        {
            TakeList.Children.Add(new TextBlock
            {
                Text = "还没有录好的\n录完一条、按回车绑定，它就出现在这里",
                FontSize = 11.5,
                LineHeight = 20,
                Foreground = Freeze(0x5F, 0x67, 0x72),
                Margin = new Thickness(2, 8, 0, 8),
            });
            return;
        }

        int no = 0;
        foreach (var t in list)
        {
            no++;
            bool active = ReferenceEquals(_activeTake, t) || ReferenceEquals(_editTake, t);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var line1 = new Grid();
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = "音频 " + no,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = active ? Freeze(0xF4, 0xE7, 0xB8) : Freeze(0xE6, 0xE9, 0xEF),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var dur = new TextBlock
            {
                Text = t.DurSec.ToString("F1") + "s" + (t.PeakDb < -50 ? " · 无声音" : ""),
                FontSize = 10,
                Foreground = t.PeakDb < -50 ? TagRedFg : Freeze(0x78, 0x82, 0x8F),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dur, 1);
            line1.Children.Add(name);
            line1.Children.Add(dur);

            var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

            var play = new Button
            {
                Content = "▸ 试听",
                Style = (Style)FindResource("MiniBtn"),
                Tag = t,
                Margin = new Thickness(0, 0, 4, 0),
            };
            play.Click += (o, _) =>
            {
                if (o is Button b && b.Tag is RecordingTake tk) PlayTake(tk);
            };

            var edit = new Button
            {
                Content = "✎ 改绑",
                Style = (Style)FindResource("MiniBtn"),
                Tag = t,
            };
            edit.Click += (o, _) =>
            {
                if (o is Button b && b.Tag is RecordingTake tk) BeginEditBind(tk);
            };

            // ★ 删除：第一次点只是"上膛"（变成「确认删除」），再点一次才真删
            bool armed = _armDeleteId == t.Id;
            var del = new Button
            {
                Content = armed ? "确认删除？" : "✕",
                Style = (Style)FindResource("MiniBtnDanger"),
                Tag = t,
                Foreground = armed ? Freeze(0xFF, 0x9A, 0x8C) : Freeze(0x8E, 0x82, 0x80),
                ToolTip = "删掉这条录音（文件挪到 素材\\录音\\_deleted，Ctrl+Z 可找回）",
            };
            del.Click += (o, _) =>
            {
                if (o is Button b && b.Tag is RecordingTake tk) OnDeleteClicked(tk);
            };
            _delBtns[t.Id] = del;

            line2.Children.Add(play);
            line2.Children.Add(edit);
            line2.Children.Add(del);

            Grid.SetRow(line1, 0);
            Grid.SetRow(line2, 1);
            grid.Children.Add(line1);
            grid.Children.Add(line2);

            var item = new Border
            {
                Background = active ? Freeze(0x1F, 0x1B, 0x10) : Freeze(0x16, 0x1A, 0x20),
                BorderBrush = active ? Freeze(0xD4, 0xAF, 0x37) : Freeze(0x26, 0x2C, 0x35),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 6, 9, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Child = grid,
                Tag = t,
                ToolTip = "点一下 = 选中这条（左栏同步高亮）",
            };
            item.MouseLeftButtonDown += (o, e) =>
            {
                if (o is Border bd && bd.Tag is RecordingTake tk)
                {
                    SelectTake(tk);
                    e.Handled = true;
                }
            };

            TakeList.Children.Add(item);
        }
    }

    private void PlayTake(RecordingTake take)
    {
        if (_mode == Mode.Recording) return;
        if (_mode == Mode.Playing) StopPlayback();

        _activeTake = take;
        PlayActive();
    }

    // ========================================================
    // 删除（按两下确认 / Ctrl+Z 能找回）
    // ========================================================

    private void OnDeleteClicked(RecordingTake take)
    {
        if (_mode == Mode.Recording) return;
        if (ReferenceEquals(take, _pendingTake)) return;     // 待确认的那条走 Esc，不进列表

        if (_armDeleteId == take.Id) { DeleteTake(take); return; }
        ArmDelete(take);
    }

    /// <summary>第一次点 = 上膛（按钮字变成「确认删除？」），4 秒不点自己收回。</summary>
    private void ArmDelete(RecordingTake take)
    {
        _armDeleteId = take.Id;
        PaintDeleteButtons();

        _armTimer.Stop();
        _armTimer.Start();

        SetVerdict("y", "删掉 " + TakeName(take) + "？",
            "再点一次 ✕ 就删 · 文件只是挪到 素材\\录音\\_deleted，Ctrl+Z 能找回");
        DebugLog.Write("record", "删除待确认 " + take.Id, new { take.A, take.B });
    }

    private void DisarmDelete()
    {
        _armTimer.Stop();
        if (_armDeleteId.Length == 0) return;
        _armDeleteId = "";
        PaintDeleteButtons();
        SetVerdict("", "", "删除已取消");
    }

    private void PaintDeleteButtons()
    {
        foreach (var kv in _delBtns)
        {
            bool on = kv.Key == _armDeleteId;
            kv.Value.Content = on ? "确认删除？" : "✕";
            kv.Value.Foreground = on ? Freeze(0xFF, 0x9A, 0x8C) : Freeze(0x8E, 0x82, 0x80);
        }
    }

    /// <summary>
    /// 真删：台账里去掉这条，wav **挪进 `素材\录音\_deleted\`**（不真删，撤销要能搬回来）。
    /// 删完把这段句子选回去 —— 直接空格就能就着重录。
    /// </summary>
    private void DeleteTake(RecordingTake take)
    {
        if (_store == null) return;
        if (_mode == Mode.Playing) StopPlayback();

        string name = TakeName(take);                  // 删完就取不到号了，先记下
        int idx = _store.Data.Takes.IndexOf(take);

        string src = TakePath(take);
        string moved = "";
        try
        {
            if (File.Exists(src))
            {
                string dir = Path.Combine(_store.Dir, "_deleted");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, Path.GetFileName(src));
                if (File.Exists(dst))
                    dst = Path.Combine(dir, take.Id + "_" + DateTime.Now.ToString("HHmmss") + ".wav");
                File.Move(src, dst);
                moved = dst;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("record", "删除时挪 wav 失败", new { take.Id, msg = ex.Message });
        }

        _peakCache.Remove(src);
        _store.Data.Takes.Remove(take);
        _store.Save();

        _undo = new UndoSnap
        {
            TakeId = take.Id,
            WasDelete = true,
            Deleted = take,
            DeletedIndex = idx,
            MovedWav = moved,
        };

        _armTimer.Stop();
        _armDeleteId = "";

        bool wasEditing = ReferenceEquals(_editTake, take);
        if (wasEditing) _editTake = null;
        if (ReferenceEquals(_activeTake, take)) _activeTake = null;

        // 候选 / 改绑各自有选区，删别的条目时别把它们夺走
        bool keepMode = !wasEditing && _mode is Mode.Candidate or Mode.EditBind;

        if (!keepMode)
        {
            // 删掉的地方就地选回去 —— 空格可以直接重录这一段
            _selA = take.A;
            _selB = take.B;
            _cursor = take.A;
            _rangeWasGuessed = false;
        }

        RebuildDoneMap();
        if (!keepMode) SetMode(Mode.Idle);
        PaintAll();

        if (!keepMode)
        {
            ShowEmptyWave();
            SetVerdict("y", "已删除 " + name,
                "文件只是挪到 素材\\录音\\_deleted（Ctrl+Z 能找回）· 这段已选回去，空格就地重录");
        }
        else
        {
            SetVerdict("y", "已删除 " + name, "Ctrl+Z 能找回");
        }

        DebugLog.Write("record", "删除 " + take.Id, new { take.A, take.B });
    }

    // ========================================================
    // 改绑定（✎）
    // ========================================================

    private void BeginEditBind(RecordingTake take)
    {
        if (_mode == Mode.Recording) return;
        if (_mode != Mode.Idle) return;      // 候选/改绑/回放先收尾

        _editTake = take;
        _activeTake = take;
        _selA = take.A;
        _selB = take.B;
        _cursor = take.A;

        SetMode(Mode.EditBind);
        PaintAll();
        ShowTakeWave(take);
        ScrollTo(take.A);

        SetVerdict("b", TakeName(take) + " · 改绑定",
            "点句子 / Shift+点 调范围 · 回车保存 · Esc 取消");
    }

    private void SaveEditBind()
    {
        if (_mode != Mode.EditBind || _editTake == null || !_selA.HasValue || !_selB.HasValue) return;

        int a = Math.Min(_selA.Value, _selB.Value);
        int b = Math.Max(_selA.Value, _selB.Value);
        var take = _editTake;

        _undo = new UndoSnap
        {
            TakeId = take.Id,
            PrevA = take.A,
            PrevB = take.B,
            WasConfirm = false,
            DeadIds = _store!.Data.Takes
                .Where(x => !x.Dead && x.Id != take.Id && x.Overlaps(a, b))
                .Select(x => x.Id).ToList(),
        };

        take.A = a;
        take.B = b;
        _store.Activate(take);
        _store.Save();
        RebuildDoneMap();

        _editTake = null;
        SetMode(Mode.Idle);
        PaintAll();
        ShowTakeWave(take);
        SetVerdict("g", "绑好了", TakeName(take) + " 已改到新的位置（Ctrl+Z 可撤销）");

        DebugLog.Write("record", "改绑定 " + take.Id, new { a, b });
    }

    private void CancelEditBind()
    {
        _editTake = null;
        _selA = _selB = null;
        SetMode(Mode.Idle);
        PaintAll();
        ShowEmptyWave();
        SetVerdict("", "", "改绑已取消");
    }

    // ========================================================
    // 模式 / 按钮
    // ========================================================

    private void PaintAll()
    {
        BuildScriptPanel();
        BuildTakeList();
        PaintScript();
        UpdateButtons();
        UpdateDocStat();
    }

    private void SetMode(Mode m)
    {
        _mode = m;
        UpdateButtons();
        PaintScript();

        if (m == Mode.Recording)
        {
            Wave.Dim = false;
            Wave.ShowPlayhead = true;
            Wave.Progress = 1;
            WaveLabel.Text = "录音中…";
        }
        else if (m == Mode.Playing)
        {
            Wave.Dim = false;
            Wave.ShowPlayhead = true;
        }
        else if (m == Mode.Idle && _activeTake == null)
        {
            ShowEmptyWave();
        }
    }

    private void UpdateButtons()
    {
        bool rec = _mode == Mode.Recording;
        bool play = _mode == Mode.Playing;

        if (rec)
        {
            TxtMainDot.Text = "■";
            TxtMainLabel.Text = "停止";
            BtnMain.Background = BarRec;
        }
        else
        {
            TxtMainDot.Text = "●";
            TxtMainLabel.Text = _mode switch
            {
                Mode.Playing => "停止回放",
                Mode.Candidate => "重录这条",
                Mode.EditBind => "改绑定中",
                _ => "开始录音",
            };
            BtnMain.Background = _mode is Mode.Playing or Mode.EditBind
                ? Freeze(0x39, 0x41, 0x4C)
                : Freeze(0xC7, 0x44, 0x3A);
        }

        bool hasTake = _activeTake != null && File.Exists(TakePath(_activeTake));

        bool candPending = _mode == Mode.Candidate
            || (_mode == Mode.Playing && _modeBeforePlay == Mode.Candidate);

        BtnPlay.IsEnabled = !rec && hasTake;
        BtnRedo.IsEnabled = !rec && _mode != Mode.EditBind && (hasTake || _selA.HasValue);
        BtnOk.IsEnabled = (candPending && _pendingTake != null) || _mode == Mode.EditBind;
        TxtOkLabel.Text = _mode == Mode.EditBind ? "保存改绑" : "就绑这段";
        BtnDiscard.IsEnabled = _mode is Mode.Candidate or Mode.EditBind
            || (_mode == Mode.Playing && _modeBeforePlay is Mode.Candidate or Mode.EditBind);
        BtnMain.IsEnabled = _mode != Mode.EditBind;
        BtnClearSel.IsEnabled = !rec && _mode == Mode.Idle && _selA.HasValue;
        BtnUndo.IsEnabled = _undo != null && !rec;

        RecDot.Fill = rec ? BarRec : BarIdle;

        if (rec) TxtTime.Text = Fmt(_engine.ElapsedSec);
        else if (play) TxtTime.Text = Fmt(_engine.PlayPositionSec);
        else TxtTime.Text = _activeTake != null ? Fmt(_activeTake.DurSec) : "00:00.0";
    }

    // ========================================================
    // 录音
    // ========================================================

    private void StartRecording()
    {
        if (_doc.IsEmpty || _store == null) return;
        if (_engine.IsRecording || _mode == Mode.EditBind) return;

        if (_mode == Mode.Candidate) DiscardCandidate();   // 上一条不要了

        if (CmbDevice.SelectedItem is not AudioInputDevice dev)
        {
            SetVerdict("r", "没有麦克风", "先连上手机 / 插上麦克风，再点右上角「刷新」");
            return;
        }

        _pendingA = _selA;
        _pendingB = _selB;

        _recId = _store.NextId();
        _recPath = _store.NewWavPath(_recId);

        try
        {
            _engine.Start(_recPath, dev.Index);
        }
        catch (Exception ex)
        {
            SetVerdict("r", "打不开麦克风", ex.Message);
            DebugLog.Error("录音台：打不开麦克风", ex);
            return;
        }

        SetMode(Mode.Recording);
        _uiTimer.Start();

        Wave.Peaks = Array.Empty<float>();
        Wave.Progress = 0;
        WaveTime.Text = "00:00.0";

        DebugLog.Write("record", "开始录音 " + _recId, new
        {
            bindA = _pendingA, bindB = _pendingB, device = dev.Name, file = _recPath,
        });
    }

    private void StopRecording(bool discard, bool quiet = false)
    {
        if (!_engine.IsRecording) return;

        double dur = _engine.Stop();
        _uiTimer.Stop();

        if (discard || dur < 0.35)
        {
            try { if (File.Exists(_recPath)) File.Delete(_recPath); } catch { }
            _pendingA = _pendingB = null;
            SetMode(Mode.Idle);
            PaintAll();
            if (!quiet) SetVerdict("y", "这条太短", discard ? "已丢弃" : "不到 0.3 秒，已丢弃 —— 按空格重来");
            DebugLog.Write("record", "录音取消 " + _recId, new { dur });
            return;
        }

        double fileDur = RecorderEngine.DurationOf(_recPath);
        if (fileDur > 0.05) dur = fileDur;

        var peaks = RecorderEngine.PeaksOf(_recPath, 260);
        _peakCache[_recPath] = peaks;
        double peakDb = -96;
        if (peaks.Length > 0)
        {
            float mx = peaks.Max();
            peakDb = mx <= 1e-5f ? -96 : 20 * Math.Log10(mx);
        }

        int a, b;
        _rangeWasGuessed = false;

        if (_pendingA.HasValue && _pendingB.HasValue)
        {
            a = Math.Min(_pendingA.Value, _pendingB.Value);
            b = Math.Max(_pendingA.Value, _pendingB.Value);
        }
        else if (ChkAutoGuess.IsChecked == true)
        {
            (a, b) = GuessRange(dur);
            _rangeWasGuessed = true;
        }
        else
        {
            a = b = Math.Clamp(_cursor, 0, _doc.Sentences.Count - 1);
        }

        int n = _doc.Sentences.Count;
        a = Math.Clamp(a, 0, n - 1);
        b = Math.Clamp(b, 0, n - 1);

        var take = new RecordingTake
        {
            Id = _recId,
            A = a,
            B = b,
            File = "takes/" + _recId + ".wav",
            DurSec = Math.Round(dur, 2),
            PeakDb = Math.Round(peakDb, 1),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // 还没确认：挂进台账当"候选"，不动任何已有绑定
        _store!.Data.Takes.Add(take);
        _store.Data.ScriptStamp = _doc.Stamp;
        _store.Save();

        _pendingTake = take;
        _activeTake = take;
        _selA = a;
        _selB = b;
        _cursor = a;
        _pendingA = _pendingB = null;

        RebuildDoneMap(take.Id);
        SetMode(Mode.Candidate);
        PaintAll();
        UpdateVerdict(take);

        Wave.Peaks = peaks;
        Wave.Dim = false;
        Wave.Progress = 1;
        Wave.ShowPlayhead = false;
        WaveLabel.Text = TakeName(take) + " · 待确认 · " + dur.ToString("F1") + " 秒";
        WaveTime.Text = Fmt(dur);
        ScrollTo(a);

        DebugLog.Write("record", "录完 " + take.Id, new
        {
            a, b, dur = take.DurSec, peakDb = take.PeakDb, guessed = _rangeWasGuessed,
        });

        if (ChkAutoPlay.IsChecked == true) PlayActive();
    }

    private (int a, int b) GuessRange(double dur)
    {
        int n = _doc.Sentences.Count;
        int start = Math.Clamp(_cursor, 0, n - 1);

        if (_done.ContainsKey(start))
        {
            int probe = start;
            while (probe < n && _done.ContainsKey(probe)) probe++;
            if (probe < n) start = probe;
        }

        double rate = _store?.LearnedRate(_doc) ?? 4.3;
        double budget = Math.Max(1, dur * rate);

        double acc = 0;
        int end = start;
        for (int i = start; i < n; i++)
        {
            acc += _doc.Sentences[i].Chars;
            end = i;
            if (acc >= budget) break;
        }
        return (start, end);
    }

    private void UpdateVerdict(RecordingTake take)
    {
        int chars = _doc.CharsBetween(take.A, take.B);
        double speed = take.DurSec > 0.1 ? chars / take.DurSec : 0;

        if (take.PeakDb < -50)
        {
            SetVerdict("r", "没声音", $"{take.PeakDb:F0} dB · 检查 WO Mic 客户端是不是连上了");
            return;
        }

        if (speed < 2.6)
        {
            SetVerdict("y", "听一下", $"语速 {speed:F1} 字/秒 偏慢 —— 停顿多，或者念漏了");
            return;
        }

        if (speed > 6.5)
        {
            SetVerdict("y", "听一下", $"语速 {speed:F1} 字/秒 偏快 —— 可能漏字或口胡");
            return;
        }

        string tail = _rangeWasGuessed ? " · 范围是猜的，可改" : "";
        SetVerdict("g", "这段可以", $"{chars} 字 · 语速 {speed:F1} 字/秒 · {take.DurSec:F1} 秒{tail}");
    }

    private void SetVerdict(string kind, string tag, string text)
    {
        TxtVerdict.Text = text;

        if (string.IsNullOrEmpty(tag))
        {
            VerdictTagHost.Visibility = Visibility.Collapsed;
            return;
        }

        VerdictTagHost.Visibility = Visibility.Visible;
        TxtVerdictTag.Text = tag;

        switch (kind)
        {
            case "g":
                TxtVerdictTag.Foreground = TagGreenFg;
                VerdictTagHost.Background = TagGreenBg;
                break;
            case "y":
                TxtVerdictTag.Foreground = TagYellowFg;
                VerdictTagHost.Background = TagYellowBg;
                break;
            case "r":
                TxtVerdictTag.Foreground = TagRedFg;
                VerdictTagHost.Background = TagRedBg;
                break;
            case "b":
                TxtVerdictTag.Foreground = TagBlueFg;
                VerdictTagHost.Background = TagBlueBg;
                break;
            default:
                TxtVerdictTag.Foreground = Freeze(0x8B, 0x93, 0xA1);
                VerdictTagHost.Background = Freeze(0x23, 0x2A, 0x33);
                break;
        }
    }

    // ========================================================
    // 试听
    // ========================================================

    private void PlayActive()
    {
        if (_mode == Mode.Recording || _activeTake == null) return;

        string path = TakePath(_activeTake);
        if (!File.Exists(path)) return;

        try
        {
            _modeBeforePlay = _mode;
            _engine.Play(path);
        }
        catch (Exception ex)
        {
            SetVerdict("r", "放不出来", ex.Message);
            return;
        }

        SetMode(Mode.Playing);
        Wave.Peaks = GetPeaks(_activeTake);
        Wave.Dim = false;
        Wave.Progress = 0;
        Wave.ShowPlayhead = true;
        WaveLabel.Text = TakeName(_activeTake) + " · 回放中 · " + _activeTake.DurSec.ToString("F1") + " 秒";
        _uiTimer.Start();
    }

    private void StopPlayback()
    {
        _suppressPlaybackEnd = true;
        _engine.StopPlay();
        _uiTimer.Stop();

        if (_mode == Mode.Playing) SetMode(_modeBeforePlay);
        _suppressPlaybackEnd = false;

        Wave.ShowPlayhead = false;
        Wave.Progress = 1;
        UpdateButtons();
    }

    private void OnPlaybackEnded()
    {
        if (_suppressPlaybackEnd) return;
        if (_mode != Mode.Playing) return;

        _engine.StopPlay();
        _uiTimer.Stop();

        SetMode(_modeBeforePlay);
        Wave.Progress = 1;
        Wave.ShowPlayhead = false;
        WaveLabel.Text = _activeTake != null
            ? TakeName(_activeTake) + " · " + _activeTake.DurSec.ToString("F1") + " 秒"
            : "还没有录";
        UpdateButtons();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_mode == Mode.Recording)
        {
            double db = _engine.LevelDb;
            TxtTime.Text = Fmt(_engine.ElapsedSec);
            WaveTime.Text = Fmt(_engine.ElapsedSec);
            PaintMeter(db);

            var live = _engine.SnapshotLivePeaks();
            Wave.Peaks = RecorderEngine.Resample(live, 260);
            Wave.Dim = false;
            Wave.Progress = 1;
            Wave.ShowPlayhead = true;
        }
        else if (_mode == Mode.Playing)
        {
            double pos = _engine.PlayPositionSec;
            double total = _engine.PlayTotalSec;
            TxtTime.Text = Fmt(pos);
            WaveTime.Text = Fmt(pos);
            Wave.Progress = total > 0.05 ? pos / total : 0;
            Wave.ShowPlayhead = true;

            double db = _engine.LevelDb;
            if (db > -90) PaintMeter(db);
        }
        else
        {
            _uiTimer.Stop();
        }
    }

    private void PaintMeter(double db)
    {
        TxtDb.Text = db <= -95 ? "-96" : db.ToString("F0");

        double ratio = Math.Clamp((db + 60) / 60, 0, 1);
        MeterFill.Width = Math.Max(0, ratio * MeterHost.Width);
        MeterFill.Background = db < -45 ? BarRec : BarGreen;
    }

    // ========================================================
    // 确认 / 扔掉 / 撤销
    // ========================================================

    private void ConfirmBind()
    {
        if (_mode != Mode.Candidate || _pendingTake == null) return;

        var take = _pendingTake;

        _undo = new UndoSnap
        {
            TakeId = take.Id,
            PrevA = take.A,
            PrevB = take.B,
            WasConfirm = true,
            DeadIds = _store!.Data.Takes
                .Where(x => !x.Dead && x.Id != take.Id && x.Overlaps(take.A, take.B))
                .Select(x => x.Id).ToList(),
        };

        _store.Activate(take);
        _store.Save();

        _pendingTake = null;
        _activeTake = take;
        _rangeWasGuessed = false;
        RebuildDoneMap();

        // ★ 绑定后自动推进：选区落到"下一句还没录的"
        var next = NextUnrecorded(take.B + 1);
        if (next.HasValue)
        {
            _selA = _selB = next.Value;
            _cursor = next.Value;
        }
        else
        {
            _selA = _selB = null;
            _cursor = _doc.Sentences.Count - 1;
        }

        SetMode(Mode.Idle);
        PaintAll();
        ShowTakeWave(take);

        if (next.HasValue)
            SetVerdict("g", "绑好了", TakeName(take) + " · 选区已跳到下一句，按空格接着录");
        else
            SetVerdict("g", "全部录完了", $"{_done.Count} / {_doc.Sentences.Count} 句 —— 都绑好了");

        ScrollTo(_cursor);
        DebugLog.Write("record", "确认绑定 " + take.Id, new { take.A, take.B, cursor = _cursor });
    }

    private void DiscardCandidate()
    {
        if (_mode != Mode.Candidate || _pendingTake == null || _store == null) return;

        string wav = TakePath(_pendingTake);
        _peakCache.Remove(wav);
        try { if (File.Exists(wav)) File.Delete(wav); } catch { }

        _store.Data.Takes.Remove(_pendingTake);
        _store.Save();

        _pendingTake = null;
        _activeTake = null;
        _rangeWasGuessed = false;
        _selA = _selB = null;
        RebuildDoneMap();

        SetMode(Mode.Idle);
        PaintAll();
        ShowEmptyWave();
        DebugLog.Write("record", "扔掉候选录音（未确认，无痕迹）");
    }

    private void UndoLast()
    {
        if (_undo == null || _store == null) return;

        // ★ 撤销"删除"：那条整个放回去（wav 从 _deleted 搬回 takes）
        if (_undo.WasDelete && _undo.Deleted != null)
        {
            var dt = _undo.Deleted;

            if (_undo.MovedWav.Length > 0 && File.Exists(_undo.MovedWav))
            {
                try
                {
                    Directory.CreateDirectory(_store.TakesDir);
                    File.Move(_undo.MovedWav, TakePath(dt));
                }
                catch (Exception ex)
                {
                    DebugLog.Write("record", "撤销删除时搬 wav 失败", new { dt.Id, msg = ex.Message });
                }
            }

            if (!_store.Data.Takes.Contains(dt))
            {
                int at = Math.Clamp(_undo.DeletedIndex, 0, _store.Data.Takes.Count);
                _store.Data.Takes.Insert(at, dt);
            }
            dt.Dead = false;

            _undo = null;
            _armTimer.Stop();
            _armDeleteId = "";
            _store.Save();
            RebuildDoneMap();

            _activeTake = dt;
            _selA = dt.A;
            _selB = dt.B;
            _cursor = dt.A;

            SetMode(Mode.Idle);
            PaintAll();
            ShowTakeWave(dt);
            SetVerdict("b", "已撤销", TakeName(dt) + " 回来了");
            DebugLog.Write("record", "撤销删除 " + dt.Id);
            return;
        }

        var take = _store.Data.Takes.FirstOrDefault(t => t.Id == _undo.TakeId);
        if (take == null) { _undo = null; UpdateButtons(); return; }

        foreach (var id in _undo.DeadIds)
        {
            var dead = _store.Data.Takes.FirstOrDefault(t => t.Id == id);
            if (dead != null) dead.Dead = false;
        }

        if (_undo.WasConfirm)
        {
            // 回到"待确认"：这条重新挂起，计数和已录列表都退回去
            _pendingTake = take;
            _activeTake = take;
            _selA = take.A;
            _selB = take.B;
            _cursor = take.A;
            RebuildDoneMap(take.Id);
            _undo = null;
            _store.Save();

            SetMode(Mode.Candidate);
            PaintAll();
            ShowTakeWave(take);
            SetVerdict("b", "已撤销", "回到待确认 —— 回车重新绑，或 Esc 扔掉");
        }
        else
        {
            take.A = _undo.PrevA;
            take.B = _undo.PrevB;
            RebuildDoneMap();
            _undo = null;
            _store.Save();

            SetMode(Mode.Idle);
            PaintAll();
            ShowTakeWave(take);
            SetVerdict("b", "已撤销", TakeName(take) + " 的绑定已回到上一步");
        }

        DebugLog.Write("record", "撤销 " + take.Id);
    }

    // ========================================================
    // 重录
    // ========================================================

    private void Redo()
    {
        if (_mode == Mode.Recording || _mode == Mode.EditBind) return;
        if (_mode == Mode.Playing) StopPlayback();

        int a, b;
        if (_mode == Mode.Candidate && _pendingTake != null) { a = _pendingTake.A; b = _pendingTake.B; }
        else if (_selA.HasValue && _selB.HasValue) { a = Math.Min(_selA.Value, _selB.Value); b = Math.Max(_selA.Value, _selB.Value); }
        else if (_activeTake != null) { a = _activeTake.A; b = _activeTake.B; }
        else { a = _cursor; b = _cursor; }

        _selA = a;
        _selB = b;
        _cursor = a;

        PaintAll();
        StartRecording();
    }

    // ========================================================
    // 波形
    // ========================================================

    private void ShowTakeWave(RecordingTake take)
    {
        Wave.Peaks = GetPeaks(take);
        Wave.Dim = false;
        Wave.Progress = 1;
        Wave.ShowPlayhead = false;
        WaveLabel.Text = TakeName(take) + " · " + take.DurSec.ToString("F1") + " 秒";
        WaveTime.Text = Fmt(take.DurSec);
        UpdateButtons();
    }

    private void ShowEmptyWave()
    {
        Wave.Peaks = Array.Empty<float>();
        Wave.Dim = true;
        Wave.Progress = 1;
        Wave.ShowPlayhead = false;
        WaveLabel.Text = "还没有录";
        WaveTime.Text = "00:00.0";
    }

    private float[] GetPeaks(RecordingTake take)
    {
        string path = TakePath(take);
        if (_peakCache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return Array.Empty<float>();

        var peaks = RecorderEngine.PeaksOf(path, 260);
        _peakCache[path] = peaks;
        return peaks;
    }

    private string TakePath(RecordingTake take)
        => _store == null ? "" : _store.WavPath(take);

    // ========================================================
    // 映射 / 计数
    // ========================================================

    private void RebuildDoneMap(string? excludeId = null)
    {
        _done = _store?.BuildDoneMap(_doc.Sentences.Count, excludeId) ?? new Dictionary<int, string>();
        UpdateDocStat();
    }

    private void UpdateDocStat()
    {
        if (TxtDocStat == null) return;
        TxtDocStat.Text = $"{_done.Count} / {_doc.Sentences.Count} 句";
    }

    private static string Fmt(double sec)
    {
        if (sec < 0) sec = 0;
        int m = (int)(sec / 60);
        double s = sec - m * 60;
        return $"{m:00}:{s:00.0}";
    }

    // ========================================================
    // 设备 / 开关
    // ========================================================

    private void RefreshDevices()
    {
        var list = RecorderEngine.AudioInputDevices();

        CmbDevice.ItemsSource = null;
        CmbDevice.ItemsSource = list;

        if (list.Count == 0)
        {
            SetVerdict("r", "没找到麦克风", "插上麦克风或连上 WO Mic 客户端，再点「刷新」");
            return;
        }

        string want = _store?.Data.LastDevice ?? "";
        int idx = list.FindIndex(d => string.Equals(d.Name, want, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = list.FindIndex(d => d.Name.Contains("WO Mic", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = 0;

        CmbDevice.SelectedIndex = idx;
    }

    private void CmbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _store == null || CmbDevice.SelectedItem is not AudioInputDevice dev) return;
        if (_store.Data.LastDevice == dev.Name) return;

        _store.Data.LastDevice = dev.Name;
        SaveStore();
        DebugLog.Write("record", "选中麦克风：" + dev.Name);
    }

    private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        SetVerdict("", "", "已刷新设备列表");
    }

    private void ChkAutoPlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        _store.Data.AutoPlay = ChkAutoPlay.IsChecked == true;
        SaveStore();
    }

    private void ChkAutoGuess_Changed(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        _store.Data.AutoGuess = ChkAutoGuess.IsChecked == true;
        SaveStore();
    }

    private void SaveStore()
    {
        if (_loading) return;
        try { _store?.Save(); }
        catch (Exception ex) { DebugLog.Error("录音台：写 record.json 失败", ex); }
    }

    private void BtnReloadScript_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Recording) return;

        _projectDir = "";
        EnsureLoaded();

        if (!_doc.IsEmpty)
            SetVerdict("", "", $"稿子已重读：{_doc.Sentences.Count} 句");
    }

    private void BtnGoScript_Click(object sender, RoutedEventArgs e)
    {
        WorkbenchWindow.FindOwner(this)?.GoTo(NavTree.Ids.Script);
    }

    // ========================================================
    // 按钮 / 快捷键
    // ========================================================

    private void BtnMain_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Recording) StopRecording(false);
        else if (_mode == Mode.Playing) StopPlayback();
        else if (_mode == Mode.EditBind) return;
        else StartRecording();
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Playing) StopPlayback();
        else PlayActive();
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e) => Redo();

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.EditBind) SaveEditBind();
        else ConfirmBind();
    }

    private void BtnDiscard_Click(object sender, RoutedEventArgs e) => DiscardCurrent();

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoLast();

    private void DiscardCurrent()
    {
        if (_mode == Mode.Recording) { StopRecording(discard: true); return; }
        if (_mode == Mode.Playing && _modeBeforePlay is Mode.Candidate or Mode.EditBind) StopPlayback();

        if (_mode == Mode.EditBind) { CancelEditBind(); return; }

        if (_mode == Mode.Candidate)
        {
            DiscardCandidate();
            SetVerdict("", "", "这条扔掉了 —— 按空格重录");
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_doc.IsEmpty) { base.OnPreviewKeyDown(e); return; }

        switch (e.Key)
        {
            case Key.Space:
                if (_mode == Mode.Recording) StopRecording(false);
                else if (_mode == Mode.Playing) StopPlayback();
                else if (_mode == Mode.EditBind) { /* 改绑中不录 */ }
                else StartRecording();
                e.Handled = true;
                break;

            case Key.Enter:
                if (_mode == Mode.Candidate) ConfirmBind();
                else if (_mode == Mode.EditBind) SaveEditBind();
                else if (_mode == Mode.Playing && _modeBeforePlay == Mode.Candidate && _pendingTake != null)
                {
                    StopPlayback();
                    ConfirmBind();
                }
                e.Handled = true;
                break;

            case Key.R:
                if (_mode != Mode.Recording) Redo();
                e.Handled = true;
                break;

            case Key.P:
                if (_mode == Mode.Playing) StopPlayback();
                else if (_mode != Mode.Recording) PlayActive();
                e.Handled = true;
                break;

            case Key.Escape:
                if (_armDeleteId.Length > 0) { DisarmDelete(); e.Handled = true; break; }
                if (_mode == Mode.Recording) StopRecording(discard: true);
                else if (_mode == Mode.Playing) StopPlayback();
                else DiscardCurrent();
                e.Handled = true;
                break;

            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                UndoLast();
                e.Handled = true;
                break;

            // ★ Delete 键 = 删掉当前选中的那条（同样按两下；Esc 可以取消这次删除）
            case Key.Delete:
                if (_mode == Mode.Idle && _activeTake != null) OnDeleteClicked(_activeTake);
                e.Handled = true;
                break;

            case Key.Down:
                if (_mode is Mode.Idle) MoveCursor(1);
                e.Handled = true;
                break;

            case Key.Up:
                if (_mode is Mode.Idle) MoveCursor(-1);
                e.Handled = true;
                break;

            default:
                base.OnPreviewKeyDown(e);
                break;
        }
    }

    private void MoveCursor(int delta)
    {
        if (_doc.IsEmpty || _mode != Mode.Idle) return;

        int n = _doc.Sentences.Count;
        int next = Math.Clamp(_cursor + delta, 0, n - 1);

        _cursor = next;
        _selA = _selB = next;

        var take = TakeAt(next);
        _activeTake = take is null ? null : take;

        PaintAll();
        if (take != null) ShowTakeWave(take);
        else ShowEmptyWave();
        ScrollTo(next);
    }
}
