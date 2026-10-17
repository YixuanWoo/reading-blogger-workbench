// ============================================================
// RecordingDeskView.xaml.cs - 录音台（V1.16.0）
// ============================================================
//
// 【这一页只做一件事：把"声音"和"稿子里的哪几句"配上对】
//
//   用户要的用法（2026-10-17 亲口）：
//     "我希望能够手动选择每一段录制哪部分……也有可能，我先录音，
//      然后录到一整段我觉得合适的位置停，然后我再手工选择我刚才录制的对应文案。"
//
//   所以这里**没有"段落"这个对象**：
//     · 录音 = 一次空格到空格（takes\t001.wav）
//     · 绑定 = 文案里的句子区间（第 12–14 句，可跨自然段）
//     · 两者当场配对，配错了点一下改过来
//   程序不预先切片、不规定句数，也不在录音里动刀（不做裁剪、不做拼接）。
//
// 【状态机】
//   Idle ──空格──► Recording ──空格──► Candidate（等回车）
//                                        │
//                                 回车 ──┴──► Idle（光标跳到下一句）
//   Candidate/Idle ──P──► Playing ──播完──► 回到进来之前的状态
//
// 【为什么不自动开始下一段】
//   录完一段要听、要想、要喘口气。自动往下录会把"人决定节奏"变成"机器赶进度"，
//   而这个工具存在的理由恰恰是"当场听、当场重录"。所以确认之后停在 Idle，
//   光标已经挪到下一句 —— 想继续，自己按空格。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiteCut.Workbench.Diagnostics;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.RecordingDesk;

// ★ 别名：这个文件同时用到 System.IO 和 System.Windows.Shapes，
//   两边的 Path 会撞名；Shapes 是 XAML 带进来的，这里钉死用 IO 的那个。
using Path = System.IO.Path;

namespace LiteCut.Workbench.Views;

/// <summary>录音台：左栏选文案、右侧录、录完当场配对。</summary>
public partial class RecordingDeskView : UserControl, INavPage
{
    // ========================================================
    // 状态
    // ========================================================

    private enum Mode { Idle, Recording, Playing, Candidate, Done }

    private Mode _mode = Mode.Idle;

    private string _projectDir = "";
    private ScriptDocument _doc = new();
    private RecordingStore? _store;
    private readonly RecorderEngine _engine = new();

    /// <summary>句子号 → 句子上的超链接（改颜色/背景用）。</summary>
    private readonly Dictionary<int, Hyperlink> _links = new();

    /// <summary>段落下标 → "第几段"（章节不算，从 1 数）。</summary>
    private readonly Dictionary<int, int> _paraNo = new();

    /// <summary>句子号 → 已采用它的录音编号。</summary>
    private Dictionary<int, string> _done = new();

    private int _cursor;
    private int? _selA, _selB;          // 选区（句子号，含两端）
    private int? _pendingA, _pendingB;  // 开始录那一刻的选区（决定绑到哪里）
    private bool _rangeWasGuessed;

    private RecordingTake? _activeTake;  // 当前这条（刚录完 / 点中的）
    private string _recId = "";
    private string _recPath = "";

    private Mode _modeBeforePlay = Mode.Idle;
    private bool _suppressPlaybackEnd;

    /// <summary>
    /// 正在灌数据（读项目 / 填开关 / 选设备）。
    /// 这期间控件会自己触发 Checked/SelectionChanged —— 那些不是用户操作，
    /// 不能因为"打开页面"就去写一次 record.json（会平白建出文件夹和台账）。
    /// </summary>
    private bool _loading;

    private readonly DispatcherTimer _uiTimer;
    private readonly Dictionary<string, float[]> _peakCache = new();

    // ========================================================
    // 配色（和 XAML 里的一致；代码里改状态色要两边一起看）
    // ========================================================

