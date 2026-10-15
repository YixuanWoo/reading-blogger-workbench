// ============================================================
// Step2ShotView.xaml.cs - 第二步：分镜设计
// ============================================================
//
// 【这一步现在的样子（2026-09-29 重写）】
//
//   ① 先算**成片口播稿**：转写稿 ∩ 剪辑决策 = 剪完之后观众实际听到的内容。
//      纯算术（FinalScriptBuilder），不需要 AI。
//      没有转写稿才退回"文案稿 + 按字数占比"，并且界面写明"估算"。
//
//   ② 再把口播稿**切成镜头**（ShotSplitter）：
//      默认一句一镜（真实句时间）；可切"固定节奏"——每 N 秒换画面，
//      但切换点一律吸附到某句话说完之后，绝不切在半句话中间。
//
//   ③ 画面构思**交给 Agent**（AgentBridge）：
//      内置提示词（含"长镜头必须动起来"的规则）+ 你填的个性化要求 + 镜头表
//      一起发过去，回来一键回填到分镜表。
//      ★ 只回填分镜，绝不连带渲染 —— 设计感必须你审过才进下一步。
//
// 【为什么把"算稿"和"切镜头"分开两个按钮】
//   算稿是给你看的（确认剪完之后到底在说什么）；切镜头是给 Agent 用的。
//   中间留一步人工确认，是因为一旦稿子错了（比如转写稿对错了音频），
//   后面全白做 —— 而这一步只要扫一眼就能发现。
//
// 【Agent 调用的两条通道】
//   填了大模型接口就直连（进度条是真的在动）；没填就走任务包文件，
//   工作台盯着回包文件自动回填。界面上写法完全一样。
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;
using LiteCut.Workbench.Agent;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Ui;
using Microsoft.Win32;

namespace LiteCut.Workbench.Views;

public partial class Step2ShotView : UserControl
{
    private readonly WorkbenchState _s = WorkbenchWindow.State;

    /// <summary>
    /// 分镜卡的视图模型。
    ///
    /// ★ V1.1.0 / T-16 起盯的是**草稿**（<see cref="WorkbenchState.ShotDraft"/>），
    ///   不是已交接版（_s.Shots）。拆 / 合 / 改字全都只动草稿，
    ///   点了「应用到下游」才写回已交接版、才会动磁盘。
    /// </summary>
    private readonly List<ShotVm> _vms = new();

    /// <summary>口播稿每一行的视图模型（可编辑 + 播放高亮）。</summary>
    private readonly List<ScriptLineVm> _lineVms = new();

    // ========================================================
    // T-16：交接闸门相关
    // ========================================================

    /// <summary>当前选中的那一镜。卡片流没有内置选中概念，自己记一个。</summary>
    private ShotVm? _sel;

    /// <summary>
    /// 草稿改动的落盘防抖。
    /// 【为什么要防抖】打一个字就把 24KB 的 shotlist.json 重写一遍太浪费 ——
    ///   而且是在 UI 线程上写。攒 1.2 秒再写，中途继续打字只是把钟重新拨一遍。
    /// </summary>
    private DispatcherTimer? _saveTimer;

    /// <summary>
    /// 最近一次交接的迁移日志（「撤销这次交接」靠它把文件反向搬回去）。
    /// **只保留最近一次** —— 不做多步栈，复杂度不值。切页 / 换项目后失效。
    /// </summary>
    private ReindexLog? _lastCommitLog;

    /// <summary>
    /// 派单给 Agent 那一刻的镜头表指纹。回来时对不上 → 拒绝回填（并发保护）。
    /// 【为什么要它】Agent 可能要跑十几分钟；这期间你拆了镜头，
    ///   回包按镜号填就会**填错镜**。与其填错，不如不填并说清原因。
    /// </summary>
    private string _agentShotsFingerprint = "";

    /// <summary>水位：这次交接有多贵（L0 只有切分 / L1 设计已回填 / L2 已渲染 / L3 已出成片）。</summary>
    private ShotWatermark _watermark = ShotWatermark.L0;

    /// <summary>全部折叠开关（折叠后一屏能扫 20+ 镜，看整体节奏用）。</summary>
    private bool _allCollapsed;

    /// <summary>当前算出来的成片口播稿。</summary>
    private FinalScript? _script;

    /// <summary>当前切出来的镜头（还没配画面）。</summary>
    private List<ShotSpan> _spans = new();

    private CancellationTokenSource? _cts;
    private bool _busy;

    // ---- Agent 调用过程中的"活体检测" ----
    //   用户反馈（2026-09-29）："光看着那个滚动进度条毫无意义" —— 看不出
    //   是在推进还是卡死了。所以这里记三条东西：
    //     · _busyStartedAt  总共跑了多久
    //     · _lastProgressAt 最后一次有动静是几时（>30s 变黄，>60s 变红建议取消）
    //     · _agentLog       事件流水（带时间戳），一眼看清走到哪一步
    private DateTime _busyStartedAt;
    private DateTime _lastProgressAt;
    private string _lastProgressKey = "";
    private readonly List<string> _agentLog = new();
    private DispatcherTimer? _busyTimer;

    // ---- 试听 ----
    private readonly AudioEngine _engine = new();
    private TimelineProject? _playProject;
    private double _playheadSec;
    private DispatcherTimer? _peakTimer;
    private int _peakTries;

    /// <summary>歌词线当前停在第几句（-1 = 还没定位）。</summary>
    private int _currentLine = -1;

    public Step2ShotView()
    {
        InitializeComponent();
        ListShots.ItemsSource = _vms;
        ListScript.ItemsSource = _lineVms;

        Unloaded += (_, _) =>
        {
            // 页面是每次进导航重建的 —— 不 Dispose 会攒下一堆播放设备
            _peakTimer?.Stop();
            _saveTimer?.Stop();
            _engine.Pause();
            _engine.Dispose();
        };

        // ---- 恢复上次的样子 ----
        TxtRequirement.Text = _s.ShotRequirement;
        RbSentence.IsChecked = _s.ShotSplitMode == 0;
        RbPace.IsChecked = _s.ShotSplitMode == 1;
        RbMerge.IsChecked = _s.ShotSplitMode == 2;
        TxtPace.Text = _s.ShotPaceSec.ToString("0");
        SyncSplitModeUi();

        // ★ 草稿不存在（刚打开老项目 / 项目里只有已交接版）→ 从已交接版深拷贝一份。
        //   之后这一页改的全是草稿，直到你点「应用到下游」。
        _s.EnsureShotDraft();
        if (_s.Shots.Count > 0 || _s.ShotDraft.Count > 0) ReloadFromState();

        // 进页就算一次口播稿：这一步是纯算术，不值得让人再点一下
        if (_s.HasDecision) BuildScript();
        else RefreshHint();

        RefreshAgentInfo();
        RefreshSpec();
        RefreshButtons();
        RefreshShotDecisionUi();
    }

    private WorkbenchWindow? Win => WorkbenchWindow.FindOwner(this);

    /// <summary>
    /// 界面控件是不是都建好了。
    ///
    /// 【为什么要有这个判断】
    ///   XAML 是一边解析一边建控件的，而 `Text="6"`、`IsChecked="True"` 这类
    ///   写在 XAML 里的初始值**会在建到一半时就触发事件**。此时后面那些控件还不存在，
    ///   事件处理里一访问就 NullReference —— 而且它发生在 InitializeComponent 内部，
    ///   崩起来是整个页面起不来（2026-09-29 踩到）。
    ///   所以凡是会被 XAML 初始值触发的 handler，开头都要挡一下。
    /// </summary>
    private bool UiReady => TxtHint != null && RbPace != null && RbSentence != null && TxtPace != null;

    // ========================================================
    // 口播稿
    // ========================================================

    /// <summary>
    /// 算成片口播稿并显示。
    ///
    /// 【重算时改过的句子必须留下来】
    ///   转写稿有错字（豆包 ASR 实测一堆），所以这一栏是能改的。
    ///   而"重算"会重新从转写稿推一遍 —— 改过的句子要继承过来，
    ///   不然改了半天一点重算全没了，谁还敢改。
    ///   匹配口径：源起止时间都对齐（量化到 0.05 秒）的才算同一句。
    /// </summary>
    private void BuildScript()
    {
        if (!_s.HasDecision)
        {
            TxtScriptInfo.Text = "还没有剪辑决策 —— 先在第①步跑粗剪，或到精剪页点「应用到下游」";
            RefreshHint();
            return;
        }

        try
        {
            // 改过的句子由 WorkbenchState 那边继承（重算、重开项目都走同一条路），
            // 这里只管显示。
            _script = _s.BuildFinalScript();

            _lineVms.Clear();
            foreach (var l in _script.Lines)
                _lineVms.Add(new ScriptLineVm(l, OnLineEdited));

            RefreshScriptInfo();
            SaveStageFiles();
            PreparePlayback();
        }
        catch (Exception ex)
        {
            TxtScriptInfo.Text = "算稿失败：" + ex.Message;
        }

        RefreshHint();
        RefreshButtons();
    }

    private void BtnBuildScript_Click(object sender, RoutedEventArgs e) => BuildScript();

    /// <summary>改过一句之后：刷新字数那一行 + 落盘。</summary>
    private void OnLineEdited()
    {
        RefreshScriptInfo();
        SaveStageFiles();
    }

    private void RefreshScriptInfo()
    {
        if (_script == null) return;

        string src = _script.IsEstimate
            ? "⚠ 估算稿（没导入转写稿，按字数占比摊的时间）"
            : "✓ 精确稿（转写稿 ∩ 剪辑决策）";

        int edited = _script.Lines.Count(l => l.Edited);
        TxtScriptInfo.Text = $"{src} —— {_script.Lines.Count} 句 · {_script.CharCount} 字 · " +
                             WorkbenchState.FormatDuration(_script.TotalSec) +
                             (edited > 0 ? $" · {edited} 句手工改过" : " · 可直接改写错字");
    }

    // ========================================================
    // 试听：整条成片波形 + 歌词线
    // ========================================================
    //
    // 【为什么这一页要能听】
    //   分镜要回答"这 X 秒画面上放什么"。光看文字，很容易把"这句讲完了"
    //   和"这句讲到一半"搞混 —— 听一遍就清楚了。
    //   而且转写稿有错字时，边听边改是最快的校对方式。
    //
    // 【波形为什么是一整条】
    //   精剪页那条波形是编辑用的（一块一块能拖能裁）。这条是**对照用**的：
    //   成片从头到尾一笔画到底，中间不画块边界 —— 它的用处是
    //   "我听到哪儿了"和"点一下跳过去"。

    /// <summary>
    /// 造一个"整条成片"的时间轴项目，供试听和波形用。
    /// 决策里保留的每一段首尾相接排下去 —— 所以它是成片，不是原素材。
    /// </summary>
    private TimelineProject? BuildPlayProject()
    {
        // ★ 不要拿 HasSource（要求 Pcm 已解码）当门槛。
        //   从项目列表直接进分镜页时音频还没解码（解码是第①步做的），
        //   那时试听不该整个消失 —— 只要源文件还在就能拼。
        //   SourceTotalSec 只影响"裁切能拖到多远"，这一页不裁，给 0 也没事。
        //
        // ★ 2026-10-03 收口：本页当年是**手搓**了这个判断（`SourcePath 非空`）。
        //   结果⑤ 组接页那边各写各的、漏掉了同一条经验，按钮灰了半天（R-19）。
        //   现在统一用状态层的 `HasSourceFile`：一处定义、所有页共用，
        //   以后想改判据（比如再要求文件在）不会又只改一处。
        if (!_s.HasSourceFile || !_s.HasDecision) return null;

        var ranges = _s.KeepRanges();
        if (ranges.Count == 0) return null;

        var proj = new TimelineProject();
        double t = 0;
        double srcTotal = _s.Pcm?.DurationSec ?? 0;

        foreach (var r in ranges)
        {
            if (r.End - r.Start <= 1e-6) continue;
            proj.Clips.Add(new TimelineClip
            {
                SourcePath = _s.SourcePath,
                SourceName = _s.SourceFileName,
                SourceStartSec = r.Start,
                SourceEndSec = r.End,
                SourceTotalSec = srcTotal,
                TimelineStartSec = t,
            });
            t += r.End - r.Start;
        }

        if (proj.Clips.Count == 0) return null;
        proj.PlayheadSec = _playheadSec;
        return proj;
    }

    private void PreparePlayback()
    {
        _playProject = BuildPlayProject();
        if (_playProject == null)
        {
            WaveBar.SetPeaks(Array.Empty<float>(), 0);
            WaveBar.Placeholder = "还没有成片可听";
            TxtWaveInfo.Text = "";
            return;
        }

        double total = _playProject.TotalDurationSec;

        _engine.PositionChanged -= OnPlayPosition;
        _engine.PositionChanged += OnPlayPosition;
        _engine.PlaybackStopped -= OnPlayStopped;
        _engine.PlaybackStopped += OnPlayStopped;
        WaveBar.SeekRequested -= OnSeek;
        WaveBar.SeekRequested += OnSeek;

        WaveBar.Placeholder = $"正在拼成片音频（{WorkbenchState.FormatDuration(total)}）…";
        WaveBar.SetPeaks(Array.Empty<float>(), total);
        TxtWaveInfo.Text = $"{_playProject.Clips.Count} 段拼成 · 拖动可定位";
        UpdateTimeText();

        // 后台先拼好，拼完波形自己出现。
        // （现场拼整条要 1.5 秒，绝不能在界面线程上等它）
        _engine.Prewarm(_playProject);
        StartPeakPolling();
    }

    private void StartPeakPolling()
    {
        _peakTries = 0;
        if (_peakTimer == null)
        {
            _peakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _peakTimer.Tick += (_, _) => PollPeaks();
        }
        _peakTimer.Start();
    }