    private static readonly Brush Transparent = Brushes.Transparent;
    private static readonly Brush InkNormal = Freeze(0xB9, 0xC1, 0xCC);
    private static readonly Brush InkDoneBg = Freeze(0x15, 0x22, 0x19);
    private static readonly Brush InkDoneFg = Freeze(0xA9, 0xC9, 0xB6);
    private static readonly Brush InkSelBg = Freeze(0x1B, 0x33, 0x50);
    private static readonly Brush InkSelFg = Freeze(0xCF, 0xE4, 0xF8);
    private static readonly Brush InkCandBg = Freeze(0x1C, 0x30, 0x47);
    private static readonly Brush InkCandFg = Freeze(0xD5, 0xE7, 0xF7);
    private static readonly Brush InkCurBg = Freeze(0x2B, 0x24, 0x15);
    private static readonly Brush InkCurFg = Freeze(0xF6, 0xEC, 0xD6);

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

        // 同一个项目、稿子没动过 → 不重读（保住光标和选区）
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

        // ★ 这段是"灌数据"，控件自己触发的 Checked/SelectionChanged 不算用户操作，
        //   期间一律不写盘（否则光是打开这一页就会建出 素材\录音\ 和 record.json）
        _loading = true;
        try
        {
            // 开关状态跟着项目走
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

            BuildScriptPanel();
            RebuildDoneMap();
            RefreshDevices();
        }
        finally
        {
            _loading = false;
        }

        // 光标落在第一句还没录的地方（全部录完就回到开头）
        _cursor = 0;
        for (int i = 0; i < _doc.Sentences.Count; i++)
        {
            if (!_done.ContainsKey(i)) { _cursor = i; break; }
        }

        _selA = _selB = null;
        _activeTake = _store.Data.Takes.LastOrDefault(t => !t.Dead);
        SetMode(Mode.Idle);

        if (_activeTake != null) ShowTakeWave(_activeTake);
        else ShowEmptyWave();

        if (!string.IsNullOrEmpty(_store.Data.ScriptStamp) &&
            _store.Data.ScriptStamp != _doc.Stamp)
        {
            SetVerdict("y", "稿子改过了", "句子可能重排过 —— 老录音的范围对不上，建议检查一下");
        }
        else
        {
            SetVerdict("", "", "左栏点一句（Shift 点可跨段选）→ 空格开始录");
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
        TakeRow.Children.Clear();
    }

    // ========================================================
    // 左栏：稿子
    // ========================================================

    private void BuildScriptPanel()
    {
        ScriptPanel.Children.Clear();
        _links.Clear();
        _paraNo.Clear();

        int paraCounter = 0;

        foreach (var para in _doc.Paras)
        {
            if (para.IsChapter)
            {
                var head = new Border
                {
                    BorderBrush = Freeze(0x22, 0x27, 0x2F),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(3, 0, 3, 6),
                    Margin = new Thickness(0, 16, 0, 7),
                    Child = new TextBlock
                    {
                        Text = para.Chapter,
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = Freeze(0xC5, 0xBC, 0xA6),
                    },
                };
                ScriptPanel.Children.Add(head);
                continue;
            }

            paraCounter++;
            int paraNo = paraCounter;
            foreach (var s in para.Sentences) _paraNo[s.Para] = paraNo;

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Freeze(0x4A, 0x52, 0x5E),
                Margin = new Thickness(0, 7, 7, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                Opacity = 0.25,
                ToolTip = "选中整段（第 " + paraNo + " 段）",
            };
            dot.MouseEnter += (o, _) => { if (o is Ellipse el) el.Opacity = 0.95; };
            dot.MouseLeave += (o, _) => { if (o is Ellipse el) el.Opacity = 0.25; };
            dot.MouseLeftButtonDown += (_, e) => { SelectParagraph(para); e.Handled = true; };

            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 23,
                Foreground = InkNormal,
            };

            foreach (var s in para.Sentences)
            {
                var link = new Hyperlink(new Run(s.Text))
                {
                    Tag = s.Index,
                    TextDecorations = null,
                    Cursor = Cursors.Hand,
                    Foreground = InkNormal,
                    Background = Transparent,
                };
                link.Click += Sentence_Click;
                text.Inlines.Add(link);
                _links[s.Index] = link;
            }

            var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);

            ScriptPanel.Children.Add(row);
        }

        PaintDoc();
    }

    /// <summary>按"当前句 / 选区 / 已录"三件事给每句话上色。</summary>
    private void PaintDoc()
    {
        int lo = 0, hi = -1;
        if (_selA.HasValue && _selB.HasValue)
        {
            lo = Math.Min(_selA.Value, _selB.Value);
            hi = Math.Max(_selA.Value, _selB.Value);
        }

        foreach (var kv in _links)
        {
            int idx = kv.Key;
            var link = kv.Value;

            bool isCur = idx == _cursor && (_mode == Mode.Idle || _mode == Mode.Recording);
            bool inSel = hi >= lo && idx >= lo && idx <= hi;

            link.FontWeight = FontWeights.Normal;
            link.Background = Transparent;
            link.Foreground = InkNormal;

            if (isCur)
            {
                link.Background = InkCurBg;
                link.Foreground = InkCurFg;
                link.FontWeight = FontWeights.SemiBold;
            }
            else if (inSel)
            {
                link.Background = _mode == Mode.Candidate ? InkCandBg : InkSelBg;
                link.Foreground = _mode == Mode.Candidate ? InkCandFg : InkSelFg;
            }
            else if (_done.ContainsKey(idx))
            {
                link.Background = InkDoneBg;
                link.Foreground = InkDoneFg;
            }
        }

        UpdateDocStat();
    }

    private void UpdateDocStat()
    {
        if (TxtDocStat == null) return;
        TxtDocStat.Text = $"{_done.Count} / {_doc.Sentences.Count} 句";
    }

    // ========================================================
    // 选区
    // ========================================================

    private void Sentence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink link || link.Tag is not int idx) return;

        // ★ 录音中、回放中都不理会：回放还没结束就点句子，会把"看完这条"的
        //   手滑变成"改了绑定范围"—— 等半秒再点，别抢戏。
        if (_mode == Mode.Recording || _mode == Mode.Playing) return;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (_mode == Mode.Candidate && _activeTake != null)
        {
            // 候选态：普通点 = 改终点；Shift 点 = 改起点
            if (shift && _selA.HasValue) _selA = idx;
            else _selB = idx;

            _rangeWasGuessed = false;
            ApplyCandidateRange();
            return;
        }

        if (shift && _selA.HasValue)
        {
            _selB = idx;
            _cursor = Math.Min(_selA.Value, idx);
        }
        else
        {
            _selA = idx;
            _selB = idx;
            _cursor = idx;
        }

        if (_mode == Mode.Done) _mode = Mode.Idle;

        PaintDoc();
        PaintScript();
        UpdateButtons();
        ScrollTo(_cursor);
    }

    private void SelectParagraph(ScriptPara para)
    {
        if (para.Sentences.Count == 0 || _mode == Mode.Recording || _mode == Mode.Playing) return;

        _selA = para.Sentences[0].Index;
        _selB = para.Sentences[^1].Index;
        _cursor = _selA.Value;

        if (_mode == Mode.Candidate && _activeTake != null)
        {
            _rangeWasGuessed = false;
            ApplyCandidateRange();
            return;
        }

        if (_mode == Mode.Done) _mode = Mode.Idle;

        PaintDoc();
        PaintScript();
        UpdateButtons();
        ScrollTo(_cursor);
    }

    /// <summary>候选态下改了范围 → 立刻重绑（旧的重叠录音自动作废）。</summary>
    private void ApplyCandidateRange()
    {
        if (_activeTake == null || !_selA.HasValue || !_selB.HasValue) return;

        _activeTake.A = Math.Min(_selA.Value, _selB.Value);
        _activeTake.B = Math.Max(_selA.Value, _selB.Value);
        _cursor = _activeTake.A;

        _store!.Activate(_activeTake);
        _store.Save();
        RebuildDoneMap();

        PaintDoc();
        PaintScript();
        UpdateButtons();
        BuildTakeRow();
        UpdateVerdict(_activeTake);
        ScrollTo(_cursor);
    }

    private void BtnClearSel_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Recording || _mode == Mode.Candidate) return;
        _selA = _selB = null;
        PaintDoc();
        PaintScript();
        UpdateButtons();
        SetVerdict("", "", "已清除选择 —— 直接按空格就录，录完按语速猜范围");
    }

    // ========================================================
    // 提词区
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
            // 没选区：从光标起显示到本段末尾（最多 4 句），念的时候有上下文
            lo = Math.Clamp(_cursor, 0, n - 1);
            int para = _doc.Sentences[lo].Para;
            hi = lo;
            while (hi + 1 < n && _doc.Sentences[hi + 1].Para == para && hi - lo < 3) hi++;
            if (hi == lo && hi + 1 < n) hi++;
        }

        string text = _doc.TextBetween(lo, hi);
        int chars = ScriptDocument.CountChars(text);

        TxtCur.Text = text;
        TxtCur.FontSize = chars <= 60 ? 31 : chars <= 100 ? 28 : chars <= 150 ? 25 : 22;
        TxtCur.LineHeight = TxtCur.FontSize * 1.75;

        TxtPrev.Text = lo > 0 ? _doc.Sentences[lo - 1].Text : "";
        TxtNext.Text = hi < n - 1 ? _doc.Sentences[hi + 1].Text : "";
        TxtPrev.Visibility = TxtPrev.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtNext.Visibility = TxtNext.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 顶部信息行
        int paraNo = _paraNo.TryGetValue(_doc.Sentences[lo].Para, out int pn) ? pn : 1;
        string range = lo == hi ? $"第 {lo + 1} 句" : $"第 {lo + 1}–{hi + 1} 句";
        TxtSegNow.Text = $"段{paraNo} · {range}";

        double rate = _store?.LearnedRate(_doc) ?? 4.3;
        int est = Math.Max(2, (int)Math.Round(chars / rate));
        TxtSegMeta.Text = $"{chars} 字 · 约 {est} 秒";

        // 状态徽章
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
                SetBadge(_rangeWasGuessed ? "猜的范围 · 可改" : "待确认 · 可改范围", TagBlueFg, TagBlueBg);
                CurBar.Background = BarBlue;
                break;
            case Mode.Done:
                SetBadge("已绑定", TagGreenFg, TagGreenBg);
                CurBar.Background = BarGreen;
                break;
            default:
                if (_selA.HasValue) SetBadge("已选中 · 待录", TagBlueFg, TagBlueBg);
                else SetBadge("自动 · 从这句往下念", Freeze(0x8B, 0x93, 0xA1), Freeze(0x23, 0x2A, 0x33));
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
        if (_links.TryGetValue(index, out var link))
        {
            try { link.BringIntoView(); } catch { }
        }
    }

    // ========================================================
    // 模式 / 按钮
    // ========================================================

    private void SetMode(Mode m)
    {
        _mode = m;
        UpdateButtons();
        PaintDoc();
        PaintScript();
        BuildTakeRow();

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

        // 主按钮：只有三个身份（开始 / 停止 / 重录这一条）
        if (rec)
        {
            TxtMainDot.Text = "■";
            TxtMainLabel.Text = "停止";
            BtnMain.Background = BarRec;
        }
        else
        {
            TxtMainDot.Text = "●";
            TxtMainLabel.Text = _mode == Mode.Candidate ? "重录这一条" : "开始录音";
            BtnMain.Background = Freeze(0xC7, 0x44, 0x3A);
        }

        bool hasTake = _activeTake != null && File.Exists(TakePath(_activeTake));

        BtnPlay.IsEnabled = !rec && !play && hasTake;
        BtnRedo.IsEnabled = !rec && !play && (hasTake || _selA.HasValue);
        BtnOk.IsEnabled = _mode == Mode.Candidate && _activeTake != null;
        BtnClearSel.IsEnabled = !rec && !play && _mode != Mode.Candidate && _selA.HasValue;

        RecDot.Fill = rec ? BarRec : BarIdle;

        if (rec)
        {
            TxtTime.Text = Fmt(_engine.ElapsedSec);
        }
        else if (play)
        {
            TxtTime.Text = Fmt(_engine.PlayPositionSec);
        }
        else
        {
            TxtTime.Text = _activeTake != null ? Fmt(_activeTake.DurSec) : "00:00.0";
        }
    }

    // ========================================================
    // 录音
    // ========================================================

    private void StartRecording()
    {
        if (_doc.IsEmpty || _store == null) return;
        if (_engine.IsRecording) return;

        if (CmbDevice.SelectedItem is not AudioInputDevice dev)
        {
            SetVerdict("r", "没有麦克风", "先连上手机 / 插上麦克风，再点右上角「刷新」");
            return;
        }

        // ★ 有选区 → 就按选区绑；没选区 → 录完按语速猜
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
            if (!quiet) SetVerdict("y", "这条太短", discard ? "已丢弃" : "不到 0.3 秒，已丢弃 —— 按空格重来");
            DebugLog.Write("record", "录音取消 " + _recId, new { dur });
            return;
        }

        // 以文件实际长度为准（设备时钟和 Stopwatch 会有毫秒级差）
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

        // ---- 绑定范围 ----
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
            // 关掉"自动猜"又没有选区 → 只绑光标这一句（宁窄不宽，改起来快）
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

        _store!.Activate(take);
        _store.Data.Takes.Add(take);
        _store.Data.ScriptStamp = _doc.Stamp;
        _store.Save();

        _activeTake = take;
        _selA = a;
        _selB = b;
        _cursor = a;
        _pendingA = _pendingB = null;

        RebuildDoneMap();
        SetMode(Mode.Candidate);
        UpdateVerdict(take);
        Wave.Peaks = peaks;
        Wave.Dim = false;
        Wave.Progress = 1;
        Wave.ShowPlayhead = false;
        WaveLabel.Text = "录好了 · " + dur.ToString("F1") + " 秒";
        WaveTime.Text = Fmt(dur);
        ScrollTo(a);

        DebugLog.Write("record", "录完 " + take.Id, new
        {
            a, b, dur = take.DurSec, peakDb = take.PeakDb, guessed = _rangeWasGuessed,
        });

        if (ChkAutoPlay.IsChecked == true) PlayActive();
    }

    /// <summary>
    /// 没选区时猜范围：按"这条录音多长 × 学到的语速"从光标往后数句子。
    /// 猜出来的永远只是**初值** —— 界面上看得见，点一下就能改。
    /// </summary>
    private (int a, int b) GuessRange(double dur)
    {
        int n = _doc.Sentences.Count;
        int start = Math.Clamp(_cursor, 0, n - 1);

        // 光标处已经录过（回头补录）→ 从这一段的下一句没录的地方开始更合理
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
            SetVerdict("r", "这一条没声音", $"{take.PeakDb:F0} dB · 检查 WO Mic 客户端是不是连上了");
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
        WaveLabel.Text = "回放中 · " + _activeTake.DurSec.ToString("F1") + " 秒";
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
            ? (_modeBeforePlay == Mode.Candidate ? "录好了 · " : "已绑定 · ") + _activeTake.DurSec.ToString("F1") + " 秒"
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

            // 试听时电平表跟着声音跳（比录的时候低一档，别闪瞎眼）
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

        // 峰值太低 = 没声音 = 红；正常是绿
        MeterFill.Background = db < -45 ? BarRec : BarGreen;
    }

    // ========================================================
    // 确认绑定 / 重录
    // ========================================================

    private void ConfirmBind()
    {
        if (_mode != Mode.Candidate || _activeTake == null) return;

        int n = _doc.Sentences.Count;
        _cursor = Math.Min(n - 1, _activeTake.B + 1);
        _selA = _selB = null;
        _rangeWasGuessed = false;

        SetMode(Mode.Idle);
        PaintScript();
        ScrollTo(_cursor);

        if (_activeTake.B >= n - 1)
            SetVerdict("g", "全部录完了", $"{_done.Count} / {n} 句 —— 都绑好了");
        else
            SetVerdict("g", "绑好了", $"光标已跳到第 {_cursor + 1} 句，按空格接着录");

        SaveStore();
        DebugLog.Write("record", "确认绑定 " + _activeTake.Id, new { _activeTake.A, _activeTake.B, cursor = _cursor });
    }

    private void Redo()
    {
        if (_mode == Mode.Recording || _mode == Mode.Playing) return;

        int a, b;
        if (_mode == Mode.Candidate && _activeTake != null) { a = _activeTake.A; b = _activeTake.B; }
        else if (_selA.HasValue && _selB.HasValue) { a = Math.Min(_selA.Value, _selB.Value); b = Math.Max(_selA.Value, _selB.Value); }
        else if (_activeTake != null) { a = _activeTake.A; b = _activeTake.B; }
        else { a = _cursor; b = _cursor; }

        _selA = a;
        _selB = b;
        _cursor = a;

        PaintDoc();
        PaintScript();
        StartRecording();
    }

    // ========================================================
    // 录音台账（右下那条）
    // ========================================================

    private void BuildTakeRow()
    {
        if (TakeRow == null || _store == null) return;
        TakeRow.Children.Clear();

        if (_store.Data.Takes.Count == 0)
        {
            TakeRow.Children.Add(new TextBlock
            {
                Text = "还没录过 —— 选好句子，按空格开始",
                FontSize = 11.5,
                Foreground = Freeze(0x5F, 0x67, 0x72),
                Margin = new Thickness(2, 8, 0, 8),
            });
            return;
        }

        // 新的排前面（最近录的永远在第一眼位置）
        foreach (var t in _store.Data.Takes.AsEnumerable().Reverse())
        {
            bool active = _activeTake != null && _activeTake.Id == t.Id;

            var stack = new StackPanel();

            var line1 = new StackPanel { Orientation = Orientation.Horizontal };
            line1.Children.Add(new TextBlock
            {
                Text = t.Id,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 10,
                Foreground = Freeze(0x6E, 0x77, 0x83),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            line1.Children.Add(new TextBlock
            {
                Text = t.A == t.B ? $"第 {t.A + 1} 句" : $"第 {t.A + 1}–{t.B + 1} 句",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = Freeze(0xE6, 0xE9, 0xEF),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (t.Dead)
            {
                line1.Children.Add(new TextBlock
                {
                    Text = "已作废",
                    FontSize = 9.5,
                    Foreground = Freeze(0x8A, 0x6A, 0x2A),
                    Margin = new Thickness(7, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            stack.Children.Add(line1);

            stack.Children.Add(new TextBlock
            {
                Text = $"{t.SentenceCount} 句 · {t.DurSec:F1}s" + (t.PeakDb < -50 ? " · 静音" : ""),
                FontSize = 10.5,
                Foreground = Freeze(0x78, 0x82, 0x8F),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Margin = new Thickness(0, 3, 0, 0),
            });

            var card = new Border
            {
                Background = Freeze(0x16, 0x1A, 0x20),
                BorderBrush = active ? Freeze(0xD4, 0xAF, 0x37) : Freeze(0x26, 0x2C, 0x35),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 12, 7),
                Margin = new Thickness(0, 0, 7, 0),
                MinWidth = 168,
                Cursor = Cursors.Hand,
                Opacity = t.Dead ? 0.45 : 1.0,
                Child = stack,
                ToolTip = "点一下 = 在左栏点亮它绑的是哪几句\n双击 = 试听这一条",
                Tag = t,
            };

            card.MouseLeftButtonDown += TakeCard_Click;
            TakeRow.Children.Add(card);
        }
    }

    private void TakeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card || card.Tag is not RecordingTake take) return;
        if (_mode == Mode.Recording || _mode == Mode.Playing) return;

        if (e.ClickCount >= 2)
        {
            _activeTake = take;
            PlayActive();
            e.Handled = true;
            return;
        }

        ActivateTake(take);
        e.Handled = true;
    }

    private void ActivateTake(RecordingTake take)
    {
        // 作废的录音点一下 = 恢复它（跟它重叠的活录音自动作废）
        if (take.Dead)
        {
            _store!.Activate(take);
            _store.Save();
            RebuildDoneMap();
            DebugLog.Write("record", "恢复作废录音 " + take.Id, new { take.A, take.B });
        }

        _activeTake = take;
        _selA = take.A;
        _selB = take.B;
        _cursor = take.A;
        _rangeWasGuessed = false;

        SetMode(Mode.Idle);
        ShowTakeWave(take);
        PaintDoc();
        PaintScript();
        UpdateButtons();
        ScrollTo(_cursor);

        SetVerdict("b", "这一条", $"绑的是第 {take.A + 1}–{take.B + 1} 句 · 按空格会重录这一段，按 P 试听");
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
        WaveLabel.Text = "这一条 · " + take.DurSec.ToString("F1") + " 秒";
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
        if (_store == null || CmbDevice.SelectedItem is not AudioInputDevice dev) return;
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

    private void BtnReloadScript_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Recording) return;

        _projectDir = "";          // 强制重读
        EnsureLoaded();

        if (!_doc.IsEmpty)
            SetVerdict("", "", $"稿子已重读：{_doc.Sentences.Count} 句");
    }

    private void BtnGoScript_Click(object sender, RoutedEventArgs e)
    {
        WorkbenchWindow.FindOwner(this)?.GoTo(NavTree.Ids.Script);
    }

    // ========================================================
    // 主按钮 / 快捷键
    // ========================================================

    private void BtnMain_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Recording) StopRecording(false);
        else if (_mode == Mode.Playing) StopPlayback();
        else StartRecording();
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Playing) StopPlayback();
        else PlayActive();
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e) => Redo();

    private void BtnOk_Click(object sender, RoutedEventArgs e) => ConfirmBind();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // 焦点在下拉框里时，空格是"打开下拉"，别抢
        if (Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is ComboBoxItem)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                if (_mode == Mode.Recording) StopRecording(false);
                else if (_mode == Mode.Playing) StopPlayback();
                else StartRecording();
                e.Handled = true;
                break;

            case Key.Enter:
                if (_mode == Mode.Candidate) ConfirmBind();
                e.Handled = true;
                break;

            case Key.R:
                if (_mode != Mode.Recording && _mode != Mode.Playing) Redo();
                e.Handled = true;
                break;

            case Key.P:
                if (_mode == Mode.Playing) StopPlayback();
                else if (_mode != Mode.Recording) PlayActive();
                e.Handled = true;
                break;

            case Key.Escape:
                if (_mode == Mode.Recording) StopRecording(discard: true);
                else if (_mode == Mode.Playing) StopPlayback();
                e.Handled = true;
                break;

            case Key.Down:
                if (_mode != Mode.Recording) MoveCursor(1);
                e.Handled = true;
                break;

            case Key.Up:
                if (_mode != Mode.Recording) MoveCursor(-1);
                e.Handled = true;
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    private void MoveCursor(int delta)
    {
        if (_doc.IsEmpty) return;

        if (_mode == Mode.Candidate || _mode == Mode.Playing) return;
        // 候选态用鼠标改范围、回放中别抢 —— 方向键都不掺和

        int n = _doc.Sentences.Count;
        _cursor = Math.Clamp(_cursor + delta, 0, n - 1);
        _selA = _cursor;
        _selB = _cursor;

        PaintDoc();
        PaintScript();
        UpdateButtons();
        ScrollTo(_cursor);
    }

    // ========================================================
    // 杂项
    // ========================================================

    private void RebuildDoneMap()
    {
        _done = _store?.BuildDoneMap(_doc.Sentences.Count) ?? new Dictionary<int, string>();
        UpdateDocStat();
    }

    private void SaveStore()
    {
        if (_loading) return;          // 灌数据期间的"假事件"不落盘
        try { _store?.Save(); }
        catch (Exception ex) { DebugLog.Error("录音台：写 record.json 失败", ex); }
    }

    private static string Fmt(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        int m = (int)(sec / 60);
        double s = sec - m * 60;
        return $"{m:00}:{s:00.0}";
    }
}