    /// <summary>每隔一会儿问一句"拼好了没"，拼好就把波形画上。</summary>
    private void PollPeaks()
    {
        if (_playProject == null) { _peakTimer?.Stop(); return; }

        int buckets = (int)Math.Clamp(WaveBar.ActualWidth, 80, 2000);
        if (_engine.TryGetPeaks(_playProject, buckets, out var peaks))
        {
            WaveBar.SetPeaks(peaks, _playProject.TotalDurationSec);
            TxtWaveInfo.Text = "成片波形 · 拖动可定位 · 点时间码跳到那句";
            _peakTimer?.Stop();
            return;
        }

        if (++_peakTries > 90)      // 等了 36 秒还没好，别再空转
        {
            _peakTimer?.Stop();
            WaveBar.Placeholder = "成片音频还没拼好 —— 点「试听」会现拼";
            WaveBar.InvalidateVisual();
        }
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_playProject == null)
        {
            Win?.Status("还没有成片可听 —— 先在第①步跑粗剪，或到精剪页点「应用到下游」");
            return;
        }

        if (_engine.IsPlaying)
        {
            _engine.Pause();
            BtnPlay.Content = "▶ 试听成片";
            return;
        }

        _playProject.PlayheadSec = _playheadSec;
        _engine.Play(_playProject);
        BtnPlay.Content = "⏸ 暂停";
    }

    private void OnPlayPosition(object? sender, PlaybackPositionEventArgs e)
    {
        _playheadSec = e.PositionSec;
        WaveBar.SetPlayhead(_playheadSec);
        UpdateTimeText();
        TrackLyric(_playheadSec, follow: true);
    }

    private void OnPlayStopped(object? sender, EventArgs e)
    {
        Ui(() => BtnPlay.Content = "▶ 试听成片");
    }

    /// <summary>波形条被点/拖，或点了某一句的时间码。</summary>
    private void OnSeek(double sec)
    {
        _playheadSec = sec;
        WaveBar.SetPlayhead(sec);
        UpdateTimeText();
        TrackLyric(sec, follow: _engine.IsPlaying);

        if (_engine.IsPlaying && _playProject != null)
        {
            // 暂停一下再播：走"复用设备、只挪读指针"那条快路，
            // 不会把整条音频重拼一遍。
            _engine.Pause();
            _playProject.PlayheadSec = sec;
            _engine.Play(_playProject);
        }
    }

    /// <summary>找 sec 落在第几句（-1 = 还没开始）。</summary>
    private int FindLine(double sec)
    {
        var lines = _script?.Lines;
        if (lines == null || lines.Count == 0) return -1;

        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int m = (lo + hi) / 2;
            if (lines[m].StartSec <= sec) { ans = m; lo = m + 1; }
            else hi = m - 1;
        }
        return ans;
    }

    /// <summary>
    /// 歌词线移到当前句。follow=true 时还要把当前句滚进视图里。
    ///
    /// 【为什么暂停时不滚】
    ///   暂停时用户要往上翻、要改字。这时候如果程序还抢着滚，
    ///   光标会被拽走，根本改不了 —— 所以只在播放中才自动跟随。
    /// </summary>
    private void TrackLyric(double sec, bool follow)
    {
        if (_script == null || _lineVms.Count == 0) return;

        int idx = FindLine(sec);
        if (idx != _currentLine)
        {
            if (_currentLine >= 0 && _currentLine < _lineVms.Count) _lineVms[_currentLine].IsCurrent = false;
            if (idx >= 0 && idx < _lineVms.Count) _lineVms[idx].IsCurrent = true;
            _currentLine = idx;
        }
        if (idx < 0) return;

        var c = ListScript.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
        if (c == null) return;      // 容器还没建出来（刚算完稿那一下）

        double contentY = c.TransformToVisual(ListScript).Transform(new Point(0, 0)).Y;
        double rowH = c.ActualHeight;

        if (follow)
        {
            double target = contentY - ScriptScroll.ActualHeight * 0.38 + rowH / 2;
            double max = Math.Max(0, ListScript.ActualHeight - ScriptScroll.ActualHeight);
            ScriptScroll.ScrollToVerticalOffset(Math.Clamp(target, 0, max));
        }

        // 线画在当前句的**底边**：不挡字，又能一眼看出"播到这句了"
        double viewY = contentY - ScriptScroll.VerticalOffset;
        LyricLine.Width = Math.Max(0, ScriptScroll.ActualWidth);
        Canvas.SetTop(LyricLine, viewY + rowH - 1);
    }

    private void UpdateTimeText()
    {
        double total = _playProject?.TotalDurationSec ?? 0;
        TxtTime.Text = $"{Fmt(_playheadSec)} / {Fmt(total)}";
    }

    private void RowTime_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ScriptLineVm vm)
        {
            OnSeek(vm.Model.StartSec + 0.02);
            Win?.Status($"跳到 {Fmt(vm.Model.StartSec)} —— 「{Trim(vm.Model.Text)}」");
        }
    }

    private void RowText_GotFocus(object sender, RoutedEventArgs e)
    {
        // 播放中点进去改字，光标会被歌词线带着跑 —— 先停下来
        if (_engine.IsPlaying)
        {
            _engine.Pause();
            BtnPlay.Content = "▶ 试听成片";
        }
    }

    private static string Trim(string s) => s.Length > 20 ? s[..20] + "…" : s;

    private static string Fmt(double sec)
    {
        int m = (int)(sec / 60);
        int s = (int)(sec - m * 60);
        return $"{m:D2}:{s:D2}";
    }

    // ========================================================
    // 成片规格（背景图 / 画幅 / 渲染方案）
    // ========================================================
    //
    // 【为什么"渲染"的规格要在"分镜"这一步就摆出来】
    //   分镜回答的是"这 X 秒画面上放什么"，而"放什么"合不合理，
    //   取决于画面最终铺在哪张底子上、拿什么技术渲染：
    //     · 底子是深蓝网格 → 设计成白底大色块就会像贴了张纸；
    //     · 渲染走 HyperFrames（网页技术）→ 设计实拍航拍就落不了地。
    //   这类错在分镜稿上完全看不出来，等渲染完才发现，返工最贵。
    //   所以规格属于渲染那一环，却必须在这一步就常驻可见、并写进提示词。

    /// <summary>
    /// 按当前全局配置算出这次成片的画面规格。
    /// 项目名/项目目录取当前打开的项目（没开项目就用素材文件名）——
    /// 规格里带着项目名、输出目录和项目夹，渲染那一环拿到就知道往哪写、用哪张底图。
    /// </summary>
    private RenderSpec BuildSpec()
    {
        var project = WorkbenchWindow.Session.Current;
        string name = project?.Name ?? Path.GetFileNameWithoutExtension(_s.SourcePath);
        if (string.IsNullOrWhiteSpace(name)) name = "untitled";

        // ★ OutputDir 传的是**渲染产物目录**（<项目夹>\渲染产物），不是 stages\04-render。
        //   2026-10-01 前这里传的是 project.RenderStageDir，那是渲染过程文件（任务包/日志）
        //   的目录 —— 渲染页读这份 spec 去探测 shot_xxx.mp4 就会指错地方（T-10）。
        //   同一份 render-spec.json，分镜页与渲染页必须写出同一个 OutputDir。
        return RenderSpec.FromConfig(_s.Config, name, project?.RenderOutputDir ?? "",
                                     project?.Dir ?? "");
    }

    /// <summary>
    /// 把规格显示出来，并顺手落一份 render-spec.json（在 SaveStageFiles 里）。
    /// 背景图找不到时明确警告 —— 否则渲染出来是一张纯色底，
    /// 而那时候人已经离开这一步了，很难往回追。
    /// </summary>
    private void RefreshSpec()
    {
        if (TxtSpec == null) return;

        var spec = BuildSpec();

        TxtSpec.Text = "成片规格：" + spec.Describe()
            + (spec.BackgroundExists
                ? ""
                : "   ⚠ 背景图找不到 —— 渲染会退成纯色底，点右边「换背景图…」指过去");

        TxtSpec.Foreground = (Brush?)TryFindResource(spec.BackgroundExists ? "TextFaint" : "Warn")
                             ?? Brushes.Gray;
    }

    /// <summary>换背景图。图会复制一份进项目自己的「素材\公共素材\」——
    /// 项目素材待在项目肚子里（2026-10-01 拍板），拷走项目就是完整的；
    /// 全局配置同时记住这张图，给还没素材的新项目兜底。</summary>
    private void BtnBackground_Click(object sender, RoutedEventArgs e)
    {
        var project = WorkbenchWindow.Session.Current;
        string initDir = project != null && Directory.Exists(project.AssetsCommonDir)
            ? project.AssetsCommonDir
            : (Directory.Exists(Path.GetDirectoryName(_s.Config.VideoBackgroundImage) ?? "")
                ? Path.GetDirectoryName(_s.Config.VideoBackgroundImage)!
                : "");
        var dlg = new OpenFileDialog
        {
            Title = "选成片背景图（会复制进项目素材）",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.webp|所有文件|*.*",
            InitialDirectory = initDir,
        };
        if (dlg.ShowDialog() != true) return;

        // 1) 复制进项目的素材夹（项目自包含的根）
        if (project != null)
        {
            try
            {
                Directory.CreateDirectory(project.AssetsCommonDir);
                string dst = Path.Combine(project.AssetsCommonDir, Path.GetFileName(dlg.FileName));
                if (!string.Equals(Path.GetFullPath(dst), Path.GetFullPath(dlg.FileName),
                                   StringComparison.OrdinalIgnoreCase))
                    File.Copy(dlg.FileName, dst, overwrite: true);
            }
            catch { /* 复制失败不致命 —— 规格里仍记录原图路径 */ }
        }

        // 2) 全局兜底配置也更新（新项目没有素材时用）
        _s.Config.VideoBackgroundImage = dlg.FileName;
        _s.Config.Save();

        RefreshSpec();
        SaveStageFiles();   // 规格落到本项目的 stages 下（这次渲染用的到底是哪张图，有据可查）
        Win?.Status("成片背景图已换成 " + Path.GetFileName(dlg.FileName));
    }

    /// <summary>打开现在铺的这张背景图，看一眼。</summary>
    private void BtnOpenBackground_Click(object sender, RoutedEventArgs e)
    {
        string p = _s.Config.VideoBackgroundImage;
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
        {
            MessageBox.Show("背景图不在：\n\n" + p + "\n\n点「换背景图…」重新指一张。",
                            "找不到背景图", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = p, UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Win?.Status("打不开：" + ex.Message);
        }
    }

    // ========================================================
    // 切镜头
    // ========================================================

    private void BtnGen_Click(object sender, RoutedEventArgs e)
    {
        if (!AutoSplit()) return;
        Win?.Status($"已切出 {_spans.Count} 个镜头 —— 下一步交给 Agent 设计画面");
    }

    /// <summary>
    /// 按当前切法把口播稿切成镜头。成功返回 true。
    /// 按钮点和"交给 Agent 前自动补切"走的是同一条路 —— 不写两份。
    /// </summary>
    private bool AutoSplit()
    {
        if (!_s.HasDecision)
        {
            Win?.Status("还没有剪辑决策 —— 先在第①步跑粗剪，或在精剪页点「应用到下游」");
            return false;
        }

        if (_script == null) BuildScript();
        if (_script == null || _script.Lines.Count == 0)
        {
            MessageBox.Show("口播稿是空的 —— 检查转写稿有没有导入、决策是不是有效。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var mode = RbMerge.IsChecked == true
            ? ShotSplitMode.SemanticMerge
            : (RbPace.IsChecked == true ? ShotSplitMode.FixedPace : ShotSplitMode.BySentence);
        double pace = ParsePace();
        var (min, max) = ParseRange();

        _spans = ShotSplitter.Split(_script, mode, pace, min, max);

        // ★ 重切之前先把老镜头按「源区间」建索引 —— 边界没变的镜头要**原样继承**。
        //
        //   【为什么必须继承】用户换个切法试试、或者手滑点了一次重切，
        //   如果所有镜头都发新 ID，那已经渲好的 51 个产物会全部被判成"没主了"、
        //   白白作废，逼着人重渲一遍 —— 而边界一样的镜头，画面**一帧都没变**。
        //   所以：源起止时间一样的 = 同一镜，ID / 设计 / 配图全部接过来。
        var old = _s.ShotDraft.Count > 0 ? _s.ShotDraft : _s.Shots;
        var byRange = new Dictionary<string, ShotRow>(StringComparer.Ordinal);
        foreach (var r in old)
            byRange[RangeKey(r.SourceStartSec, r.SourceEndSec)] = r;

        var fresh = new List<ShotRow>();
        int kept = 0;
        foreach (var sp in _spans)
        {
            var row = new ShotRow
            {
                Index = sp.Index,
                StartSec = sp.StartSec,
                EndSec = sp.EndSec,
                Text = sp.Text,
                SourceStartSec = sp.SourceStartSec,
                SourceEndSec = sp.SourceEndSec,
            };

            if (byRange.TryGetValue(RangeKey(sp.SourceStartSec, sp.SourceEndSec), out var keep)
                && !string.IsNullOrEmpty(keep.Id))
            {
                row.Id = keep.Id;
                row.Description = keep.Description;
                row.Motion = keep.Motion;
                row.Image = keep.Image;
                row.ImageFile = keep.ImageFile;
                row.Edited = keep.Edited;
                row.DesignStale = keep.DesignStale;
                kept++;
            }
            else
            {
                row.Id = _s.NextShotId();
            }
            fresh.Add(row);
        }

        // ★ 插片是用户**手动**放进来的，重切分不能把它冲掉 —— 按原位置插回去。
        //   【位置怎么定】用"原来排第几"，尽力而为。切完镜头数可能变了，
        //   所以夹在中间的那几段位置会有偏移 —— 但设计、素材、时长都还在，
        //   用户拖一下就归位了，总比凭空消失强。
        int inserts = 0;
        foreach (var (r, i) in old.Select((r, i) => (r, i)).Where(x => x.r.IsInsert).ToList())
        {
            fresh.Insert(Math.Min(i, fresh.Count), r);
            inserts++;
        }
        if (inserts > 0)
        {
            RetimeDraft();
            ReindexDraft();
        }

        // 镜头 → 草稿（★ 仍然只是草稿：下游要等你点「应用到下游」）
        _s.ShotDraft = fresh;
        _s.EnsureShotIds(_s.ShotDraft);
        _s.EnsureShotIds(_s.Shots);
        ClearSelAfterTableChange();

        ReloadFromState();
        SaveStageFiles();

        int longs = _spans.Count(x => x.IsLong);
        Win?.Status($"已切出 {_spans.Count} 个镜头（{kept} 镜边界没变、设计原样保留）"
                    + (longs > 0 ? $"，{longs} 个长镜头（≥12s，会要求 Agent 做多阶段动效）" : "")
                    + (inserts > 0 ? $"，{inserts} 段插片按原位置保留" : "")
                    + " —— 记得点「应用到下游」才算交下去");

        return true;
    }

    /// <summary>源区间的对账键（量化到 0.01 秒，躲开浮点尾巴）。</summary>
    private static string RangeKey(double start, double end) =>
        Math.Round(start, 2).ToString("F2") + "|" + Math.Round(end, 2).ToString("F2");

    // ========================================================
    // ★ 任务三：手动调边界（合并 / 拆分 / 重切分）
    //   纯本地、瞬成、不调 Agent。改过的镜头打 Edited，重切分前确认。
    // ========================================================

    // ========================================================
    // ★ T-16：拆 / 合镜头 + 交接闸门
    // ========================================================
    //
    // 【这一节的规矩，一句话说完】
    //   拆 / 合只改**草稿**（随便拆，不打扰）；点「应用到下游」才交接
    //   （那一刻才重排镜号、把产物改名、标「待重渲」）。
    //
    //   所以下面这些方法里**一个字都不碰磁盘** —— 也正因如此，
    //   "改内存不改磁盘 → 产物静默错位"那个坑是从根上关掉的，
    //   不是靠"迁移做得好"来解的（迁移只是把交接那一刻做干净）。
    //
    // 【摩擦放在哪】
    //   拆的那一刻：只问「设计怎么处理」（设计全空时不弹，兑现"随意拆分没问题"）；
    //   交接那一刻：才按水位摊代价（改多少文件 / 多少镜待重渲）。

    // ---- 选中：卡片流没有内置选中概念，自己维护一个 ----

    private void ShotCard_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        // 点在输入框 / 按钮上是在做别的事，别抢选中
        if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
        if (sender is FrameworkElement fe && fe.DataContext is ShotVm vm) Select(vm);
    }

    /// <summary>往上找几层，看是不是点在"能交互的东西"上（输入框 / 按钮）。</summary>
    private static bool IsInteractive(DependencyObject? d)
    {
        for (int i = 0; i < 14 && d != null; i++)
        {
            if (d is TextBox || d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Select(ShotVm? vm)
    {
        if (ReferenceEquals(_sel, vm)) return;
        if (_sel != null) _sel.IsSelected = false;
        _sel = vm;
        if (_sel != null) _sel.IsSelected = true;
        RefreshEditButtons();
    }

    private void CardMerge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShotVm vm) Select(vm);
        MergeSelected();
    }

    private void CardSplit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShotVm vm) Select(vm);
        ShowSplitMenu(sender as FrameworkElement);   // 在卡片上弹出切点菜单
    }

    private void BtnMerge_Click(object sender, RoutedEventArgs e) => MergeSelected();
    private void BtnSplit_Click(object sender, RoutedEventArgs e) => ShowSplitMenu(BtnSplit);

    private void BtnFold_Click(object sender, RoutedEventArgs e)
    {
        _allCollapsed = !_allCollapsed;
        BtnFold.Content = _allCollapsed ? "全部展开" : "全部折叠";
        foreach (var v in _vms) v.IsCollapsed = _allCollapsed;
    }

    private void CardFold_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShotVm vm) vm.IsCollapsed = !vm.IsCollapsed;
    }

    private void CardConfirmDesign_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ShotVm vm) return;
        vm.Model.DesignStale = false;

        // ★ 这里必须调**页面级**的 RefreshBadges()（它会把新的徽章文字
        //   重新算一遍灌进每张卡：SetDesignBadge）。合并 / 拆分之所以没这毛病，
        //   是因为它们走了 ReloadFromState()，顺带就调了这个。
        //
        //   ⚠ 曾经这里写的是 vm.RefreshBadges() —— 那是 VM 上的**同名**方法，
        //     只重发 tooltip / 按钮可见性，**徽章文字根本不重算**。后果：
        //     点「✎确认无误」后按钮消失了、「待复核」却还挂在卡上
        //     （2026-10-01 用户在 GUI 实点里抓到）。VM 那个已改名 RaiseBadgeVisibility()，
        //     就是为了让这个坑不再有第二次。
        RefreshBadges();

        MarkDraftTouched("已确认 #" + vm.Model.Index + " 的设计，摘掉「待复核」");
    }

    /// <summary>点进输入框要先把播放停下来 —— 不然歌词线会把光标拽走。</summary>
    private void CardBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_engine.IsPlaying)
        {
            _engine.Pause();
            BtnPlay.Content = "▶ 试听成片";
        }
    }

    // ---- 拆 / 合（只改草稿） ----

    private ShotRow? SelRow()
    {
        if (_sel == null) return null;
        return _s.ShotDraft.FirstOrDefault(r => ReferenceEquals(r, _sel.Model))
               ?? _s.ShotDraft.FirstOrDefault(r => !string.IsNullOrEmpty(r.Id) && r.Id == _sel.Model.Id);
    }

    private void MergeSelected()
    {
        if (_busy) { Win?.Status("Agent 正在跑 —— 等这次设计跑完再调镜头（不然回填会填错镜）"); return; }

        var a = SelRow();
        if (a == null) { Win?.Status("先在分镜卡片上点一下要合并的那一镜"); return; }

        int pos = _s.ShotDraft.IndexOf(a);
        if (pos < 0 || pos + 1 >= _s.ShotDraft.Count) { Win?.Status("只能合并有「下一镜」的镜头"); return; }

        var b = _s.ShotDraft[pos + 1];

        // ★ 被合并掉的那一镜的设计**不能静默丢失**（现状就是这么丢的）。
        //   默认两段拼接保留：合并后这一镜的内容确实就是 a、b 两段连续的，
        //   所以 a 的设计 + b 的设计拼起来刚好覆盖新范围。
        bool hasDesign = HasAnyDesign(a) || HasAnyDesign(b);
        if (hasDesign)
        {
            var preview = new List<string>
            {
                $"#{a.Index}  {a.DurationSec + b.DurationSec:F1}s"
                    + $"（{a.StartSec:F1} → {b.EndSec:F1}s）  合并后只剩这一镜",
                $"#{b.Index}  原设计不会丢，拼进合并后的第 #{a.Index} 镜",
                $"合并后这一镜会标「待复核」—— 你确认过再点卡上的「✎确认无误」",
            };

            var choice = ShotDialogs.AskDesignChange(
                Window.GetWindow(this), $"把第 {a.Index}、{b.Index} 镜合成一镜？", preview, isSplit: false);
            if (choice == DesignChoice.Cancel) return;

            if (choice == DesignChoice.Clear)
            {
                a.Description = a.Motion = a.Image = "";
            }
            else
            {
                a.Description = Join(a.Description, b.Description);
                a.Motion = Join(a.Motion, b.Motion);
                a.Image = Join(a.Image, b.Image);
            }
        }

        a.Text = (a.Text + " " + b.Text).Trim();
        a.EndSec = b.EndSec;
        a.SourceEndSec = b.SourceEndSec;
        a.Edited = true;

        // ★ 「待复核」的含义是"有设计、但边界变了、可能对不上了"。
        //   所以判的是**合并后这一镜还剩不剩设计**：
        //     拼接保留 → 有设计 → 待复核 ✓
        //     选了清空 → 没设计 → 该是「待补设计」，不是「待复核」
        //     本来两镜都没设计 → 同样该是「待补设计」
        //   （以前无条件写 true → 没设计的镜也挂「待复核」，点了「✎确认无误」还摘不掉）
        a.DesignStale = HasAnyDesign(a);
        b.MergedInto = a.Id;          // 血缘：它是被谁吃掉的（留痕用，不再出现在镜头表里）

        _s.ShotDraft.RemoveAt(pos + 1);

        ReindexDraft();
        ClearSelAfterTableChange();
        ReloadFromState();
        MarkDraftTouched($"已把第 {a.Index} 镜与下一镜合并（只在草稿里 —— 点「应用到下游」才算交下去）");
    }

    /// <summary>
    /// 点「拆分」= **列出一镜之内所有可切的句末**，点哪个就在哪切。
    ///
    /// 【为什么改成菜单（2026-10-01 用户拍板）】
    ///   ① 原来只给"从中间拆"一个动作，还得先过一道设计弹窗 —— 用户原话：
    ///      「拆镜头的选项为什么要给我弹开一个弹窗，这个交互也很不舒服」。
    ///   ② 更要命的是**切点是程序猜的，而猜错是必然的**：只要这一镜里有一句
    ///      "起点在中点之前、却一直说到镜尾"的长句（例：`[8.32→10.20]`，块 `[7.13→10.20]`），
    ///      旧算式 `linesIn.Last(l => l.StartSec <= mid)` 就会选中它 →
    ///      切点 = 镜尾 → 后半镜长度 0、台词为空、前半镜一个字没少。
    ///      用户实点抓到，磁盘上留下 4 个零长空镜（s63/s64/s66/s67）。
    ///   ③ 镜头的最小单位是**转写好的句子**（句子自带起止时间），所以"切在哪"
    ///      本来就该让用户点 —— 候选点全是句末，怎么点都拆不坏。
    /// </summary>
    private void ShowSplitMenu(FrameworkElement? anchor)
    {
        if (_busy) { Win?.Status("Agent 正在跑 —— 等这次设计跑完再调镜头（不然回填会填错镜）"); return; }

        var block = SelRow();
        if (block == null) { Win?.Status("先在分镜卡片上点一下要拆的那一镜"); return; }
        if (_script == null) { Win?.Status("还没有成片口播稿"); return; }

        var linesIn = LinesIn(block);
        if (linesIn.Count <= 1)
        {
            Win?.Status("这一镜只有一句，没法按句子拆 —— 用「重新自动切分」换种切法");
            return;
        }

        double mid = (block.StartSec + block.EndSec) / 2;
        // ★★ 候选 = **除最后一句外**的每个句末。必须排除最后一句 ——
        //    切在它后面就是切在镜尾，会拆出一个零长度的空镜（就是上面那个 bug）。
        var cands = linesIn.Take(linesIn.Count - 1).ToList();
        var best = cands.OrderBy(l => Math.Abs(l.EndSec - mid)).FirstOrDefault();

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        menu.Items.Add(new MenuItem
        {
            Header = $"第 {block.Index} 镜 · {block.StartSec:F1} → {block.EndSec:F1}s · 共 {linesIn.Count} 句",
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());
        foreach (var l in cands)
        {
            var target = l;
            var mi = new MenuItem
            {
                Header = $"在「{Trim(target.Text)}」之后切　｜ 前半 {target.EndSec - block.StartSec:F1}s · 后半 {block.EndSec - target.EndSec:F1}s"
                         + (ReferenceEquals(target, best) ? "　◀ 推荐" : ""),
            };
            mi.Click += (_, _) => DoSplit(block, target);
            menu.Items.Add(mi);
        }
        if (HasAnyDesign(block))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "这一镜有设计 —— 前半保留它并标「待复核」",
                IsEnabled = false,
            });
        }
        menu.IsOpen = true;
    }

    /// <summary>落在这一镜时间区间里的句子（按成片时间）。</summary>
    private List<ScriptLine> LinesIn(ShotRow block) => _script == null
        ? new List<ScriptLine>()
        : _script.Lines
            .Where(l => l.StartSec >= block.StartSec - 1e-6 && l.EndSec <= block.EndSec + 1e-6)
            .ToList();

    /// <summary>
    /// 真正切开。切点由用户在菜单里点定，这里只落实。
    /// 设计默认**保留**（前半镜标「待复核」，摘不摘由用户说了算）。
    /// </summary>
    private void DoSplit(ShotRow block, ScriptLine splitLine)
    {
        var linesIn = LinesIn(block);
        double splitAt = splitLine.EndSec;
        if (splitAt >= block.EndSec - 1e-6)
        {
            // 兜底：理论上菜单已经把这种切点排除了，这里是最后一道闸。
            Win?.Status("这个切点在这一镜的末尾，拆出来会是个空镜 —— 换一个切点");
            return;
        }

        // 新镜：新 ID（身份永不复用），血缘记着从谁拆出来的
        var second = new ShotRow
        {
            Id = _s.NextShotId(),
            FromId = block.Id,
            Index = block.Index + 1,
            StartSec = splitAt,
            EndSec = block.EndSec,
            Text = string.Join(" ", linesIn.Where(l => l.StartSec > splitAt - 1e-6).Select(l => l.Text)),
            SourceStartSec = splitLine.SourceEndSec,
            SourceEndSec = block.SourceEndSec,
            Edited = true,
            // 后半镜是全新的、**没有设计**（设计不会自动劈成两半）→「待补设计」。
            DesignStale = false,
        };

        block.EndSec = splitAt;
        block.Text = string.Join(" ", linesIn.Where(l => l.EndSec <= splitAt + 1e-6).Select(l => l.Text));
        block.SourceEndSec = splitLine.SourceEndSec;
        block.Edited = true;
        // 前半镜：设计还在就「待复核」（它原本是按整镜写的）；没设计就是「待补设计」。
        block.DesignStale = HasAnyDesign(block);

        int pos = _s.ShotDraft.IndexOf(block);
        _s.ShotDraft.Insert(pos + 1, second);

        ReindexDraft();
        ClearSelAfterTableChange();
        ReloadFromState();
        MarkDraftTouched($"已把第 {block.Index} 镜拆成两镜（切在「{Trim(splitLine.Text)}」之后 · 只在草稿里，点「应用到下游」才算交下去）");
    }

    private static string Join(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + "\n" + b;
    }

    /// <summary>这一镜有没有任何设计内容（用来判水位 / 决定拆合时弹不弹框）。</summary>
    private static bool HasAnyDesign(ShotRow r) =>
        r.Description.Length > 0 || r.Motion.Length > 0 || r.Image.Length > 0;

    /// <summary>
    /// 把草稿的镜号理成 1..N。
    /// ★ 只改内存 —— **磁盘上的文件名一个字都不动**（那要等你点「应用到下游」）。
    /// </summary>
    private void ReindexDraft()
    {
        for (int k = 0; k < _s.ShotDraft.Count; k++) _s.ShotDraft[k].Index = k + 1;
    }

    // ========================================================
    // ★ 插片：手动插一段（没台词 → 成片里静音；时长自己定；素材可选）
    // ========================================================
    //
    // 【这一段为什么存在】
    //   口播片子里偶尔要插一段不是"说"出来的画面：一段街景、一段别人的视频、
    //   或者干脆就是几秒钟的静场。那一段里没有台词，所以成片必须是静音的 ——
    //   但**画面照常写分镜**，跟别的镜头一模一样（用户明确要求：别把插片搞成另一套东西）。
    //
    // 【三件事必须同时成立，否则片子根本导不出来】
    //   ① 画面：分镜表里多一行 → 画面轴 +N（CollectBlocks 天然就含它）
    //   ② 音频：组接时补一段等长静音 → 音轨 +N（见 Step4ExportView.CollectSegments）
    //   ③ 时间：插进去之后后面所有镜头顺延（RetimeDraft）
    //   ①②少任何一样，导出前的音画 drift 校验就会直接拒绝出片。
    //
    // 【为什么只改分镜表，不去动精剪时间轴】
    //   往时间轴里塞"空白块"会让口播稿凭空多出一句（时间轴会被当成口播保留区间
    //   去和转写稿求交），而且预览静音、导出却是真人人声 —— 详见 §5.19 的三重翻车。
    //   所以插片只写在**下游**，静音留到组接那一刻再生成。

    /// <summary>上次插入的时长（秒）。记住它 —— 连着插几段时不用每段都重新调。</summary>
    private double _lastInsertSec = 3.0;

    /// <summary>这条插入带是"插在第几镜之后"（0 = 插在片头）。<para/>
    /// 认不出来时返回 -1 —— 让调用方**报错停手**，而不是悄悄插到列表末尾。</summary>
    private int BandPosOf(FrameworkElement el)
    {
        var rows = _s.ShotDraft.Count > 0 ? _s.ShotDraft : _s.Shots;

        // ★ 顺着可视树往上找到那条带。
        //   右键时 sender 就是带本身，可以直接看 Tag；但**左键点 ⊕ 时 sender 是那个按钮**，
        //   它嵌在带里面、自己没有 Tag —— 以前正是在这里掉进"DataContext 不是 ShotVm"的兜底分支，
        //   于是片头那条带会把插片插到整个列表的末尾（2026-10-03 用户实测：在第一镜前插，插成了第 26 镜）。
        FrameworkElement band = el;
        if (!IsBand(band))
        {
            DependencyObject? cur = VisualTreeHelper.GetParent(band);
            while (cur != null)
            {
                if (cur is FrameworkElement f && IsBand(f)) { band = f; break; }
                cur = VisualTreeHelper.GetParent(cur);
            }
        }
        if (!IsBand(band)) return -1;

        if ((string)band.Tag! == "top") return 0;
        if (band.DataContext is ShotVm vm)
        {
            int k = rows.IndexOf(vm.Model);
            return k < 0 ? rows.Count : k + 1;
        }
        return -1;
    }

    /// <summary>这一层是不是插入带（认 Tag：top = 片头那条，after = 卡片之间那条）。</summary>
    private static bool IsBand(FrameworkElement fe) =>
        fe.Tag is string t && (t == "top" || t == "after");

    private void BandPlus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) TryInsertAt(BandPosOf(fe), null);
    }

    /// <summary>插入的统一入口：位置没认出来（&lt;0）就报错停手，绝不猜一个位置默默插下去。</summary>
    private void TryInsertAt(int pos, string? videoPath)
    {
        if (pos < 0) { Win?.Status("没认出要插在哪 —— 这一步先跳过，别插错位置。麻烦告诉我重现步骤。"); return; }
        InsertClipAt(pos, videoPath);
    }


    private void Band_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        int pos = BandPosOf(fe);

        var menu = new ContextMenu();
        var m1 = new MenuItem { Header = "在此插入插片" };
        m1.Click += (_, _) => TryInsertAt(pos, null);
        var m2 = new MenuItem { Header = "在此插入插片并选素材…" };
        m2.Click += (_, _) =>
        {
            string? p = PickVideoFile();
            if (p != null) TryInsertAt(pos, p);     // 取消就什么都不做，别插一段空的
        };
        menu.Items.Add(m1);
        menu.Items.Add(m2);
        fe.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CardPickVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ShotVm vm) return;
        string? p = PickVideoFile();
        if (p == null) return;

        string rel = ImportVideoAsset(p);
        if (rel.Length == 0)
        {
            Win?.Status("素材没能拷进项目 —— 看一下这个文件是不是正在被占用");
            return;
        }

        vm.Model.VideoFile = rel;
        vm.Model.VideoDurationSec = ProbeVideoDurationSec(p);

        // ★ 只在用户**没亲手定过**时长时才跟着素材走。他一旦定过，永远以他为准。
        if (!vm.Model.DurationPinned && vm.Model.VideoDurationSec > 0.2)
            vm.Model.EndSec = vm.Model.StartSec + Math.Round(vm.Model.VideoDurationSec, 2);

        RetimeDraft();
        foreach (var v in _vms) v.RaiseTime();
        MarkDraftTouched($"已换素材：{Path.GetFileName(rel)}");
    }

    private void CardDeleteInsert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ShotVm vm) return;
        if (!vm.Model.IsInsert) return;

        var rows = _s.ShotDraft;
        int k = rows.IndexOf(vm.Model);
        if (k < 0) return;

        rows.RemoveAt(k);
        RetimeDraft();
        ReindexDraft();
        ClearSelAfterTableChange();
        ReloadFromState();
        MarkDraftTouched($"已删掉那段插片（{vm.Model.DurationSec:F1}s）—— 后面的镜头自动接上");
    }

    private void CardClearVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ShotVm vm) return;
        vm.Model.VideoFile = "";
        vm.Model.VideoDurationSec = 0;
        vm.RaiseTime();
        MarkDraftTouched("已去掉素材 —— 这段现在是纯静音画面（分镜设计保留）");
    }

    /// <summary>
    /// 在 <paramref name="pos"/> 处插一段插片（0 = 片头）。
    /// </summary>
    /// <param name="videoPath">本地视频文件的完整路径；null = 不引用素材。</param>
    private void InsertClipAt(int pos, string? videoPath)
    {
        if (_busy) { Win?.Status("Agent 正在跑 —— 等这次设计跑完再插"); return; }
        if (WorkbenchWindow.Session.Current == null) { Win?.Status("还没打开项目"); return; }

        _s.EnsureShotDraft();
        var rows = _s.ShotDraft;
        if (rows.Count == 0)
        {
            Win?.Status("还没有镜头 —— 先点「生成镜头切分」再插片");
            return;
        }

        var row = new ShotRow
        {
            Id = _s.NextShotId(),
            IsInsert = true,
            StartSec = 0,
            EndSec = _lastInsertSec,

            // ★ 故意给 -1：插片没有源录音区间。用 0 会撞 RangeKey(0,0)，
            //   重切分时可能被别的镜头误继承（详见 ReindexDraft 附近那次踩坑）。
            SourceStartSec = -1,
            SourceEndSec = -1,
        };

        if (!string.IsNullOrEmpty(videoPath))
        {
            string rel = ImportVideoAsset(videoPath);
            if (rel.Length > 0)
            {
                row.VideoFile = rel;
                row.VideoDurationSec = ProbeVideoDurationSec(videoPath);
                if (row.VideoDurationSec > 0.2)
                {
                    row.EndSec = Math.Round(row.VideoDurationSec, 2);
                    _lastInsertSec = row.EndSec;
                }
            }
        }

        pos = Math.Max(0, Math.Min(pos, rows.Count));
        rows.Insert(pos, row);

        RetimeDraft();
        ReindexDraft();
        _s.EnsureShotIds(rows);

        ReloadFromState();

        // 新插的那一段：展开、选中、滚过去（别让人插完还要找它在哪）
        var vm = _vms.FirstOrDefault(v => v.Model == row);
        if (vm != null)
        {
            vm.IsCollapsed = false;
            Select(vm);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ListShots.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement c)
                    c.BringIntoView();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        MarkDraftTouched($"已插入一段插片（{row.DurationSec:F1}s · 静音）"
                         + (row.VideoFile.Length > 0 ? $"，素材：{Path.GetFileName(row.VideoFile)}" : "")
                         + " —— 记得点「应用到下游」");
    }

    /// <summary>
    /// 按"每段有多长"重排成片时间轴。
    /// ★ 只动 StartSec/EndSec（成片坐标），**源录音坐标一个字都不改** ——
    ///   所以口播和画面的对应关系还在，只是整体往后顺延。
    /// </summary>
    private void RetimeDraft()
    {
        double cursor = 0;
        foreach (var r in _s.ShotDraft)
        {
            double d = r.EndSec - r.StartSec;
            if (d < 0.05) d = 0.05;              // 兜底：绝不给 0 长度，组接会跳过它
            r.StartSec = cursor;
            r.EndSec = cursor + d;
            cursor += d;
        }
    }

    /// <summary>改了插片时长：顺延后面所有镜头 + 刷新卡头时间 + 落盘。</summary>
    private void OnInsertDurationChanged()
    {
        RetimeDraft();
        foreach (var v in _vms) v.RaiseTime();
        UpdateCounts();
        ScheduleSave();

        var ins = _s.ShotDraft.Where(r => r.IsInsert).ToList();
        if (ins.Count > 0) _lastInsertSec = ins[^1].DurationSec;
    }

    private static string? PickVideoFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选一段视频当插片素材",
            Filter = "视频|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v|所有文件|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// 把选中的视频拷进项目素材库（<c>素材\插片素材\</c>），返回相对路径。
    /// 【为什么要拷一份，而不是直接记原路径】项目是自包含的 ——
    ///   记外部路径的话，哪天移动了那个文件夹，这一段就废了。
    /// </summary>
    private string ImportVideoAsset(string src)
    {
        try
        {
            var project = WorkbenchWindow.Session.Current;
            if (project == null) return "";

            string dir = Path.Combine(project.AssetsDir, "插片素材");
            Directory.CreateDirectory(dir);

            string name = Path.GetFileName(src);
            string dst = Path.Combine(dir, name);
            if (File.Exists(dst))
            {
                // 同名就加序号 —— 两段插片用同一个文件名太常见了，别互相覆盖
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                int k = 1;
                do { dst = Path.Combine(dir, $"{stem}_{++k}{ext}"); } while (File.Exists(dst));
            }
            File.Copy(src, dst, false);
            return "插片素材/" + Path.GetFileName(dst);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>问 ffprobe 这段视频有多长（问不到就返回 0，界面上就不显示提示）。</summary>
    private static double ProbeVideoDurationSec(string path)
    {
        try
        {
            string? probe = LiteCut.Core.Media.FFmpegLocator.FFprobePath;
            if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) return 0;

            var psi = new ProcessStartInfo(probe)
            {
                Arguments = "-v error -show_entries format=duration -of csv=p=0 \"" + path + "\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return 0;
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(4000);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>表结构变了（拆/合/重切）之后，原来选中的那个 VM 已经不在表里了，清掉。</summary>
    private void ClearSelAfterTableChange()
    {
        _sel = null;
    }

    /// <summary>草稿被改了：刷新界面 + 排一次防抖落盘。</summary>
    private void MarkDraftTouched(string? status)
    {
        RefreshShotDecisionUi();
        RefreshEditButtons();
        UpdateCounts();
        ScheduleSave();
        if (!string.IsNullOrEmpty(status)) Win?.Status(status);
    }

    /// <summary>
    /// 攒 1.2 秒再写盘。
    /// 【为什么不立刻写】卡片上的文本框是"边打边生效"的，一个字一次落盘
    ///   （shotlist-draft.json 二十多 KB）纯属浪费，还会卡界面。
    /// </summary>
    private void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _saveTimer.Tick += (_, _) => { _saveTimer!.Stop(); SaveStageFiles(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void BtnResplit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { Win?.Status("Agent 正在跑 —— 等这次设计跑完再重切"); return; }
        if (_s.ShotDraft.Count == 0 && _s.Shots.Count == 0) return;

        RefreshWatermark();
        var cur = _s.ShotDraft.Count > 0 ? _s.ShotDraft : _s.Shots;
        int edited = cur.Count(x => x.Edited);
        int designed = cur.Count(HasAnyDesign);

        var lines = new List<string>();
        if (edited > 0) lines.Add($"{edited} 镜手动调过（合并 / 拆分 / 改过边界），会被覆盖");
        if (designed > 0) lines.Add($"{designed} 镜已经有分镜设计；边界没变的会原样保留，边界变了的会清空重来");
        if (_watermark >= ShotWatermark.L2) lines.Add("已渲的产物不会立刻作废 —— 要等你点「应用到下游」时才结算");

        if (lines.Count > 0)
        {
            var r = MessageBox.Show(
                "重新自动切分会按当前切法从头再切一遍：\n\n  · " + string.Join("\n  · ", lines)
                + "\n\n确定要重切吗？",
                "确认重新切分", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }

        if (!AutoSplit()) return;
        MarkDraftTouched("已按当前切法重新切分（只在草稿里）");
    }

    /// <summary>根据选择 / 是否编辑 / 是否忙，刷新手动调边界按钮可用状态 + 提示。</summary>
    private void RefreshEditButtons()
    {
        if (!UiReady) return;

        var sel = SelRow();
        int pos = sel == null ? -1 : _s.ShotDraft.IndexOf(sel);

        BtnMerge.IsEnabled = !_busy && sel != null && pos >= 0 && pos + 1 < _s.ShotDraft.Count
                             && !sel.IsInsert && !_s.ShotDraft[pos + 1].IsInsert;
        // 插片没有源录音区间，也就没有"按句子边界拆"这一说
        BtnSplit.IsEnabled = !_busy && sel != null && !sel.IsInsert;
        BtnResplit.IsEnabled = !_busy && (_s.ShotDraft.Count > 0 || _s.Shots.Count > 0);
        BtnApplyShots.IsEnabled = !_busy && _s.ShotDraft.Count > 0 && WorkbenchWindow.Session.HasProject;

        int edited = _s.ShotDraft.Count(x => x.Edited);
        int stale = _s.ShotDraft.Count(x => x.DesignStale);
        int blank = _s.ShotDraft.Count(x => !HasAnyDesign(x));

        var bits = new List<string>();
        if (edited > 0) bits.Add($"{edited} 镜手动调过");
        if (stale > 0) bits.Add($"{stale} 镜设计待复核");
        if (blank > 0) bits.Add($"{blank} 镜还没设计");
        TxtEditHint.Text = bits.Count > 0 ? "· " + string.Join(" · ", bits) : "";
    }

    // ========================================================
    // ★ T-16：水位 + 交接闸门（「应用到下游」）
    // ========================================================

    /// <summary>
    /// 算一次水位 —— 判的是"这一次交接有多贵"。
    /// L0 只有切分（无代价）/ L1 设计已回填（设计错配）/ L2 已渲染（产物要改名）/
    /// L3 已出成片（同 L2 + 提醒重拼）。
    /// </summary>
    private void RefreshWatermark()
    {
        _watermark = _s.ShotDraft.Any(HasAnyDesign) ? ShotWatermark.L1 : ShotWatermark.L0;

        var p = WorkbenchWindow.Session.Current;
        if (p == null) return;
        try
        {
            string outDir = OutputDirOf(p);
            if (outDir.Length > 0 && Directory.Exists(outDir) &&
                Directory.EnumerateFiles(outDir, "shot_*.mp4").Any())
                _watermark = ShotWatermark.L2;

            if (Directory.Exists(p.AssembleDir) &&
                Directory.EnumerateFiles(p.AssembleDir, "*.mp4").Any())
                _watermark = ShotWatermark.L3;
        }
        catch { /* 目录读不了就当没渲染过 —— 不该因为这个打断交接 */ }
    }

    /// <summary>渲染产物目录（内存设置 → 项目里的 render-spec.json → 按约定推）。</summary>
    private string OutputDirOf(Project.ProjectRef p) =>
        RenderTasksFile.ResolveOutputDir(p.RenderStageDir, _s.RenderOutputDir,
                                         _s.Config.RenderAssetRoot, _s.SourcePath);

    /// <summary>
    /// 刷新"下游当前使用…"那一行 + 「应用到下游」按钮三态。
    /// 【为什么要显式说出来】以前"下游在用哪份"是个黑箱，用户看不出
    ///   自己改了到底生效没有（粗剪页踩过同样的坑，2026-09-29）。
    /// </summary>
    private void RefreshShotDecisionUi()
    {
        if (TxtShotDecision == null || !UiReady) return;

        RefreshWatermark();

        int committed = _s.Shots.Count;
        int draft = _s.ShotDraft.Count;
        bool dirty = _s.ShotDraftDirty;

        string label = committed > 0
            ? $"镜头表 {committed} 镜"
              + (string.IsNullOrEmpty(_s.ShotCommittedAt) ? "" : $" · 已交接于 {_s.ShotCommittedAt}")
            : "尚未交接";

        TxtShotDecision.Text = label;
        TxtShotDecision.Foreground = (Brush?)TryFindResource(committed > 0 ? "Ok" : "TextDim") ?? Brushes.Gray;

        if (draft == 0)
        {
            TxtShotDecisionHint.Text = "还没有镜头表 —— 先在左边生成镜头切分。";
            BtnApplyShots.Content = "应用到下游";
            BtnApplyShots.IsEnabled = false;
            return;
        }

        if (dirty)
        {
            string wm = _watermark switch
            {
                ShotWatermark.L0 => "只有切分，交下去没有任何代价",
                ShotWatermark.L1 => "有分镜设计，交下去只重排镜号、不动文件",
                ShotWatermark.L2 => "已经渲染过 —— 交接会重排镜号并改名产物、标「待重渲」",
                _ => "成片已经出过 —— 交接后还需要到第⑤步重新组接",
            };
            TxtShotDecisionHint.Text = $"草稿 {draft} 镜（有还没交下去的改动）· {wm}";
            BtnApplyShots.Content = "应用到下游";
            BtnApplyShots.IsEnabled = !_busy;
        }
        else
        {
            TxtShotDecisionHint.Text = $"草稿与已交接版一致 —— 下游现在就用这一份（{draft} 镜）。改动后记得点一次交接。";
            BtnApplyShots.Content = "✓ 已在生效";
            BtnApplyShots.IsEnabled = false;
        }
    }

    /// <summary>
    /// ★★ 交接：这一刻才重排镜号、把磁盘上的产物改名、标「待重渲」。
    /// 这是 T-16 的核心 —— 也是唯一会动磁盘的地方。
    /// </summary>
    private void BtnApplyShots_Click(object sender, RoutedEventArgs e)
    {
        var project = WorkbenchWindow.Session.Current;
        if (project == null) { Win?.Status("先打开一个项目再交接"); return; }
        if (_busy) { Win?.Status("Agent 正在跑 —— 等这次设计跑完再交接"); return; }
        if (_s.ShotDraft.Count == 0) { Win?.Status("还没有镜头表 —— 先点「生成镜头切分」"); return; }

        ReindexDraft();

        if (!_s.ShotDraftDirty && _s.Shots.Count > 0)
        {
            Win?.Status("这份镜头表已经在生效了，没有新改动");
            return;
        }

        RefreshWatermark();
        string outDir = OutputDirOf(project);

        // 先算搬迁计划（★ 必须在改 _s.Shots 之前算 —— 它要拿"已交接版"当基准）
        var plan = ReindexMigration.Plan(project, outDir, _s.Shots, _s.ShotDraft);
        bool heavy = _watermark >= ShotWatermark.L2;

        // ---- 框 B：这里才摊代价，按水位加权 ----
        if (_watermark >= ShotWatermark.L1)
        {
            var lines = new List<(string, bool)>
            {
                ($"镜头表 {_s.Shots.Count} 镜 → {_s.ShotDraft.Count} 镜", false),
                (ReindexMigration.Describe(plan), heavy),
            };
            if (heavy) lines.Add(("拆 / 合过的那些镜头内容已经变了 —— 要重渲", true));
            if (_watermark == ShotWatermark.L3) lines.Add(("成片需要到第⑤步重新组接", true));

            string check = heavy ? "我知道从改动那个位置往后的画面会失效，需要重渲" : "";
            bool ok = ShotDialogs.ConfirmCommit(
                Window.GetWindow(this), $"把这份镜头表交给下游？（{_s.ShotDraft.Count} 镜）", lines, check);
            if (!ok) return;
        }
        // L0（只有切分、都还没设计）不弹框：一行状态提示就够了 —— 兑现"随意拆分没问题"

        // 交接前的快照（「撤销这次交接」靠它把镜头表也退回去）
        var before = _s.Shots.Select(WorkbenchState.CloneShot).ToList();
        string beforeAt = _s.ShotCommittedAt;

        // ---- 执行：搬文件（双缓冲改名，失败自动回滚）----
        var (moved, msg) = ReindexMigration.Execute(project, plan);
        if (!moved)
        {
            MessageBox.Show("改名失败，已经尽力回滚到原状：\n\n" + msg + "\n\n"
                            + "详细过程在 " + project.ShotDir + "\\reindex-log-*.json。\n"
                            + "镜头表**没有**交接，下游还是上一版 —— 不会出现错位。",
                            "交接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshShotDecisionUi();
            return;
        }

        // ---- 写回已交接版 ----
        _s.CommitShotDraft();
        _lastCommitLog = plan;
        _preCommitShots = before;
        _preCommitAt = beforeAt;

        // 任务包 / 分镜留档跟着重写 —— 名字必须对上，否则执行者拿着旧表去渲又是静默错位
        SaveStageFiles();
        RenderTaskPackage.Write(_s.Shots, BuildSpec(), outDir, project.RenderStageDir,
                                project.Name, _s.DecisionDurationSec, project.AssetsDir);

        ReloadFromState();
        RefreshShotDecisionUi();
        ShowSummary(plan);
        Win?.Status($"已应用到下游：镜头表 {_s.Shots.Count} 镜 —— " + ReindexMigration.Describe(plan));
    }

    // ---- 单步撤销 ----

    private List<ShotRow>? _preCommitShots;
    private string _preCommitAt = "";

    private void BtnUndoReindex_Click(object sender, RoutedEventArgs e)
    {
        var project = WorkbenchWindow.Session.Current;
        if (project == null || _lastCommitLog == null || _preCommitShots == null)
        {
            Win?.Status("没有可撤销的交接（只保留最近一次，离开这一页就失效）");
            return;
        }

        var r = MessageBox.Show(
            "撤销这次交接？\n\n会把镜头表和磁盘上的产物一起退回交接前的样子：\n"
            + "  · " + ReindexMigration.Describe(_lastCommitLog) + "\n\n"
            + "（改名全部反向搬回。这中间如果你在外面手工动过那些文件，这里可能搬不回来。）",
            "撤销交接", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        var (_, msg) = ReindexMigration.Rollback(project, _lastCommitLog);

        _s.Shots = _preCommitShots.Select(WorkbenchState.CloneShot).ToList();
        _s.ShotCommittedAt = _preCommitAt;
        _s.ResetShotDraft();

        _lastCommitLog = null;
        _preCommitShots = null;
        _preCommitAt = "";

        SaveStageFiles();
        ReloadFromState();
        RefreshShotDecisionUi();
        BarSummary.Visibility = Visibility.Collapsed;
        Win?.Status("已撤销这次交接：" + msg + " —— 镜头表退回交接前那一版");
    }

    private void ShowSummary(ReindexLog plan)
    {
        TxtSummary.Text = "本次交接：" + ReindexMigration.Describe(plan)
                          + " · 镜头表 " + _s.Shots.Count + " 镜";
        BarSummary.Visibility = Visibility.Visible;
    }

    private void BtnDismissSummary_Click(object sender, RoutedEventArgs e) =>
        BarSummary.Visibility = Visibility.Collapsed;

    private void SplitMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!UiReady) return;

        _s.ShotSplitMode = RbMerge.IsChecked == true ? 2
            : (RbPace.IsChecked == true ? 1 : 0);
        _s.Config.LastShotSplitMode = _s.ShotSplitMode;
        _s.Config.Save();
        SyncSplitModeUi();
        RefreshHint();
    }

    /// <summary>按当前选中的切法，显示对应的参数控件（固定节奏 / 语义合并）。</summary>
    private void SyncSplitModeUi()
    {
        if (!UiReady) return;
        bool pace = RbPace.IsChecked == true;
        bool merge = RbMerge.IsChecked == true;
        GrpPace.Visibility = pace ? Visibility.Visible : Visibility.Collapsed;
        TxtPace.IsEnabled = pace;
        GrpMerge.Visibility = merge ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtPace_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!UiReady) return;

        double v = ParsePace();
        _s.ShotPaceSec = v;
        _s.Config.LastShotPaceSec = v;
        RefreshHint();
    }

    private void TxtRange_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!UiReady) return;
        RefreshHint();
    }

    private double ParsePace()
    {
        if (double.TryParse(TxtPace.Text, out double v) && v >= 1 && v <= 60) return v;
        return 6.0;
    }

    /// <summary>读语义合并的块长区间（秒）。默认 5~9，越界回落到安全值。</summary>
    private (double min, double max) ParseRange()
    {
        double min = 5, max = 9;
        if (double.TryParse(TxtMinSec.Text, out var a) && a >= 1 && a <= 60) min = a;
        if (double.TryParse(TxtMaxSec.Text, out var b) && b >= min && b <= 60) max = b;
        else if (b < min) max = min + 1;
        return (min, max);
    }

    // ========================================================
    // ★ 交给 Agent
    // ========================================================

    private async void BtnAgent_Click(object sender, RoutedEventArgs e)
    {
        // ★ 稿子有了就能点 —— 没有镜头表就按当前切的法子先切一遍。
        //   之前要求"必须先点生成镜头切分"，结果按钮一直灰着，
        //   用户以为坏了（2026-09-29 用户反馈："明明稿子也有了，但无法点击"）。
        if (_s.ShotDraft.Count == 0 && _s.Shots.Count == 0 && !AutoSplit())
        {
            Win?.Status("还没有口播稿 —— 先在第①步跑粗剪，或在精剪页点「应用到下游」");
            return;
        }

        // ★ 派单前把镜头表指纹记下来（T-16 并发保护）：
        //   Agent 可能要跑十几分钟，这期间你要是拆了镜头，回包按镜号回填就会**填错镜**。
        //   与其填错，不如不填并说清原因。
        ReindexDraft();
        _agentShotsFingerprint = WorkbenchState.ShotsFingerprint(_s.ShotDraft);

        var cfg = _s.Config;
        var project = WorkbenchWindow.Session.Current;

        // 任务包落在项目的 stages/03-shot 下；没打开项目就退回临时目录（不让人卡住）
        string workDir = project?.ShotDir
                         ?? Path.Combine(Path.GetTempPath(), "litecut-shot", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        string projectName = project?.Name ?? Path.GetFileNameWithoutExtension(_s.SourcePath);

        // ★ 成片规格一起发过去：Agent 得知道画面最终铺在哪张底子上、拿什么渲染，
        //   否则会设计出白底大色块、铺满全屏的图、或者网页做不出来的实拍动效。
        var spec = BuildSpec();

        var req = new AgentRequest
        {
            // ★ 必须显式给 Kind：领任务的人靠它一眼分清"这次干什么活"，
            //   不看目录名也不会搞错（2026-10-01 实机验证查出：这里原先漏了赋值，
            //   任务包里 kind 为空 → 桥接日志显示 kind=? ，唤醒消息退化成 kind=unknown）。
            Kind = "shot",
            Title = $"分镜画面设计 · {projectName} · {_s.ShotDraft.Count} 个镜头",
            SystemPrompt = ShotPrompt.BuildSystemPrompt(spec),
            UserPrompt = ShotPrompt.BuildUserPrompt(projectName, _s.ShotRequirement, _spans, _script!, spec),
            WorkDir = workDir,
            Meta = new Dictionary<string, string>
            {
                ["project"] = projectName,
                ["shots"] = _s.ShotDraft.Count.ToString(),
                ["durationSec"] = _s.DecisionDurationSec.ToString("F1"),
                ["scriptSource"] = _script?.Source ?? "",
                ["canvas"] = $"{spec.Width}x{spec.Height}@{spec.Fps}",
                ["background"] = spec.Background,
                ["engine"] = spec.Engine,
            },
        };

        _cts = new CancellationTokenSource();
        _busyStartedAt = DateTime.Now;
        _lastProgressAt = DateTime.Now;
        _lastProgressKey = "";
        _agentLog.Clear();

        // 开工前先把通道交代清楚，别让人对着进度条猜
        AgentLog(AgentBridge.UseHttp(cfg)
            ? $"通道：直连 {cfg.AgentModel} @ {AgentBridge.EndpointOf(cfg)}"
            : "通道：任务包文件（未配置直连接口）—— 等外部 Agent 领取，独立运行时会一直等");

        SetBusy(true);

        try
        {
            var result = await AgentBridge.CallAsync(req, cfg, p =>
            {
                // 进度回调可能来自后台线程 —— 界面更新一律回 UI 线程
                Ui(() =>
                {
                    _lastProgressAt = DateTime.Now;
                    TxtProgress.Text = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
                    string key = p.Stage + "|" + p.Detail;
                    if (key != _lastProgressKey)
                    {
                        _lastProgressKey = key;
                        AgentLog($"{p.Stage} · {p.Detail}");
                    }
                });
            }, _cts.Token);

            // ★ await 之后的续延不保证还在 UI 线程（库内部的 ConfigureAwait(false)
            //   会把上下文丢掉），所以凡是碰控件的都必须显式回 UI 线程 ——
            //   不然会抛"调用线程无法访问此对象"（2026-09-29 端到端验证抓到）。
            Ui(() =>
            {
                try
                {
                    SetBusy(false);

                    if (!result.Ok)
                    {
                        AgentLog("失败：" + result.Error);
                        Win?.Status("Agent 调用失败：" + result.Error);
                        MessageBox.Show("Agent 调用失败：\n\n" + result.Error + "\n\n" +
                                        "可以点「检测 Agent」先验证配置，或点「Agent 设置…」检查接口地址/密钥。",
                                        "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var items = ShotPrompt.Parse(result.Content);
                    if (items.Count == 0)
                    {
                        Win?.Status("Agent 回来了，但没解析出镜头设计 —— 原始返回已存到分镜目录");
                        try { File.WriteAllText(Path.Combine(workDir, "agent-raw.txt"), result.Content, Encoding.UTF8); } catch { }
                        return;
                    }

                    // ★ 并发保护（T-16 P6）：镜头表在这次设计期间被改过 → **拒绝回填**。
                    //   Agent 可能跑了十几分钟，这期间你要是拆了一镜，镜号全往后挪了；
                    //   这时候按镜号硬填，结果是**每个镜头的设计都错位一格**，
                    //   而且界面上完全看不出来 —— 比不填危险得多。
                    string nowFp = WorkbenchState.ShotsFingerprint(_s.ShotDraft);
                    if (_agentShotsFingerprint.Length > 0 && nowFp != _agentShotsFingerprint)
                    {
                        Win?.Status("镜头表在这次设计期间被改过 —— 这份设计已作废，请重跑一次");
                        MessageBox.Show(
                            "这份设计**没有**填回去，因为镜头表在这次设计期间被改过了"
                            + "（拆过 / 合过 / 改过边界）。\n\n"
                            + "硬填的结果会是每个镜头的设计都错位一格 —— 所以这里宁可不动。\n\n"
                            + "Agent 的原始返回已存到分镜目录，确认没问题的话直接再点一次「交给 Agent 设计画面」。",
                            "设计作废", MessageBoxButton.OK, MessageBoxImage.Warning);
                        try { File.WriteAllText(Path.Combine(workDir, "agent-raw.txt"), result.Content, Encoding.UTF8); } catch { }
                        return;
                    }

                    int filled = FillVisuals(items);

                    ReloadFromState();
                    SaveStageFiles();

                    Win?.Status($"Agent 已回填 {filled}/{_s.ShotDraft.Count} 个镜头的画面构思"
                                + (result.Channel == "file" ? "（任务包通道）" : "（直连通道）")
                                + " —— 填在草稿里；看过确认后点「应用到下游」，再进渲染");
                }
                catch (Exception ex)
                {
                    Win?.Status("处理回包出错：" + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                SetBusy(false);
                Win?.Status("调用出错：" + ex.Message);
            });
        }
    }

    /// <summary>把一段界面操作送回 UI 线程执行（从哪调都安全）。</summary>
    private void Ui(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        TxtProgress.Text = "正在取消…";
    }

    /// <summary>把 Agent 的设计按镜号填回**草稿**。返回填了几个。</summary>
    private int FillVisuals(List<ShotPrompt.VisualItem> items)
    {
        int filled = 0;

        var byIndex = new Dictionary<int, ShotPrompt.VisualItem>();
        foreach (var it in items)
            if (it.Index > 0 && !byIndex.ContainsKey(it.Index)) byIndex[it.Index] = it;

        foreach (var row in _s.ShotDraft)
        {
            if (!byIndex.TryGetValue(row.Index, out var it)) continue;
            if (it.Visual.Length > 0) row.Description = it.Visual;
            if (it.Motion.Length > 0) row.Motion = it.Motion;
            if (it.Image.Length > 0) row.Image = it.Image;

            // 设计是按"当前这一镜的范围"新写的 → 「待复核」可以摘掉了。
            // （这是我们**知道**的事实：Agent 收到的就是现在这张表，不是猜的）
            if (it.Visual.Length > 0 || it.Motion.Length > 0) row.DesignStale = false;
            filled++;
        }

        return filled;
    }

    private void SetBusy(bool busy)
    {
        // ★ 一律回 UI 线程做。
        //   async void 的 await 续延不保证在 UI 线程（库内部的 ConfigureAwait(false)
        //   会把上下文丢掉），曾经从后台线程改按钮直接崩掉整个工作台
        //   （2026-09-29 crash.log：SetBusy → "调用线程无法访问此对象"）。
        //   这里挡一道，调用方忘包 Ui(...) 也不会出事。
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => SetBusy(busy))); return; }

        _busy = busy;
        BtnAgent.IsEnabled = !busy;
        BtnCancel.IsEnabled = busy;
        BtnGen.IsEnabled = !busy && _s.HasDecision;
        BtnTestAgent.IsEnabled = !busy;
        BtnAgentSettings.IsEnabled = !busy;
        ProgressRow.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BarAgent.IsIndeterminate = busy;
        if (busy)
        {
            TxtProgress.Text = "准备任务包…";
            TxtElapsed.Text = "已用时 00:00";
            TxtLog.Visibility = Visibility.Collapsed;
            StartBusyTimer();
        }
        else
        {
            StopBusyTimer();
        }
        RefreshEditButtons();
    }

    // ========================================================
    // ★ Agent 调用过程中的实时反馈
    //   耗时 / 最后进展 / 事件流水，替代"只会转圈的进度条"。
    // ========================================================

    /// <summary>往事件流水里记一条（带时间戳），最新一条也同步进状态栏。</summary>
    private void AgentLog(string msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AgentLog(msg))); return; }

        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _agentLog.Add(line);
        if (_agentLog.Count > 8) _agentLog.RemoveAt(0);

        TxtLog.Text = string.Join("\n", _agentLog);
        TxtLog.Visibility = _agentLog.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartBusyTimer()
    {
        StopBusyTimer();
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _busyTimer.Tick += (_, _) => TickBusyClock();
        _busyTimer.Start();
    }

    private void StopBusyTimer()
    {
        _busyTimer?.Stop();
        _busyTimer = null;
    }

    /// <summary>每秒刷一次：总耗时 + 距上次有动静多久了。超过 30 秒没动静开始提醒。</summary>
    private void TickBusyClock()
    {
        var total = DateTime.Now - _busyStartedAt;
        var since = DateTime.Now - _lastProgressAt;

        TxtElapsed.Text = $"已用时 {total:mm\\:ss} · 最后进展 {since.TotalSeconds:0} 秒前";

        if (since.TotalSeconds > 60)
        {
            TxtElapsed.Foreground = (Brush)FindResource("Danger");
            TxtElapsed.Text += " —— 超过一分钟没有新内容，建议取消重试";
        }
        else if (since.TotalSeconds > 30)
        {
            TxtElapsed.Foreground = (Brush)FindResource("Warn");
            TxtElapsed.Text += "（有一会儿没动静了）";
        }
        else
        {
            TxtElapsed.Foreground = (Brush)FindResource("TextDim");
        }
    }

    // ========================================================
    // ★ Agent 配置与检测
    // ========================================================

    /// <summary>
    /// 「检测 Agent」。
    ///
    /// 【两条通道分别怎么"检测"】
    ///   · 直连模式：真发一次最小请求给大模型接口（max_tokens=8，成本近乎为零）。
    ///   · 桥接 / 任务包模式：★ 走**通路自检** —— 真的把 WorkBuddy 会话里的 Agent
    ///     叫起来，要求它用工具写一份带一次性 token 的收据文件，桥接看到收据才算通。
    ///
    /// 【为什么非改不可】
    ///   老版本在这条通道上直接回一句"未配置直连接口"就结束了 —— 点完屏幕上什么
    ///   都没发生。用户原话：「点这个，测试太无感了感觉不到发生了啥」。
    ///   现在无论哪条通道，结论都**弹对话框**：通了说通、耗时多少；不通给人话原因。
    /// </summary>
    private async void BtnTestAgent_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _s.Config;
        bool direct = AgentBridge.UseHttp(cfg);

        BtnTestAgent.IsEnabled = false;
        TxtAgentInfo.Text = direct
            ? "检测中…（真的发了一次最小请求，最多等 30 秒）"
            : "检测中…（真把 Agent 叫起来，要它写一份收据回来；最多等 5 分钟）";
        TxtAgentInfo.Foreground = (Brush)FindResource("TextDim");

        AgentResult r;

        if (direct)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            r = await AgentBridge.TestAsync(cfg, cts.Token);
        }
        else
        {
            // 桥接自检可能要先排队（Agent 正忙），给足 6 分钟；桥接内部上限是 5 分钟。
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            r = await AgentBridge.PingViaBridgeAsync(
                p => Dispatcher.Invoke(() =>
                {
                    TxtAgentInfo.Text = "检测中… " + p.Detail;
                }),
                cts.Token);
        }

        if (r.Ok)
        {
            TxtAgentInfo.Text = direct
                ? "✔ Agent 已就绪 —— " + r.Detail
                : "✔ 通路已打通 —— 工作台 → 桥接 → Agent 全链路正常（" + r.Detail + "）";
            TxtAgentInfo.Foreground = (Brush)FindResource("Ok");
            Win?.Status("Agent 通路正常：" + r.Detail);
        }
        else
        {
            TxtAgentInfo.Text = "✘ 没通：" + r.Error;
            TxtAgentInfo.Foreground = (Brush)FindResource("Warn");
            Win?.Status("Agent 检测没通过：" + r.Error);
        }

        // ★ 检测结果必须"看得见"——不能只写在一行小字里
        ShowTestResultDialog(r, direct);
        BtnTestAgent.IsEnabled = true;
    }

    /// <summary>把检测结论弹出来。通了说通/耗时，不通给人话原因和排查顺序。</summary>
    private void ShowTestResultDialog(AgentResult r, bool direct)
    {
        var owner = Window.GetWindow(this);

        if (r.Ok)
        {
            string what = direct
                ? "工作台 → 大模型接口（直连）正常。"
                : "工作台 → 本地桥接 → WorkBuddy 会话里的 Agent，整条链路正常。" + Environment.NewLine
                  + "（自检要求 Agent 真的写了一份收据文件才算过 —— 说明它不只能被叫醒，" + Environment.NewLine
                  + "　而且确实能干活。）";
            MessageBox.Show(
                owner,
                (direct ? "✔ Agent 已就绪" : "✔ 通路已打通") + Environment.NewLine + Environment.NewLine
                + what + Environment.NewLine + Environment.NewLine
                + (direct ? "模型 · 延迟：" : "响应耗时：") + r.Detail,
                "检测 Agent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            string hint = direct
                ? "可以点「Agent 设置…」检查接口地址 / 密钥。"
                : "逐条排查：" + Environment.NewLine
                  + "  1) WorkBuddy 里这个会话是否开着（Agent 不在线就没人领任务）；" + Environment.NewLine
                  + "  2) 本地桥接是否在跑 —— 看 工具\\agent-bridge\\bridge.log；" + Environment.NewLine
                  + "  3) 唤醒会排队：Agent 正忙时最多等 5 分钟。";
            MessageBox.Show(
                owner,
                "✘ 通路没打通" + Environment.NewLine + Environment.NewLine
                + r.Error + Environment.NewLine + Environment.NewLine + hint,
                "检测 Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnAgentSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AgentSettingsDialog(_s.Config) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (dlg.Saved) RefreshAgentInfo();
    }

    private void RefreshAgentInfo()
    {
        var cfg = _s.Config;
        if (AgentBridge.UseHttp(cfg))
        {
            TxtAgentInfo.Text = $"已配置直连：{cfg.AgentModel}（还没验证 —— 点「检测 Agent」确认可用）";
            TxtAgentInfo.Foreground = (Brush)FindResource("TextDim");
        }
        else
        {
            TxtAgentInfo.Text = "未配置直连接口 —— 走任务包通道，由本地桥接唤醒 WorkBuddy 里的 Agent。点「检测 Agent」可自检通路是否打通";
            TxtAgentInfo.Foreground = (Brush)FindResource("TextDim");
        }
    }

    // ========================================================
    // 界面状态
    // ========================================================

    private void RefreshHint()
    {
        if (!UiReady) return;

        if (!_s.HasDecision)
        {
            TxtHint.Text = "⚠ 还没有剪辑决策 —— 先在第①步跑粗剪，或在精剪页点「应用到下游」";
            return;
        }

        string mode = RbPace?.IsChecked == true
            ? $"固定节奏：每 {ParsePace():0} 秒换一次画面，切换点吸附到句末"
            : RbMerge?.IsChecked == true
                ? $"语义合并：贪心把句子并成 {ParseRange().min:0}~{ParseRange().max:0} 秒的块"
                : "一句一镜：说完一句换一次画面，时长用真实句时间";

        string est = _script != null && _script.IsEstimate ? "（⚠ 时间是估算的）" : "";
        TxtHint.Text = $"将按 {_s.ActiveDecisionLabel} 切镜头{est} —— {mode}";
    }

    private void RefreshButtons()
    {
        if (!UiReady) return;

        bool hasDecision = _s.HasDecision;
        bool hasScript = _script != null && _script.Lines.Count > 0;

        BtnGen.IsEnabled = hasDecision && hasScript;
        BtnScript.IsEnabled = hasDecision;
        // 有稿子就能交给 Agent（没镜头表会顺手先切一遍），不要卡在"必须先切镜头"上
        BtnAgent.IsEnabled = hasScript;
        BtnOpenDir.IsEnabled = WorkbenchWindow.Session.HasProject;
    }

    /// <summary>
    /// 把**草稿**镜头表铺到卡片流上。
    ///
    /// ★ 只读 UI、只写 VM —— 不碰磁盘、不碰已交接版。
    /// </summary>
    private void ReloadFromState()
    {
        _vms.Clear();
        var rows = _s.ShotDraft.Count > 0 ? _s.ShotDraft : _s.Shots;

        for (int i = 0; i < rows.Count; i++)
        {
            // 打字就落草稿 → 防抖落盘 + 刷新"下游使用"那一行
            var vm = new ShotVm(rows[i], () => MarkDraftTouched(null), OnInsertDurationChanged)
            {
                IsCollapsed = _allCollapsed,

                // ★ 插片不能跟别的镜合并：合了之后"这段到底有没有口播"就说不清了，
                //   组接时静音该补在哪儿也没法算。要改长度直接改时长那一格。
                CanMerge = i + 1 < rows.Count && !rows[i].IsInsert && !rows[i + 1].IsInsert,
            };
            _vms.Add(vm);
        }

        // 选中项在重建之后已经不在表里了 —— 清掉，别留个指不到人的引用
        _sel = null;

        // ★ 镜头明细跟着重建。
        //   页面每次进都重建，_spans 只在"生成镜头切分"那一刻填过 ——
        //   打开项目后直接点 Agent 的话它是空的，提示词里就会没有镜头表。
        _spans = rows.Select(r => new ShotSpan
        {
            Index = r.Index,
            StartSec = r.StartSec,
            EndSec = r.EndSec,
            Text = r.Text,
            SourceStartSec = r.SourceStartSec,
            SourceEndSec = r.SourceEndSec,
            IsLong = r.DurationSec >= FinalScriptBuilder.LongLineSec,
        }).ToList();

        ListShots.ItemsSource = null;
        ListShots.ItemsSource = _vms;

        RefreshBadges();
        UpdateCounts();
        RefreshButtons();
        RefreshEditButtons();
    }

    /// <summary>
    /// 刷新每张卡的两枚徽章。
    /// 【为什么"设计"和"产物"要分开两枚】它们是两件事：
    ///   设计待复核 = 我写的东西可能不匹配了（用户自己确认）；
    ///   产物待重渲 = 磁盘上那份东西旧了（跟渲染有关）。
    ///   合并成一枚就会互相覆盖，谁也说不清。
    /// </summary>
    private void RefreshBadges()
    {
        var project = WorkbenchWindow.Session.Current;
        Dictionary<string, RenderManifestEntry> manifest = new(StringComparer.Ordinal);
        string outDir = "";
        if (project != null)
        {
            try
            {
                outDir = OutputDirOf(project);
                manifest = RenderManifest.Load(outDir);
            }
            catch { }
        }

        foreach (var v in _vms)
        {
            var m = v.Model;
            bool hasDesign = HasAnyDesign(m);
            bool hasFile = outDir.Length > 0 && File.Exists(Path.Combine(outDir, RenderManifest.Mp4NameOf(m.Index)));

            if (m.DesignStale) v.SetDesignBadge("待复核", "Warn");
            else if (!hasDesign) v.SetDesignBadge("待补设计", "TextFaint");
            else v.SetDesignBadge("", "");

            if (!hasFile) v.SetRenderBadge(hasDesign ? "待渲染" : "", "TextFaint");
            else if (!string.IsNullOrEmpty(m.Id) && manifest.TryGetValue(m.Id, out var e))
            {
                bool sameFile = string.Equals(e.File, RenderManifest.Mp4NameOf(m.Index), StringComparison.OrdinalIgnoreCase);
                bool sameContent = string.Equals(e.Fingerprint, RenderManifest.FingerprintOf(m), StringComparison.Ordinal);
                if (!sameFile) v.SetRenderBadge("产物过期", "Danger");
                else if (!sameContent) v.SetRenderBadge("待重渲", "Danger");
                else v.SetRenderBadge("已就绪", "Ok");
            }
            else
            {
                // 没有清单条目 → 不臆断（老项目第一次打开会走这里，回头渲染页会给它建基线）
                v.SetRenderBadge("已出片", "TextFaint");
            }
        }
    }

    private void UpdateCounts()
    {
        bool has = _vms.Count > 0;
        BtnExportJson.IsEnabled = has;
        BtnExportPrompt.IsEnabled = has;

        int img = _vms.Count(v => !string.IsNullOrWhiteSpace(v.Model.Image));
        BtnExportImages.IsEnabled = img > 0;

        // ★ 插片会让成片变长 —— 这行常显，别到导出才发现多了半分钟
        int ins = _vms.Count(v => v.Model.IsInsert);
        double insSec = _vms.Where(v => v.Model.IsInsert).Sum(v => v.Model.DurationSec);

        TxtCount.Text = has
            ? $"共 {_vms.Count} 个镜头" + (img > 0 ? $" · {img} 个要配图" : "")
              + (ins > 0 ? $" · 插片 {ins} 段 · 静音 {insSec:F1}s" : "")
            : "";
    }

    private void TxtRequirement_TextChanged(object sender, TextChangedEventArgs e)
    {
        _s.ShotRequirement = TxtRequirement.Text;
    }

    // ========================================================
    // 产物落地
    // ========================================================

    /// <summary>
    /// 把口播稿和镜头表写到项目的 stages/03-shot 下。
    ///
    /// 【为什么非要落盘】
    ///   ① 进度能从"猜"变成"数文件"；
    ///   ② Agent 读写文件比读写内存容易得多 —— 任务包也在这个目录交换；
    ///   ③ 打开项目一眼看出上次做到哪一步。
    /// </summary>
    private void SaveStageFiles()
    {
        var project = WorkbenchWindow.Session.Current;
        if (project == null) return;

        try
        {
            Directory.CreateDirectory(project.ShotDir);

            // 成片规格也落一份 —— 过几个月回头看这支片子，
            // 能知道当时铺的是哪张底图、什么画幅、拿什么渲染的。
            RenderSpec.Write(project.ShotDir, BuildSpec());

            if (_script != null)
                ContractJson.WriteFile(Path.Combine(project.ShotDir, "final-script.json"), _script);

            // ★ 两份镜头表分开写，文件名把身份说清楚（V1.1.0 / T-16）：
            //     shotlist.json       = **已交接版** —— 下游 / 渲染执行者读的就是这一份，
            //                           跟渲染产物目录里的 render_tasks.json 是同一口径；
            //     shotlist-draft.json = 编辑中的**草稿** —— 只在"还没交接"的时候存在，
            //                           交接完就删掉（没有差异 = 没有草稿文件）。
            var committed = _s.Shots;
            var draft = _s.ShotDraft;

            string committedPath = Path.Combine(project.ShotDir, "shotlist.json");
            if (committed.Count > 0) ContractJson.WriteFile(committedPath, committed);
            else if (File.Exists(committedPath)) File.Delete(committedPath);

            string draftPath = Path.Combine(project.ShotDir, "shotlist-draft.json");
            if (draft.Count > 0 && _s.ShotDraftDirty) ContractJson.WriteFile(draftPath, draft);
            else if (File.Exists(draftPath)) File.Delete(draftPath);

            // 需要配图的镜头单独列一份 —— 渲染环节的 Agent 照着它去搜图。
            // ★ 按**已交接版**写：下游只吃交下去的那一版（草稿里的配图还没生效）。
            //   有就写，没有就把旧文件删掉（免得上一版的需求误导下一环）。
            string imgPath = Path.Combine(project.ShotDir, "images.json");
            var need = committed.Where(x => !string.IsNullOrWhiteSpace(x.Image))
                               .Select(x => new ImageNeed
                               {
                                   Index = x.Index,
                                   StartSec = Math.Round(x.StartSec, 2),
                                   EndSec = Math.Round(x.EndSec, 2),
                                   Text = x.Text,
                                   Image = x.Image,
                                   Motion = x.Motion,
                                   File = x.ImageFile,
                               }).ToList();

            if (need.Count > 0) ContractJson.WriteFile(imgPath, need);
            else if (File.Exists(imgPath)) File.Delete(imgPath);
        }
        catch
        {
            // 落盘失败不影响界面继续用 —— 不值得为此打断工作流
        }
    }

    // ========================================================
    // 配图清单（渲染环节的 Agent 照着它去搜图下载）
    // ========================================================

    /// <summary>
    /// 把"需要网络图片"的镜头列成清单。
    ///
    /// 【为什么现在就要落这份文件】
    ///   用户定的工作流里，搜图下载是**确认分镜之后**才做的（渲染那一环）。
    ///   但需求是在分镜这一步产生的 —— 现在就写下来，下一环直接读，
    ///   不用回头再从表格里抠一遍。
    /// </summary>
    private void BtnExportImages_Click(object sender, RoutedEventArgs e)
    {
        // 用**草稿**：这是"我要交出去"的清单，用户此刻看到的就是草稿
        var need = _s.ShotDraft.Where(x => !string.IsNullOrWhiteSpace(x.Image)).ToList();
        if (need.Count == 0)
        {
            Win?.Status("分镜卡片里还没有需要配图的镜头 —— 在「配图」框里写需求，或让 Agent 填");
            return;
        }

        var list = need.Select(x => new ImageNeed
        {
            Index = x.Index,
            StartSec = Math.Round(x.StartSec, 2),
            EndSec = Math.Round(x.EndSec, 2),
            Text = x.Text,
            Image = x.Image,
            Motion = x.Motion,
            File = x.ImageFile,
        }).ToList();

        // ① 落一份到分镜目录 —— 渲染环节的 Agent 读的就是它
        var project = WorkbenchWindow.Session.Current;
        if (project != null)
        {
            try
            {
                Directory.CreateDirectory(project.ShotDir);
                ContractJson.WriteFile(Path.Combine(project.ShotDir, "images.json"), list);
                Win?.Status($"搜图清单已写到分镜目录：{need.Count} 个镜头需要配图");
            }
            catch (Exception ex)
            {
                Win?.Status("写搜图清单失败：" + ex.Message);
            }
        }

        // ② 再导出一份人能看的 md
        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_搜图清单.md";
        var dlg = new SaveFileDialog
        {
            Title = "导出搜图清单",
            Filter = "Markdown|*.md",
            FileName = def,
            InitialDirectory = project?.ShotDir ?? Path.GetDirectoryName(_s.SourcePath) ?? "",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("# 搜图清单");
        sb.AppendLine();
        sb.AppendLine($"共 {list.Count} 个镜头需要网络图片。请逐个搜图、下载到素材库，");
        sb.AppendLine($"并把落地的文件名填回 images.json 里每个条目的 file 字段。");
        sb.AppendLine();
        sb.AppendLine("| 镜号 | 时间 | 口播 | 要什么样的图 | 怎么展示 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var it in list)
            sb.AppendLine($"| {it.Index} | {it.StartSec:0.0}–{it.EndSec:0.0}s | {EscapeCell(it.Text)} | {EscapeCell(it.Image)} | {EscapeCell(it.Motion)} |");

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            Win?.Status($"搜图清单已保存到 {dlg.FileName}（{list.Count} 张图）");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string EscapeCell(string s) =>
        (s ?? "").Replace("|", "丨").Replace("\n", " ").Replace("\r", "");

    private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
    {
        var project = WorkbenchWindow.Session.Current;
        if (project == null) return;

        try
        {
            Directory.CreateDirectory(project.ShotDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = project.ShotDir, UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Win?.Status("打不开目录：" + ex.Message);
        }
    }

    // ========================================================
    // 导出（保留：给不想走 Agent 的人一条手工的路）
    // ========================================================

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_s.ShotDraft.Count == 0) return;

        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_分镜.json";
        var dlg = new SaveFileDialog
        {
            Title = "导出分镜表",
            Filter = "JSON|*.json",
            FileName = def,
            InitialDirectory = Path.GetDirectoryName(_s.SourcePath) ?? "",
        };
        if (dlg.ShowDialog() != true) return;

        var plan = new ShotPlan
        {
            ProjectId = Path.GetFileNameWithoutExtension(_s.SourcePath),
            AudioDurationSec = Math.Round(_s.DecisionDurationSec, 3),
            GeneratedAt = DateTime.Now.ToString("o"),
        };

        foreach (var v in _vms)
        {
            plan.Shots.Add(new Shot
            {
                Index = v.Model.Index,
                StartSec = Math.Round(v.Model.StartSec, 3),
                EndSec = Math.Round(v.Model.EndSec, 3),
                Script = v.Model.Text,
                VisualIdea = v.Model.Description,
                RenderDurationSec = Math.Round(v.Model.DurationSec, 3),
            });
        }

        try
        {
            ContractJson.WriteFile(dlg.FileName, plan);
            Win?.Status($"分镜表已保存到 {dlg.FileName}（{plan.Shots.Count} 个镜头）");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_s.ShotDraft.Count == 0) return;

        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_画面构思提示词.md";
        var dlg = new SaveFileDialog
        {
            Title = "导出提示词",
            Filter = "Markdown|*.md|文本|*.txt",
            FileName = def,
            InitialDirectory = Path.GetDirectoryName(_s.SourcePath) ?? "",
        };
        if (dlg.ShowDialog() != true) return;

        // 跟交给 Agent 的那份内容完全一致 —— 手工粘给别的模型也是同一套口径
        // （含成片规格：底图、画幅、渲染方案，不然粘出去的那份会缺约束）
        var spec = BuildSpec();
        string md = "# " + ShotPrompt.BuildSystemPrompt(spec) + "\n\n"
                    + ShotPrompt.BuildUserPrompt(
                        WorkbenchWindow.Session.Current?.Name ?? Path.GetFileNameWithoutExtension(_s.SourcePath),
                        _s.ShotRequirement, _spans, _script!, spec);

        try
        {
            File.WriteAllText(dlg.FileName, md, Encoding.UTF8);
            Win?.Status($"提示词已保存到 {dlg.FileName}（含你的特殊要求）");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dlg.FileName, UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>
/// 分镜卡片的视图模型（V1.1.0 / T-15 重写）。
///
/// 【为什么要上 INotifyPropertyChanged】
///   卡片是"边打字边生效"的：改了台词，卡头要跟着更新字数 / 折起来时那句预览、
///   徽章要跟着变。表格时代整表重建（ItemsSource = null 再赋回来）能糊过去，
///   卡片时代那么干会**把你的输入焦点一起清掉**（正在打的字没了）。
///   所以改成逐属性通知，谁变通知谁。
/// </summary>
public sealed class ShotVm : INotifyPropertyChanged
{
    private readonly Action _onChanged;

    /// <summary>改了插片时长才调（要顺延后面所有镜头，比普通改动重一些）。</summary>
    private readonly Action? _onDurationChanged;

    public ShotRow Model { get; }

    public ShotVm(ShotRow model, Action onChanged, Action? onDurationChanged = null)
    {
        Model = model;
        _onChanged = onChanged;
        _onDurationChanged = onDurationChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    // ---- 卡头元信息 ----

    public string IndexText => Model.Index.ToString("D2");
    public string StartText => Step1CutView.FormatTime(Model.StartSec);
    public string EndText => Step1CutView.FormatTime(Model.EndSec);
    public string DurText => Model.DurationSec.ToString("F1") + "s";

    /// <summary>手动调过边界的镜头打 ✎ 标记（重切分前会提醒）。</summary>
    public string EditMark => Model.Edited ? "✎" : "";

    /// <summary>折叠态那行预览：把多行台词压成一行，太长截断。</summary>
    public string PreviewText
    {
        get
        {
            string t = (Model.Text ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
            return t.Length > 60 ? t[..60] + "…" : t;
        }
    }

    // ---- 四个可编辑字段 ----
    //
    // ★ 台词（Text）以前在表格里是**只读的截断显示**（DisplayText 是 get-only），
    //   改不了也看不全 —— 用户原话"镜头的台词一行都不够"。
    //   现在它跟另外三个字段一样，直接改了就写回草稿。

    public string Text
    {
        get => Model.Text;
        set
        {
            if (Model.Text == value) return;
            Model.Text = value ?? "";
            if (!Model.Edited) { Model.Edited = true; Raise(nameof(EditMark)); }
            Raise(nameof(PreviewText));
            RaiseBadges();
            _onChanged();
        }
    }

    public string Description
    {
        get => Model.Description;
        set
        {
            if (Model.Description == value) return;
            Model.Description = value ?? "";
            RaiseBadges();
            _onChanged();
        }
    }

    public string Motion
    {
        get => Model.Motion;
        set
        {
            if (Model.Motion == value) return;
            Model.Motion = value ?? "";
            RaiseBadges();
            _onChanged();
        }
    }

    public string Image
    {
        get => Model.Image;
        set
        {
            if (Model.Image == value) return;
            Model.Image = value ?? "";
            RaiseBadges();
            _onChanged();
        }
    }

    public bool Edited => Model.Edited;

    // ========================================================
    // ★ 插片（V1.10.0）
    //   插片就是普通镜头，写分镜的方式一模一样；只有三处是它独有的：
    //   ① 卡头一个「◈ 插片」标记   ② 卡身一行「时长 + 素材 + 原声」
    //   ③ 默认成片静音（V1.10.15 起可以勾「素材原声」把素材的声音放出来）
    // ========================================================

    public Visibility InsertTagVisibility =>
        Model.IsInsert ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InsertRowVisibility =>
        Model.IsInsert ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VideoClearVisibility =>
        Model.IsInsert && !string.IsNullOrEmpty(Model.VideoFile)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 时长（秒）。**只有插片这一格是能改的** —— 普通镜头的时长来自转写稿，改不得。
    ///
    /// 【改了会连带什么】改完立刻顺延后面所有镜头的时间轴（见页面 RetimeDraft），
    ///   并打上 <see cref="ShotRow.DurationPinned"/> —— 之后选素材不会再自动覆盖这个值。
    /// </summary>
    public string DurationText
    {
        get => Model.DurationSec.ToString("F1", CultureInfo.InvariantCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return;                                  // 打了一半的"3."之类，先不动
            d = Math.Round(Math.Max(0.2, Math.Min(600, d)), 2);
            if (Math.Abs(Model.DurationSec - d) < 0.005) return;

            Model.EndSec = Model.StartSec + d;
            Model.DurationPinned = true;
            Raise(nameof(VideoHint));
            Raise(nameof(DurText));
            _onDurationChanged?.Invoke();
        }
    }

    /// <summary>素材那一栏的说明：播多长、会不会截断、会不会定格。</summary>
    public string VideoHint
    {
        get
        {
            if (!Model.IsInsert) return "";
            if (string.IsNullOrEmpty(Model.VideoFile)) return "（不引用素材：纯静音画面，照常写分镜）";

            double dur = Model.DurationSec;
            if (Model.VideoDurationSec > 0.05)
            {
                if (dur + 0.05 < Model.VideoDurationSec)
                    return $"素材 {Model.VideoDurationSec:F1}s · 只播前 {dur:F1}s";
                if (dur > Model.VideoDurationSec + 0.05)
                    return $"素材只有 {Model.VideoDurationSec:F1}s · 后 {(dur - Model.VideoDurationSec):F1}s 定格在最后一帧";
                return $"素材 {Model.VideoDurationSec:F1}s（正好播完）";
            }
            return Model.VideoFile;
        }
    }

    /// <summary>这一镜有没有素材。没有素材就没有"素材原声"这回事（勾选框禁用）。</summary>
    public bool HasVideo => Model.IsInsert && !string.IsNullOrEmpty(Model.VideoFile);

    // ---- ★ 素材原声（V1.10.15 / T-25）----
    //
    // 【它管什么】插片默认成片静音。"素材原声"勾上之后，这一段的音轨改成取
    //   该镜渲染产物自己的音轨（素材被渲染时按音量录进去）。
    //
    // 【为什么要用户显式勾】素材有声音 ≠ 用户想放出来 —— 这是意图问题，不是技术问题。
    //   而且默认静音是"安全的错"：漏了原声顶多少点氛围，多放了原声会跟口播撞在一起。

    /// <summary>「素材原声」勾选框。</summary>
    public bool InsertAudio
    {
        get => Model.InsertAudio;
        set
        {
            if (Model.InsertAudio == value) return;
            Model.InsertAudio = value;
            Raise(nameof(InsertAudio));
            Raise(nameof(AudioVolumeVisibility));
            Raise(nameof(SilentHintVisibility));
            Raise(nameof(VideoHint));
            _onChanged();
        }
    }

    /// <summary>音量输入框：勾了原声、而且真有素材时才露出来。</summary>
    public Visibility AudioVolumeVisibility =>
        Model.IsInsert && Model.InsertAudio && HasVideo ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「（成片这一段静音）」这句提示：没走原声的时候才该看见。</summary>
    public Visibility SilentHintVisibility =>
        Model.IsInsert && !(Model.InsertAudio && HasVideo) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>原声音量（dB）。0 = 原样、−6 ≈ 减半、+6 ≈ 加倍。</summary>
    public string InsertVolumeText
    {
        get => Model.InsertVolumeDb.ToString("0.#", CultureInfo.InvariantCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return;                                  // 打了一半的 "-" 之类，先不动
            d = Math.Round(Math.Max(-40, Math.Min(12, d)), 1);
            if (Math.Abs(Model.InsertVolumeDb - d) < 0.05) return;

            Model.InsertVolumeDb = d;
            Raise(nameof(InsertVolumeText));
            _onChanged();
        }
    }

    /// <summary>重发卡头的时间显示（改插片时长会带着后面所有镜头顺延，要一起刷新）。</summary>
    public void RaiseTime()
    {
        Raise(nameof(StartText));
        Raise(nameof(EndText));
        Raise(nameof(DurText));
        Raise(nameof(VideoHint));
        Raise(nameof(HasVideo));
        Raise(nameof(AudioVolumeVisibility));
        Raise(nameof(SilentHintVisibility));
    }

    // ---- 交互状态 ----

    private bool _isSelected;
    /// <summary>是不是当前选中的那一镜（合并 / 拆分针对它）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; Raise(nameof(IsSelected)); }
    }

    private bool _isCollapsed;
    /// <summary>折叠成一行（只看元信息，密集浏览用）。</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { if (_isCollapsed == value) return; _isCollapsed = value; Raise(nameof(IsCollapsed)); }
    }

    /// <summary>后面还有镜头（最后一镜不能"合并下一镜"）。</summary>
    public bool CanMerge { get; set; } = true;

    // ---- 状态徽章（设计和产物分开两枚）----

    private string _designBadge = "";
    private string _renderBadge = "";
    private Brush _designBrush = Brushes.Gray;
    private Brush _renderBrush = Brushes.Gray;

    public string DesignBadge { get => _designBadge; private set { _designBadge = value; Raise(nameof(DesignBadge)); RaiseBadges(); } }
    public string RenderBadge { get => _renderBadge; private set { _renderBadge = value; Raise(nameof(RenderBadge)); RaiseBadges(); } }
    public Brush DesignBadgeBrush { get => _designBrush; private set { _designBrush = value; Raise(nameof(DesignBadgeBrush)); } }
    public Brush RenderBadgeBrush { get => _renderBrush; private set { _renderBrush = value; Raise(nameof(RenderBadgeBrush)); } }

    /// <summary>有「待复核 / 待补设计」时提示一下（整卡的 tooltip 用它）。</summary>
    public bool NeedDesignAttention => Model.DesignStale || _designBadge.Length > 0;

    /// <summary>「✎确认无误」只在「待复核」时出现 —— 别的状态没有可确认的。</summary>
    public Visibility ConfirmButtonVisibility =>
        Model.DesignStale ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseBadges()
    {
        Raise(nameof(NeedDesignAttention));
        Raise(nameof(ConfirmButtonVisibility));
    }

    /// <summary>由页面统一算好之后灌进来（算它要读磁盘上的产物，不该塞进 VM）。</summary>
    public void SetDesignBadge(string text, string brushKey)
    {
        DesignBadge = text;
        DesignBadgeBrush = Res(brushKey, Colors.Gray);
    }

    public void SetRenderBadge(string text, string brushKey)
    {
        RenderBadge = text;
        RenderBadgeBrush = Res(brushKey, Colors.Gray);
    }

    /// <summary>
    /// 只重发「✎确认无误按钮可见性 / 整卡提示」这两个派生属性。
    /// ★ 别拿它当"刷新徽章"用 —— 徽章**文字**是由页面级 <c>RefreshBadges()</c>
    ///   算好之后用 <see cref="SetDesignBadge"/> 灌进来的；这里重发多少遍都改不了文字。
    ///   （原名 RefreshBadges 与页面级同名，踩过一次坑，故改名。）
    /// </summary>
    public void RaiseBadgeVisibility() => RaiseBadges();

    private static Brush Res(string key, Color fallback) =>
        string.IsNullOrEmpty(key) ? Brushes.Transparent
        : Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>
/// 一个镜头需要的一张网络图片。渲染环节的 Agent 照着它去搜图下载，
/// 下载完把落地的文件名填回 File。
/// </summary>
public sealed class ImageNeed
{
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }

    /// <summary>这一镜的口播内容（搜图要看它才知道配什么）。</summary>
    public string Text { get; set; } = "";

    /// <summary>"什么样的图 + 用什么动效展示它"。</summary>
    public string Image { get; set; } = "";

    public string Motion { get; set; } = "";

    /// <summary>图下下来之后在素材库里的文件名。空 = 还没下。</summary>
    public string File { get; set; } = "";
}

/// <summary>
/// 口播稿一行的视图模型。
///
/// 包一层是为了两件事：① 改字要通知界面（顺手标"改过"）；
/// ② 播放时要能高亮当前句（歌词线和高亮都看它）。
/// </summary>
public sealed class ScriptLineVm : INotifyPropertyChanged
{
    private readonly Action _onEdited;
    private bool _isCurrent;
    private bool _edited;

    public ScriptLine Model { get; }

    public ScriptLineVm(ScriptLine model, Action onEdited)
    {
        Model = model;
        _onEdited = onEdited;
        _edited = model.Edited;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>行首的时间码（成片时间）。</summary>
    public string StartText
    {
        get
        {
            int m = (int)(Model.StartSec / 60);
            int s = (int)(Model.StartSec - m * 60);
            return $"{m:D2}:{s:D2}";
        }
    }

    public string Text
    {
        get => Model.Text;
        set
        {
            if (Model.Text == value) return;
            Model.Text = value;
            // ★ 改过就打标记 —— 点「重算」时这一句不会被覆盖
            Model.Edited = true;
            if (!_edited) { _edited = true; Raise(nameof(Edited)); }
            _onEdited();
        }
    }

    public bool Edited => Model.Edited;

    /// <summary>播放头是不是正在这一句上。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent == value) return; _isCurrent = value; Raise(nameof(IsCurrent)); }
    }

    private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// 分镜这一步的「水位」—— 判的是"这一次交接有多贵"（T-16 §3.2）。
///
/// 【为什么要算一个水位，而不是写一堆 if】
///   要不要弹框、弹多重、要不要勾选，本来能写成一堆条件判断；
///   但那样每加一种情况就多一个分支，谁也说不清"现在到底该拦多狠"。
///   把复杂度收成一个 0~3 的刻度，所有 UI 都只看它。
///
/// 【刻度是按用户原话切的】
///   "镜头语义确定好之前随意拆分没问题" = L0；"已经返回了分镜设计" = L1。
/// </summary>
public enum ShotWatermark
{
    /// <summary>只有切分 —— 所有镜的设计都是空的。交接**零代价**，不打扰。</summary>
    L0 = 0,

    /// <summary>分镜设计已回填（至少一镜有设计）。交接只重排镜号，代价轻。</summary>
    L1 = 1,

    /// <summary>已经渲染过（产物目录里有 shot_*.mp4）。产物要跟着改名 —— 最麻烦的一档。</summary>
    L2 = 2,

    /// <summary>成片已经出过（stages\05-assemble 里有 mp4）。交接后还要重新组接。</summary>
    L3 = 3,
}
