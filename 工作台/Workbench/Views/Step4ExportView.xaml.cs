// ============================================================
// Step4ExportView.xaml.cs - 第五步：组接导出
// ============================================================
//
// 【三条出口】（2026-09-30 补齐 —— 以前只有前两条的一半）
//   ① 画面成片（推荐）── 把第④步渲好的一镜一镜 MP4 按镜号拼起来，
//      再挂上按剪辑决策重建的音轨，直接出 .mp4。**不开达芬奇、不重编码画面。**
//   ② 音频成片 ── 只把声音按决策重编，出 .m4a。
//      口播交付给后期、或者只想先听整体节奏时用。
//   ③ 达芬奇 XML ── 只给时间线，不渲染。要调色/遮罩/特效时才走。
//
// 【为什么必须补 ① —— 以前这一步只能出 m4a】
//   口播视频的源素材就是一条**录音**（没有画面）。原来的"直接成片"只会
//   "拿源素材按决策裁一遍"：源没画面，所以永远只能出 m4a。
//   可画面明明在第④步一镜一镜渲好了，躺在 04-render 的输出目录里 ——
//   组接页却完全不认识它们。设计草案第五节写的三条路里，B（画面成片）
//   一直空着，这个文件就是把它填上。
//
// 【① 的两个关键点（都写在 FFmpegAssembler 里，这里只说结论）】
//   · 音频零处理：从 FineCut.Clips 原生重建，不拉伸、不变调，一个采样都不改。
//   · 画面 conform：逐块把画面裁/补到"和该块音频段一样长"（帧对齐），
//     所以音画在**每一块边界**都严格对齐，整帧累积差不再往后传。
//     代价是画面要过一次编码（旧的 -c:v copy 直拷做不到精确裁剪）——
//     见 文档/优化与风险登记.md 的 R-03。
//   · 轻量剪辑区（本页，2026-10-01 T-07b）：把这张"逐块对齐表"摊给用户 ——
//     音频列锁死（口播是基准），画面列允许 ±几帧微调，conform 照表执行。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiteCut.Core.Media;
using LiteCut.Core.Contracts;
using LiteCut.Workbench.Agent;
using LiteCut.Workbench.Ui;
using Microsoft.Win32;

namespace LiteCut.Workbench.Views;

public partial class Step4ExportView : UserControl
{
    private readonly WorkbenchState _s = WorkbenchWindow.State;

    /// <summary>控件树建好之前，事件回调一律不干活（见维护手册的坑①）。</summary>
    private bool _uiReady;

    /// <summary>出片进行中，防重入。</summary>
    private bool _busy;

    /// <summary>本页能不能出片（构造时 + 进页面各算一次，见 RefreshReady）。</summary>
    private bool _ready;

    /// <summary>
    /// 本页自己探出来的源素材信息（有没有视频轨）。
    /// 没进过第①步时 `_s.Probe` 是空壳，这里补一次只读探测 ——
    /// 否则「源素材成片」会被错报成「音频成片」、容器选成 .m4a。
    /// </summary>
    private MediaProbe _localProbe = new();

    /// <summary>本页是否已经自己探过一次源素材（每次进页最多探一次）。</summary>
    private bool _probed;

    /// <summary>
    /// 成片画面规格（画幅 / 帧率 / 安全帧）。
    /// ⑤ 组接要按它做帧对齐，所以必须和 ④ 渲染那边用**同一份** ——
    /// 优先读项目的 render-spec.json 留痕，读不到才按全局配置现算。
    /// </summary>
    private readonly RenderSpec _spec;

    /// <summary>轻量剪辑区的一行行（逐块对齐表）。</summary>
    private readonly ObservableCollection<AlignRow> _alignRows = new();

    /// <summary>成片帧率（规格里没有就按 30）。</summary>
    private int Fps => _spec.Fps > 0 ? _spec.Fps : 30;

    /// <summary>
    /// 源素材的探测结果：状态里有就用状态里的（第①步探得更全），没有就用本页自己探的。
    /// 判据 = 有没有被填过（默认的 MediaProbe 三项全空）。
    /// </summary>
    private MediaProbe Probe =>
        _s.Probe.HasAudio || _s.Probe.HasVideo || _s.Probe.DurationSec > 0 ? _s.Probe : _localProbe;

    public Step4ExportView()
    {
        InitializeComponent();

        // 规格：和 ④ 渲染页同一套口径 —— 项目留痕优先，否则按配置现算。
        _spec = RenderSpec.Load(StageDir) ?? RenderSpec.FromConfig(
            _s.Config,
            WorkbenchWindow.Session.Current?.Name
                ?? Path.GetFileNameWithoutExtension(_s.SourcePath),
            StageDir,
            WorkbenchWindow.Session.Current?.Dir ?? "");

        GridAlign.ItemsSource = _alignRows;

        // 决策口径：粗剪或精剪任一都算。纯精剪项目（直接导素材手工剪、
        // 没跑过粗剪）以前在这里永远"还没有粗剪结果" —— 按钮永远灰着。
        //
        // ★★ 2026-10-03 修：门槛从 HasSource（要求 Pcm 已解码）换成 HasSourceFile
        //   （只问"磁盘上有没有源素材"）。病根见 WorkbenchState.HasSourceFile 注释 ——
        //   解码只有第①步会做，用户直接进本页时 ① 从没构造过、Pcm 恒为 null，
        //   于是按钮永远灰着、还谎报"还没有加载源素材"。
        //   本页本来就不碰 Pcm（音轨按源区间交给 ffmpeg 裁），这个门槛本就该放开。
        RefreshReady();

        // ★ 初始值放代码里设，不写进 XAML —— 写死 IsSelected 会在控件树建到
        //   一半时触发 SelectionChanged，handler 摸到还没创建的控件直接整页崩。
        CmbRes.SelectedIndex = 0;
        CmbQ.SelectedIndex = 1;              // 标准 crf 20
        CmbMode.SelectedIndex = HasRenderProducts() ? 0 : 1;

        _uiReady = true;                     // 控件树已就绪，事件回调可以干活了
        RefreshModeUi();                     // 模式文案（含第二条路的名字）统一在这里铺

        // ★ 初始值放代码里设，不写进 XAML —— 写死 IsSelected 会在控件树建到
        //   一半时触发 SelectionChanged，handler 摸到还没创建的控件直接整页崩。
        CmbSpeed.SelectedIndex = 2;          // 审片默认 1.5×（T-27：看片为主，速览节奏）

        // 键盘：空格 = 播放/暂停（T-27，2026-10-04 用户要求"跟手"）。
        // 挂在 Preview（隧道）阶段：焦点在任何控件上都先到这儿，
        // 且 Handled=true 之后按钮不会再把空格当"点击"吃掉。
        // 打字场景（意见框）在 handler 里单独放行。
        PreviewKeyDown += Step4_PreviewKeyDown;

        // 进页面再核对一次，顺手把"源素材有没有画面"补齐：
        //   · ① 可能刚解码完（Pcm / Probe 这时才到位）
        //   · 剪辑决策可能在别处刚被"应用到下游"
        //   · 没进过①的本页要自己探一次（ffprobe，毫秒级；只读，不改任何共享状态）
        Loaded += async (_, _) =>
        {
            RefreshReady();
            RefreshModeUi();
            await EnsureProbeAsync();
            RefreshModeUi();
            ReviewOnLoaded();                // T-27 审片台：载标记、探成片状态
        };

        if (!_ready)
        {
            Log(NotReadyHint());
            return;
        }

        Log($"已就绪：{_s.SourceFileName}");

        // 先说清"这一刀是按谁剪的" —— 精剪还是录音铺排，段数时长多少。
        // 用户点了「应用到下游」之后来这一页，第一眼就能确认到底生效了没有。
        string who = _s.ActiveDecision == DecisionSource.Fine ? "精剪" : "录音铺排";
        Log($"本次成片依据：{_s.ActiveDecisionLabel}（{who}决策）");

        // 决策可能来自粗剪（有 Plan 的统计），也可能来自精剪（只有保留区间）—— 分开设
        if (_s.HasPlan && _s.Plan != null)
        {
            Log($"剪辑后时长：{TimeText.Format(_s.KeepRanges().Sum(r => r.End - r.Start))}" +
                $"（原始 {TimeText.Format(_s.Plan.Stats.OriginalSec)}）");
        }
        else
        {
            Log($"剪辑后时长：{TimeText.Format(_s.KeepRanges().Sum(r => r.End - r.Start))}" +
                $"（精剪决策，{_s.KeepRanges().Count} 段）");
        }

        if (Probe.HasVideo)
            Log("源文件有画面");
        else
            Log("源文件只有音频 —— 想在这一页出**视频**，走「画面成片」拼第④步的渲染产物；" +
                "「音频成片」只会出声音。");

        if (_s.Shots.Count > 0)
            Log($"已载入 {_s.Shots.Count} 个分镜（来自第③步）");
    }

    // ========================================================
    // 能不能出片：判据是磁盘事实
    // ========================================================

    /// <summary>
    /// 重新判一次"本页能不能出片"，并把「开始出片」放行 / 灰掉。
    ///
    /// 【两个判据，都是磁盘事实，不是内存里的半成品状态】
    ///   · 源素材在不在   → `_s.HasSourceFile`（**不看 Pcm**，见它的注释）
    ///   · 有没有剪辑决策 → `_s.HasDecision`（粗剪或精剪任一）
    ///
    /// 【为什么不能只在构造时算一次】构造这一页的时候，① 可能还没把源素材解码完，
    ///   决策也可能刚在别处被"应用到下游"。所以构造时算一次、进页面再算一次。
    /// </summary>
    private void RefreshReady()
    {
        _ready = _s.HasSourceFile && _s.HasDecision;
        BtnGo.IsEnabled = _ready && !_busy;
    }

    /// <summary>
    /// "还不能出片"的时候该说的话。要分清"没源素材"和"没决策"两种缺法 ——
    /// 以前一句"还没有加载源素材"打天下，可源素材明明躺在磁盘上。
    /// </summary>
    private string NotReadyHint()
    {
        if (!_s.HasSourceFile)
        {
            return string.IsNullOrWhiteSpace(_s.SourcePath)
                ? "还没有加载源素材 —— 先在第①步选一个音频/视频文件。"
                : $"源素材文件找不到了：{_s.SourcePath}（挪走或改名了？回第①步重新选一次）";
        }
        return "还没有剪辑决策 —— 先在录音台点「应用到下游」，母带才会交给精剪。";
    }

    /// <summary>
    /// 补齐"源素材有没有画面"这一件事。
    ///
    /// 【为什么本页要自己探一次】没进过第①步时 `_s.Probe` 是空壳（HasVideo=false），
    ///   会把「源素材成片」错报成「音频成片」、容器选成 .m4a。
    ///   ffprobe 一次是毫秒级，而且**只读** —— 结果只存在本页字段里，
    ///   绝不写回共享状态（免得跟第①步的解码结果打架）。
    /// </summary>
    private async Task EnsureProbeAsync()
    {
        if (_probed) return;
        if (!_s.HasSourceFile) return;

        // 状态里已经有真结果了（① 探过）→ 不用探
        if (!ReferenceEquals(Probe, _localProbe)) { _probed = true; return; }

        string path = _s.SourcePath;
        try
        {
            var p = await Task.Run(() =>
            {
                try { return FFmpegDecoder.Probe(path); }
                catch { return null; }
            });
            _probed = true;
            if (p != null) _localProbe = p;
        }
        catch { /* 探不到就按"没画面"处理 —— 不影响「画面成片」那条主路 */ }
    }

    private WorkbenchWindow? Win => WorkbenchWindow.FindOwner(this);

    // ========================================================
    // 模式
    // ========================================================

    private string Mode => (CmbMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "film";

    private void CmbMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;     // 见构造函数里的坑①
        RefreshModeUi();
    }

    /// <summary>
    /// 按当前模式把"能不能出、吃什么、缺什么"写清楚，并决定画质选项要不要灰。
    /// 用户在这一页最怕的是"点了没反应"，所以每个模式都要自报家门。
    /// </summary>
    private void RefreshModeUi()
    {
        if (!_uiReady) return;

        // 第二条路的说法要跟着源素材走：
        //   源只有声音 → 这条路就是"只出声音"（口播视频最常见）；
        //   源本身有画面 → 那还是老口径"把源素材按决策裁一遍重编"，别让人以为只出音频。
        // （放在这里刷而不是构造里：探测结果可能在进页面之后才补齐，见 EnsureProbeAsync）
        if (CmbMode.Items.Count > 1 && CmbMode.Items[1] is ComboBoxItem audioItem)
            audioItem.Content = Probe.HasVideo
                ? "源素材成片（按决策裁源素材）"
                : "音频成片（只出声音）";

        // 还没到"能出片"的程度 → 这一页只报"缺什么"，别去讲各模式吃什么的细节
        // （缺源素材时讲"已出片 0/26 块"只会让人更困惑）
        if (!_ready)
        {
            TxtModeHint.Text = NotReadyHint();
            PanelAlign.Visibility = Visibility.Collapsed;
            return;
        }

        // 轻量剪辑区只属于「画面成片」—— 别的出口没有"逐块画面"这回事。
        PanelAlign.Visibility = Mode == "film" ? Visibility.Visible : Visibility.Collapsed;
        TxtAlignPolicy.Text = $"音频列锁死，只微调画面留几帧（按 {Fps}fps，1 帧 = {1.0 / Fps:F3}s）";

        if (Mode == "film")
        {
            var blocks = CollectBlocks();
            int have = blocks.Count(b => File.Exists(b.VideoPath) &&
                                         new FileInfo(b.VideoPath).Length >= FFmpegAssembler.MinBlockBytes);
            double design = blocks.Sum(b => b.DesignSec);

            TxtModeHint.Text = blocks.Count == 0
                ? "画面成片：第④步还没有渲染产物（没有任务表或产物目录里没有 shot_xxx.mp4）。" +
                  "先去第④步把镜头渲出来，再回来组接。"
                : $"画面成片：把第④步渲好的 {blocks.Count} 个镜头按镜号拼成成片 —— " +
                  $"当前已出片 {have}/{blocks.Count} 块，设计总长 {FFmpegAssembler.Fmt(design)}。" +
                  $"画面逐块 conform 到各块音频段长度（按 {Fps}fps 帧对齐，要过一次编码），" +
                  "音轨零处理重建（不拉伸、不变调）。";

            // conform 要过一次编码 —— 所以「画质」对它有意义，别灰。
            CmbQ.IsEnabled = true;
            CmbQ.ToolTip = "conform 要过一次编码，画质在这里生效（crf/cq 越小越清晰、文件越大）。";

            // 但分辨率不行：conform 只裁/补时长，不缩放，沿用第④步渲染产物的画幅。
            CmbRes.IsEnabled = false;
            CmbRes.ToolTip = "画面成片沿用第④步渲染产物的分辨率（conform 只裁/补时长，不缩放）。"
                           + "想换画幅，去第④步按新规格重渲。";

            EnsureAlignRows();      // 进画面成片就把逐块对齐表填上（不探测，立刻有东西看）
        }
        else
        {
            bool audioOnly = !Probe.HasVideo;
            TxtModeHint.Text = Mode == "xml"
                ? "达芬奇 XML：只导出时间线描述，不渲染。在达芬奇里 File → Import Timeline 导入。"
                : audioOnly
                    ? "音频成片：把源素材按剪辑决策裁好、顺序拼成一整条声音（.m4a）。" +
                      "适合先把整体节奏听一遍，或把口播交给后期。"
                    : "源素材成片：把源素材按剪辑决策裁一遍、重编码出片（画面就是源素材本身）。";

            // 这两条路会重编码，选项有效
            CmbRes.IsEnabled = !audioOnly;
            CmbQ.IsEnabled = !audioOnly;
            if (audioOnly)
            {
                const string why = "源素材只有音频，出的是声音文件，没有画面可缩放。";
                CmbRes.ToolTip = why;
                CmbQ.ToolTip = why;
            }
            else { CmbRes.ToolTip = null; CmbQ.ToolTip = null; }
        }
    }

    // ========================================================
    // 第④步的产物：块清单 / 音轨段
    // ========================================================

    private string StageDir => WorkbenchWindow.Session.Current?.RenderStageDir ?? "";

    /// <summary>渲染产物目录。和渲染页同一套推导（内存 → render-spec.json → 按约定推）。</summary>
    private string RenderOutDir => RenderTasksFile.ResolveOutputDir(
        StageDir, _s.RenderOutputDir, _s.Config.RenderAssetRoot, _s.SourcePath);

    /// <summary>成片默认落在项目自己的 05-assemble 下（设计草案里定的固定位置）。</summary>
    private string AssembleDir =>
        WorkbenchWindow.Session.Current?.AssembleDir
        ?? Path.Combine(Path.GetDirectoryName(_s.SourcePath) ?? "", "05-assemble");

    /// <summary>
    /// 这一页有没有"画面可拼"。只数文件，不探测时长 —— 进页要立刻给出答案。
    /// </summary>
    private bool HasRenderProducts()
    {
        try
        {
            var blocks = CollectBlocks();
            return blocks.Count > 0 && blocks.Any(b => File.Exists(b.VideoPath));
        }
        catch { return false; }
    }

    /// <summary>
    /// 精剪保留区间（**源时间**）。组接层要把「气口」归还给对应的块，
    /// 得先知道"源上哪几段是真的进了成片的"。
    /// 与 <see cref="CollectSegments"/> 取同一份数据（精剪时间轴优先），
    /// 免得"算窗口用一套、切音频用另一套"。
    /// </summary>
    private List<(double Start, double End)> KeptRanges()
    {
        if (_s.ActiveDecision == DecisionSource.Fine && _s.TimelineSnapshot.Count > 0)
        {
            var fine = _s.TimelineSnapshot
                .Where(c => c.SourceEndSec - c.SourceStartSec > 0.001)
                .Select(c => (Start: c.SourceStartSec, End: c.SourceEndSec))
                .OrderBy(r => r.Start)
                .ToList();
            if (fine.Count > 0) return fine;
        }
        return _s.KeepRanges()
                 .Select(r => (Start: r.Start, End: r.End))
                 .OrderBy(r => r.Start)
                 .ToList();
    }

    /// <summary>
    /// 每块的**成片目标时长**（秒，按镜号）。
    ///
    /// 【★ V1.10.14 —— 组接层补偿「精剪的气口」（B 方案）】
    ///
    ///   病根：分镜按**口播句**切，句子与句子之间那口气、片头前导、片尾余量
    ///   都不属于任何一行 → 落在**块边界上、且被精剪保留**的音频原本谁都不认领，
    ///   画面轴于是比音轨轴短（本项目实测差 1.340s），出片被 drift 自检拦下。
    ///
    ///   分镜侧已修（V1.10.13 的 ShotSplitter.FillToTimeline，重切即对齐）。
    ///   但**一批已经渲好的镜头**不该为了 1.3 秒全部重渲 ——
    ///   所以组接层也要能兜住：按「该块内容在决策轴上的真实位置」重算窗口。
    ///
    ///     首块 Start=0（吃片头前导）；
    ///     第 i 块窗口 = [它自己的真实起点, 下一块的真实起点]（**气口归前一块**）；
    ///     末块 End=决策轴总长（吃片尾余量）。
    ///
    ///   数学保证：Σ 目标时长 ≡ 决策轴总长 ⇒ **画面轴 ≡ 音轨轴**，逐块对齐。
    ///   代价：画面偏短的块由 conform 冻最后一帧补齐（B 方案认下的交换，见风险登记）。
    ///
    ///   前提：成片顺序与源时间顺序一致。精剪允许挪块，真挪过就退回原时长
    ///   （硬套公式会把窗口算反，比不补更糟）。
    /// </summary>
    private Dictionary<int, double> ComputeBlockLens()
    {
        var rows = (_s.Shots.Count > 0 ? _s.Shots : RenderTasksFile.Load(StageDir))
                   .Where(r => r.DurationSec > 0.01)
                   .OrderBy(r => r.Index)
                   .ToList();

        var lens = new Dictionary<int, double>();
        foreach (var r in rows) lens[r.Index] = r.DurationSec;      // 默认原样

        // 只管"有源区间的语音块"；插片（IsInsert）的时长就是设计时长。
        var speech = rows.Where(r => !r.IsInsert && r.SourceEndSec - r.SourceStartSec > 0.005).ToList();
        if (speech.Count < 2) return lens;

        for (int i = 1; i < speech.Count; i++)
            if (speech[i].SourceStartSec < speech[i - 1].SourceStartSec - 0.005)
                return lens;                                         // 源顺序 ≠ 成片顺序 → 不补

        var ranges = KeptRanges();
        if (ranges.Count == 0) return lens;

        double Kept(double a, double b)
        {
            if (b <= a) return 0;
            double t = 0;
            foreach (var (s, e) in ranges) t += Math.Max(0, Math.Min(b, e) - Math.Max(a, s));
            return t;
        }

        double axisTotal = Kept(0, double.MaxValue);
        if (axisTotal <= 0.01) return lens;

        for (int i = 0; i < speech.Count; i++)
        {
            double start = i == 0 ? 0 : Kept(0, speech[i].SourceStartSec);
            double end = i + 1 < speech.Count ? Kept(0, speech[i + 1].SourceStartSec) : axisTotal;
            double len = end - start;
            if (len > 0.01) lens[speech[i].Index] = len;
        }
        return lens;
    }

    /// <summary>
    /// 拼成片要用的块清单：优先内存里的分镜表，退回第④步留下的任务包。
    ///
    /// 【为什么用"时长累加"而不是任务表里的 startSec/endSec】
    ///   音轨是按"决策保留区间依序首尾相接"排的，它就是一条严格连续的轴。
    ///   块的成片窗口必须落在同一条轴上。用累加保证严格连续，
    ///   表里万一有细小的缝也不会让音画整体错位。
    ///
    /// ★ 时长来自 <see cref="ComputeBlockLens"/>（V1.10.14 起含"归还的气口"），
    ///   不是裸的 DurationSec —— 否则画面轴会比音轨轴短，出片被自检拦下。
    /// </summary>
    private List<FilmBlock> CollectBlocks()
    {
        var rows = _s.Shots.Count > 0 ? _s.Shots : RenderTasksFile.Load(StageDir);
        string outDir = RenderOutDir;
        var lens = ComputeBlockLens();

        var list = new List<FilmBlock>();
        double cursor = 0;
        foreach (var r in rows.OrderBy(r => r.Index))
        {
            double dur = lens.TryGetValue(r.Index, out double L) ? L : r.DurationSec;
            if (dur <= 0.01) continue;

            list.Add(new FilmBlock
            {
                Index = r.Index,
                DesignStartSec = cursor,
                DesignEndSec = cursor + dur,
                VideoPath = RenderTasksFile.Mp4Of(outDir, r.Index),
            });
            cursor += dur;
        }
        return list;
    }

    /// <summary>
    /// 拼成片要用的音轨段：从源录音的哪些区间来、多大音量。
    ///
    /// 【为什么优先用精剪时间轴而不是 KeepRanges】
    ///   KeepRanges 只有"保留哪几段"，**不带每块的音量**（精剪里调过的音量记在
    ///   完整时间轴的 VolumeDb 上）。只按区间拼，音量会回到 0dB ——
    ///   本项目精剪统一压了 -5dB，那样拼出来的成片会整体响一档。
    ///   所以精剪接管下游时，读完整时间轴。
    /// </summary>
    private List<FilmSegment> CollectSegments()
    {
        if (_s.ActiveDecision == DecisionSource.Fine && _s.TimelineSnapshot.Count > 0)
        {
            var clips = _s.TimelineSnapshot
                .Where(c => c.SourceEndSec - c.SourceStartSec > 0.001)
                .OrderBy(c => c.TimelineStartSec)          // ★ 按"在成片里的先后"，不是源时间
                .Select(c => new FilmSegment
                {
                    SourceStartSec = c.SourceStartSec,
                    SourceEndSec = c.SourceEndSec,
                    VolumeDb = c.VolumeDb,
                    FadeInSec = c.FadeInSec,
                    FadeOutSec = c.FadeOutSec,
                })
                .ToList();

            // 时间轴和生效决策长度对不上（改过没应用 / 档案被手工改过）就别硬用，
            // 宁可退回纯区间（音量按 0dB），也不要拼出一条和画面错位的音轨。
            if (Math.Abs(clips.Sum(s => s.LengthSec) - _s.DecisionDurationSec) < 0.05)
                return AddAudioForInserts(clips);

            Log("提示：精剪时间轴的长度与生效决策不一致，本次音轨改按保留区间重建（音量按 0dB）。");
        }

        return AddAudioForInserts(_s.KeepRanges()
            .Select(r => new FilmSegment { SourceStartSec = r.Start, SourceEndSec = r.End })
            .ToList());
    }

    // ========================================================
    // ★ 插片那一段的声音（V1.10.0 起补静音；V1.10.15 起可以是"素材原声"）
    // ========================================================
    //
    // 【为什么必须占位】画面轴和音轨轴**必须一样长**（差 0.5 秒就不给出片）。
    //   插片让画面长了 N 秒，音轨就得跟着长 N 秒。
    //
    // 【为什么不往精剪时间轴里塞空白块】
    //   时间轴会被当成"口播保留区间"去和转写稿求交，塞进去一个不属于录音的块，
    //   口播稿会凭空多出一句；而且预览听着是静音、导出却是真的人声
    //   （导出按源区间从原录音裁）。所以插片的声音只在**组接这一刻**处理，上游一概不动。
    //
    // 【★ 为什么"素材原声"只能从渲染产物里取】V1.10.15 / T-25
    //   成片音轨是从**原录音**（精剪决策 + 源文件）重建的，而插片不属于这次录音 ——
    //   它在原录音里根本没有对应位置。所以插片想出声，唯一来源就是它自己的产物音轨。
    //   这也是"插片原声"必须连渲染一起打开的原因（提示词硬写静音 → 产物静音 → 成片只能静音）。

    /// <summary>
    /// 按分镜表里的插片，在音轨的对应位置补上那一段 ——
    /// 默认静音；该插片开了「素材原声」时，取它自己产物 MP4 的音轨。
    /// </summary>
    private List<FilmSegment> AddAudioForInserts(List<FilmSegment> segs)
    {
        var rows = _s.Shots.Count > 0 ? _s.Shots : RenderTasksFile.Load(StageDir);
        if (rows.Count == 0) return segs;

        // 插点在**口播轴**上算：把插片摘掉，剩下那些镜头累加出来的坐标。
        // （画面轴 = 口播轴 + 插片，两条轴混着算会越算越偏）
        // ★ V1.10.14：累加用 ComputeBlockLens（含归还的气口），
        //   和 CollectBlocks 同一条轴 —— 否则插片会插错位置。
        var lens = ComputeBlockLens();
        var holes = new List<(double At, FilmSegment Seg)>();
        double speech = 0;
        int voiced = 0, missed = 0;

        foreach (var r in rows.OrderBy(r => r.Index))
        {
            double d = lens.TryGetValue(r.Index, out double L) ? L : r.DurationSec;
            if (d <= 0.01) continue;

            if (r.IsInsert)
            {
                string mp4 = RenderTasksFile.Mp4Of(RenderOutDir, r.Index);
                bool wantVoice = r.InsertAudio && r.VideoFile.Length > 0;

                if (wantVoice && File.Exists(mp4))
                {
                    // ★ 音量**不在这里调** —— 渲染时已按 data-volume 录进产物了，
                    //   这里再施加一次就是双倍衰减（或双倍放大）。
                    holes.Add((speech, new FilmSegment
                    {
                        VideoAudioPath = mp4,
                        VideoAudioStartSec = 0,
                        VideoAudioEndSec = d,
                    }));
                    voiced++;
                }
                else
                {
                    if (wantVoice) missed++;
                    // 没开原声 / 产物还没渲 / 压根没素材 → 一律静音占位
                    holes.Add((speech, new FilmSegment { IsSilence = true, SilenceSec = d }));
                }
                continue;
            }

            speech += d;
        }

        if (voiced > 0)
            Log($"插片原声：{voiced} 段取自渲染产物的音轨（音量在渲染时已录好，这里不二次处理）。");
        if (missed > 0)
            Log($"⚠ {missed} 段插片开了「素材原声」，但磁盘上找不到它的产物 —— 本次按静音处理；" +
                "先把那几镜渲出来，再出片就带声音了。");

        if (holes.Count == 0) return segs;

        foreach (var (at, seg) in holes) InsertSegmentAt(segs, at, seg);
        return segs;
    }

    /// <summary>在音轨的 at 秒处插入一段（静音 / 插片原声）。at 是**口播轴**坐标。</summary>
    private static void InsertSegmentAt(List<FilmSegment> segs, double at, FilmSegment seg)
    {
        double len = seg.LengthSec;
        if (len <= 0.01) return;

        double acc = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            // ★ 只按"源录音段"累加 —— 插片自己的段（静音或原声）不占口播轴坐标。
            if (s.IsSilence || s.VideoAudioPath.Length > 0) continue;

            double L = s.LengthSec;
            if (at <= acc + L + 1e-6)
            {
                double off = at - acc;
                if (off > 0.02 && off < L - 0.02)
                {
                    // 插点落在这一段中间 → 把它切成两半，中间夹上这一段。
                    // 切开的那两半要各留各的淡化（切口那侧不该有淡入淡出，会听出一次喘气）。
                    var right = new FilmSegment
                    {
                        SourceStartSec = s.SourceStartSec + off,
                        SourceEndSec = s.SourceEndSec,
                        VolumeDb = s.VolumeDb,
                        FadeOutSec = s.FadeOutSec,
                    };
                    s.SourceEndSec = s.SourceStartSec + off;
                    s.FadeOutSec = 0;
                    segs.Insert(i + 1, seg);
                    segs.Insert(i + 2, right);
                }
                else if (off <= 0.02)
                {
                    segs.Insert(i, seg);
                }
                else
                {
                    segs.Insert(i + 1, seg);
                }
                return;
            }
            acc += L;
        }

        segs.Add(seg);      // 落在所有段之后（片尾插片）
    }

    // ========================================================
    // 轻量剪辑区：逐块对齐表（2026-10-01 T-07b）
    // ========================================================
    //
    // 【它解决什么】
    //   ⑤ 组接 = 逐块把画面 conform 到"和该块音频段一样长"。这件事本该可见、
    //   可微调 —— 而不是一个黑箱按钮。这张表就是那个把手：
    //     音频段 / 画面实测 / Δ 三列只读（口播是基准，动不得）；
    //     只有「微调 帧」能改，且只动画面（正数=多留几帧、负数=少留几帧）。
    //
    // 【为什么进页就填表、但不探测】
    //   探测 51 块要几秒（每块一次 ffprobe）。进页要"立刻有东西看"，
    //   所以先把音频段长度和"产物在不在"填上（这两样不读文件时长），
    //   画面实测留给用户点「逐块对齐预览」再去读。

    /// <summary>
    /// 填/刷新逐块对齐表（不探测时长）。行数没变就不重建，避免把已扫描的数据冲掉。
    /// </summary>
    private void EnsureAlignRows()
    {
        var blocks = CollectBlocks();
        if (_alignRows.Count == blocks.Count) { UpdateAlignSummary(); return; }

        _alignRows.Clear();
        foreach (var b in blocks.OrderBy(b => b.Index))
        {
            var fi = string.IsNullOrWhiteSpace(b.VideoPath) ? null : new FileInfo(b.VideoPath);
            _alignRows.Add(new AlignRow
            {
                Index = b.Index,
                VideoPath = b.VideoPath,
                Fps = Fps,
                AudioSec = b.DesignSec,
                HasVideo = fi != null && fi.Exists && fi.Length >= FFmpegAssembler.MinBlockBytes,
            });
        }
        UpdateAlignSummary();
    }

    /// <summary>底部那一行汇总：总长、帧对齐余量、缺几块、手动微调了几块。</summary>
    private void UpdateAlignSummary()
    {
        if (_alignRows.Count == 0)
        {
            TxtAlignSummary.Text = "还没有可对齐的镜头 —— 先去第④步把镜头渲出来。";
            BtnAlignReset.IsEnabled = false;
            return;
        }

        int missing = _alignRows.Count(r => !r.HasVideo);
        int adjusted = _alignRows.Count(r => r.AdjustFrames != 0);
        double sumAudio = _alignRows.Sum(r => r.AudioSec);
        double sumTarget = _alignRows.Sum(r => r.TargetSec);

        TxtAlignSummary.Text =
            $"{_alignRows.Count} 块 · 音频总长 {sumAudio:F3}s · 对齐后画面总长 {sumTarget:F3}s" +
            $"（帧对齐余量 {sumTarget - sumAudio:F3}s）" +
            (missing > 0 ? $" · 缺 {missing} 块" : "") +
            (adjusted > 0 ? $" · {adjusted} 块带微调" : "") +
            "。未探测时长时先点「逐块对齐预览」。";

        BtnAlignReset.IsEnabled = adjusted > 0;
    }

    /// <summary>
    /// 逐块读出画面实测时长，和音频段长度比一比（只读磁盘，不改任何文件）。
    /// </summary>
    private async void BtnAlignScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var blocks = CollectBlocks();
        if (blocks.Count == 0)
        {
            Log("没有可对齐的镜头 —— 第④步还没有渲染产物。");
            return;
        }

        // 扫描会重建整张表，先把用户已经调过的微调按镜号记下来，扫完还原 ——
        // 微调是"用户的意图"，别被一次探测冲掉。
        var keep = _alignRows.Where(r => r.AdjustFrames != 0)
                             .GroupBy(r => r.Index)
                             .ToDictionary(g => g.Key, g => g.First().AdjustFrames);

        BtnAlignScan.IsEnabled = false;
        BtnAlignScan.Content = "探测中…";
        Log($"逐块对齐：探测 {blocks.Count} 块画面实测时长（每块一次 ffprobe）…");
        var sw = Stopwatch.StartNew();

        List<FFmpegAssembler.BlockDelta> deltas;
        try
        {
            deltas = await Task.Run(() => FFmpegAssembler.PreviewDelta(blocks, Fps));
        }
        finally
        {
            BtnAlignScan.IsEnabled = true;
            BtnAlignScan.Content = "逐块对齐预览";
        }
        sw.Stop();

        _alignRows.Clear();
        foreach (var d in deltas)
        {
            _alignRows.Add(new AlignRow
            {
                Index = d.Index,
                VideoPath = d.VideoPath,
                Fps = Fps,
                AudioSec = d.AudioSec,
                HasVideo = d.HasVideo,
                ActualSec = d.ActualSec,
                AdjustFrames = keep.TryGetValue(d.Index, out int a) ? a : 0,
            });
        }
        UpdateAlignSummary();

        int missing = deltas.Count(d => !d.HasVideo);
        Log($"✓ 探测完成（{sw.Elapsed.TotalSeconds:F1}s）：{deltas.Count} 块，缺 {missing} 块");
        Log("  音频一列锁死（口播是基准）；要调只调「微调 帧」：正数=多留几帧、负数=少留几帧。");
    }

    /// <summary>把所有微调归零，回到严格按音频段长度对齐。</summary>
    private void BtnAlignReset_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var r in _alignRows)
        {
            if (r.AdjustFrames == 0) continue;
            r.AdjustFrames = 0;
            n++;
        }
        UpdateAlignSummary();
        Log(n > 0 ? $"已还原 {n} 块微调（全部按音频段长度严格对齐）。" : "本来就没有微调。");
    }

    /// <summary>面板底部的「对齐并组接」= 同一个出片流程（只是从这张表出发）。</summary>
    private async void BtnAssemble_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await ExportFilmAsync();
    }

    /// <summary>把轻量剪辑区的微调收成 conform 用的字典（只收非 0 项）。</summary>
    private Dictionary<int, int>? CollectTrimFrames()
    {
        Dictionary<int, int>? trim = null;
        foreach (var r in _alignRows)
        {
            if (r.AdjustFrames == 0) continue;
            (trim ??= new Dictionary<int, int>())[r.Index] = r.AdjustFrames;
        }
        return trim;
    }

    // ========================================================
    // 出片：画面成片（拼第④步渲染产物）
    // ========================================================

    private async Task<bool> ExportFilmAsync()
    {
        var blocks = CollectBlocks();
        if (blocks.Count == 0)
        {
            Log("✗ 没有可拼的镜头 —— 第④步还没有渲染产物。");
            TxtResult.Text = "没有可拼的镜头，先去第④步渲染";
            return false;
        }

        EnsureAlignRows();      // 逐块对齐表得在，后面要从它收微调

        string def = (WorkbenchWindow.Session.Current?.Name
                      ?? Path.GetFileNameWithoutExtension(_s.SourcePath)) + "-成片.mp4";

        var dlg = new SaveFileDialog
        {
            Title = "输出成片",
            Filter = "视频|*.mp4",
            FileName = def,
            InitialDirectory = Directory.Exists(AssembleDir) ? AssembleDir : PickDir(),
        };
        if (dlg.ShowDialog() != true) return false;

        int quality = int.TryParse((CmbQ.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                                   out int qq) ? qq : 20;

        Log("────── 组接成片 ──────");
        Log($"画面：{blocks.Count} 块渲染产物（{RenderOutDir}）");
        return await AssembleCoreAsync(dlg.FileName, quality, blocks, isFinal: true);
    }

    /// <summary>
    /// 组接核心：自检 → conform → 拼接 + 音轨重建。
    /// 正式导出（弹对话框那条路）和审片台的「组装审片成片」共用这一段 ——
    /// 唯一区别是 isFinal：只有正式导出才登记 LastExportPath / 启用"打开输出文件"。
    /// </summary>
    private async Task<bool> AssembleCoreAsync(string outPath, int quality,
                                               List<FilmBlock> blocks, bool isFinal)
    {
        var segs = CollectSegments();
        string src = _s.SourcePath;

        if (!isFinal)
            Log($"画面：{blocks.Count} 块渲染产物（{RenderOutDir}）");
        Log($"音轨：{segs.Count} 段，源 {Path.GetFileName(src)}，零处理重建（不拉伸、不变调）");

        SetBusy(true);
        var sw = Stopwatch.StartNew();

        // ---- 1. 自检：缺块 / 音画对不上就不拼 ----
        Log("自检中（逐块探测时长）…");
        var plan = await Task.Run(() => FFmpegAssembler.Precheck(blocks, segs));

        Log($"自检：画面 {blocks.Count} 块 · 设计 {plan.DesignTotalSec:F3}s · " +
            $"实测 {plan.ActualTotalSec:F3}s · 音轨 {plan.AudioTotalSec:F3}s");

        if (Math.Abs(plan.DriftSec) > 0.05)
            Log($"  整帧累积差 {plan.DriftSec:+0.000;-0.000}s —— 各块 MP4 向上取整到整帧造成的，" +
                "将逐块 conform 画面到各自音频段长度来消掉（音频零处理）");

        foreach (var w in plan.Warnings) Log("  ⚠ " + w);

        if (!plan.Ok)
        {
            Log("✗ 自检不通过，先不拼：");
            foreach (var p in plan.Problems) Log("    · " + p);
            TxtResult.Text = "自检不通过，看上面的日志";
            SetBusy(false);
            return false;
        }

        // ---- 2. 出片 ----
        var trim = CollectTrimFrames();
        if (trim != null)
            Log("轻量剪辑：" + trim.Count + " 块带人工微调（"
                + string.Join("、", trim.OrderBy(kv => kv.Key)
                                        .Select(kv => $"#{kv.Key:D3} {kv.Value:+0;-0}帧")) + "）");

        Log($"开始组接（逐块 conform 画面 + 音轨零处理重建）· {Fps}fps · crf/cq={quality}…");
        var progress = new Progress<string>(l =>
        {
            // ffmpeg 的进度是同一行反复刷新，只挑关键节点显示，别把日志刷爆
            string t = (l ?? "").Trim();
            if (t.StartsWith("conform ") || t.StartsWith("frame=") ||
                t.Contains("rror") || t.Contains("Invalid"))
                Log(t.Length > 130 ? t[^130..] : t);
        });

        var res = await Task.Run(() => FFmpegAssembler.Assemble(outPath, segs, plan,
            new FilmAssembleOptions
            {
                SourcePath = src,
                Fps = Fps,
                Quality = quality,
                TrimFrames = trim,
                Progress = progress,
            }));

        sw.Stop();
        SetBusy(false);

        if (!res.Success)
        {
            Log("✗ 失败：" + res.Error);
            if (res.LogTail.Length > 0) Log("ffmpeg 输出：" + res.LogTail);
            TxtResult.Text = "出片失败，看上面的日志";
            return false;
        }

        foreach (var s in res.Steps) Log("  · " + s);

        var fi = new FileInfo(outPath);
        double speed = res.ElapsedSec > 0 ? res.FilmSec / res.ElapsedSec : 0;

        Log("✓ 成片完成");
        Log($"  输出：{outPath}");
        Log($"  大小：{fi.Length / 1024.0 / 1024.0:F1} MB");
        Log($"  耗时：{sw.Elapsed.TotalSeconds:F1}s（{speed:F1}× 实时）");

        TxtResult.Text = $"已出片：{Path.GetFileName(outPath)}  {fi.Length / 1024.0 / 1024.0:F1}MB  " +
                         $"{FFmpegAssembler.Fmt(res.FilmSec)}  " +
                         $"耗时 {sw.Elapsed.TotalSeconds:F1}s";

        // ★ 只有正式导出才登记"最近输出"—— 审片草稿不算交付物
        if (isFinal)
        {
            _s.LastExportPath = outPath;
            _s.Config.LastOutputDir = Path.GetDirectoryName(outPath) ?? "";
            _s.SaveConfig();
            SetOutButtons(true);
        }
        return true;
    }

    // ========================================================
    // 出片：音频成片 / XML（原样保留）
    // ========================================================

    private async Task ExportAudioAsync()
    {
        // ---- 直接成片（按决策裁源素材）----
        string ext = Probe.HasVideo ? ".mp4" : ".m4a";
        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_成片" + ext;

        var dlg = new SaveFileDialog
        {
            Title = "输出成片",
            Filter = Probe.HasVideo ? "视频|*.mp4" : "音频|*.m4a;*.mp3",
            FileName = def,
            InitialDirectory = PickDir(),
        };
        if (dlg.ShowDialog() != true) return;

        int q = int.TryParse((CmbQ.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int qq) ? qq : 20;
        string? scale = (CmbRes.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "1920x1080" => "1920:1080",
            "1280x720" => "1280:720",
            _ => null,
        };

        var opt = new ExportOptions
        {
            WithVideo = Probe.HasVideo,
            WithAudio = true,
            Quality = q,
            Scale = scale,
            Progress = new Progress<string>(OnProgress),
        };

        SetBusy(true);
        Log("────── 开始出片 ──────");
        Log($"画质 crf/cq={q}，分辨率={scale ?? "原始"}，编码器=自动（优先 NVENC）");

        var sw = Stopwatch.StartNew();
        var ranges = _s.KeepRanges().ToList();
        string src = _s.SourcePath;
        string outPath = dlg.FileName;

        var res = await Task.Run(() => FFmpegExporter.ExportRanges(src, ranges, outPath, opt));
        sw.Stop();

        SetBusy(false);

        if (!res.Success)
        {
            Log("✗ 失败：" + res.Error);
            Log("ffmpeg 输出：" + res.LogTail);
            TxtResult.Text = "出片失败，看上面的日志";
            return;
        }

        var fi = new FileInfo(outPath);
        // 倍速的分母是"成片时长"—— 精剪决策的项目没有 Plan，用决策总时长
        double decisionSec = _s.KeepRanges().Sum(r => r.End - r.Start);
        double speed = res.ElapsedSec > 0 ? decisionSec / res.ElapsedSec : 0;

        _s.LastExportPath = outPath;
        _s.Config.LastOutputDir = Path.GetDirectoryName(outPath) ?? "";
        _s.SaveConfig();

        Log("✓ 完成");
        Log($"  输出：{outPath}");
        Log($"  大小：{fi.Length / 1024.0 / 1024.0:F1} MB");
        Log($"  耗时：{res.ElapsedSec:F1}s（{speed:F1}× 实时）");
        Log($"  编码器：{res.VideoCodecUsed}");

        TxtResult.Text = $"已出片：{Path.GetFileName(outPath)}  {fi.Length / 1024.0 / 1024.0:F1}MB  " +
                         $"耗时 {res.ElapsedSec:F1}s（{speed:F1}× 实时）";
        SetOutButtons(true);
    }

    private void OnProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        // ffmpeg 的进度是同一行反复刷新（\r 结尾），全部追加会把日志刷爆。
        // 只挑关键节点显示。
        string t = line.Trim();
        if (t.StartsWith("frame=") || t.StartsWith("size=") || t.StartsWith("[lib") ||
            t.Contains("Error") || t.Contains("error") || t.Contains("Invalid"))
        {
            Log(t.Length > 130 ? t[^130..] : t);
        }
    }

    /// <summary>
    /// 导出 FCP7 XML 给达芬奇。需要精修时才走这条路。
    /// </summary>
    private void ExportXml()
    {
        // XML 只描述"保留哪些段"—— 粗剪或精剪的决策都能导
        if (!_s.HasDecision) return;

        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_剪辑.xml";
        var dlg = new SaveFileDialog
        {
            Title = "导出达芬奇 XML",
            Filter = "XML|*.xml",
            FileName = def,
            InitialDirectory = PickDir(),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // ★ 无 BOM：FCP7 XML 是给达芬奇读的，标准做法是纯 UTF-8 + XML 自带 encoding 声明，
            //   带 BOM 时个别 NLE 会解析失败（R-13 同一类坑）。
            File.WriteAllText(dlg.FileName, BuildFcp7Xml(_s.SourcePath, _s.KeepRanges()), ContractJson.Utf8NoBom);
            Log($"✓ XML 已导出：{dlg.FileName}");
            Log("  在达芬奇里：File → Import Timeline → 选这个 XML");
            TxtResult.Text = "XML 已导出，可在达芬奇里导入";
            SetOutButtons(true);
            _s.LastExportPath = dlg.FileName;
        }
        catch (Exception ex)
        {
            Log("✗ 导出 XML 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 拼一份最简 FCP7 XML。只放必要字段，能被达芬奇认出来即可。
    /// </summary>
    private static string BuildFcp7Xml(string sourcePath, List<FFmpegExporter.Range> ranges)
    {
        double fps = 30.0;
        var sb = new StringBuilder();

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE xmeml>");
        sb.AppendLine("<xmeml version=\"4\">");
        sb.AppendLine("  <sequence>");
        sb.AppendLine($"    <name>{Path.GetFileNameWithoutExtension(sourcePath)}</name>");
        sb.AppendLine("    <media>");
        sb.AppendLine("      <video>");
        sb.AppendLine("        <format>");
        sb.AppendLine("          <samplecharacteristics>");
        sb.AppendLine($"            <rate><timebase>{(int)Math.Round(fps)}</timebase></rate>");
        sb.AppendLine("            <width>1920</width>");
        sb.AppendLine("            <height>1080</height>");
        sb.AppendLine("          </samplecharacteristics>");
        sb.AppendLine("        </format>");
        sb.AppendLine("        <track>");

        double tl = 0;
        foreach (var r in ranges)
        {
            int start = (int)Math.Round(r.Start * fps);
            int end = (int)Math.Round(r.End * fps);
            int tlStart = (int)Math.Round(tl * fps);
            int tlEnd = (int)Math.Round((tl + r.End - r.Start) * fps);
            tl += r.End - r.Start;

            sb.AppendLine("          <clipitem>");
            sb.AppendLine("            <file id=\"" + Path.GetFileName(sourcePath) + "\">");
            sb.AppendLine($"              <pathurl>file:///{sourcePath.Replace("\\", "/")}</pathurl>");
            sb.AppendLine("            </file>");
            sb.AppendLine($"            <in>{start}</in>");
            sb.AppendLine($"            <out>{end}</out>");
            sb.AppendLine($"            <start>{tlStart}</start>");
            sb.AppendLine($"            <end>{tlEnd}</end>");
            sb.AppendLine("          </clipitem>");
        }

        sb.AppendLine("        </track>");
        sb.AppendLine("      </video>");
        sb.AppendLine("    </media>");
        sb.AppendLine("  </sequence>");
        sb.AppendLine("</xmeml>");

        return sb.ToString();
    }

    // ========================================================
    // 审片台（2026-10-04 T-27）—— 只审最终成片
    // ========================================================
    //
    // 【这套东西解决什么】
    //   以前审核 = 第④页逐镜看缩略图（一镜十几秒 × 26 镜 ≈ 25 分钟），
    //   看完还要自己把"第几镜什么问题"抄进修改意见框。
    //   现在改成审片人视角：一键拼一版**完整的成片** → 1.5× 倍速看 →
    //   看到问题**在这里打点**（时间 + 一句话）→ 工作台自动算出"这是第几镜" →
    //   意见批量写回 shot-overrides.json → 一键唤醒 Agent 批量重渲 →
    //   改完重新点一次组装。用户只出现在"看片 + 说话"两个环节里。
    //
    // 【打点 → 镜号怎么算（必须和 ffmpeg 干的事一模一样）】
    //   成片时间轴 = 逐块 conform 后首尾相接，每块的输出时长 =
    //   FramesToSec( UpToFrames(设计时长, fps) + 用户微调帧数, fps )。
    //   把这个公式在 C# 里复算一遍，累加出每块在成片上的起止，
    //   打点时间落进哪块的窗口就是哪块 —— **帧级精确，不靠猜**。
    //   （认镜号 Index；分镜重排后历史标记仍指同一镜号的格子。）
    //
    // 【重渲为什么是"批量一个任务包"而不是一镜一个包】
    //   agent-reshoot.json 一次只能有一份（同名会覆盖），桥接队列对同名包
    //   还会合并 —— 一镜一包会把后面的镜挤丢。批量包 = 一份任务里逐镜一节
    //   （每节就是一份完整的单镜任务正文，Agent 逐节处理）。
    //   回包只配对到 Meta 里第一镜做即时反馈，其余镜头的状态灯靠
    //   渲染页的磁盘判据（产物比意见新 = 已落地）自然回绿 —— 谁渲的都认。

    private static readonly string MarkerFile = "review-markers.json";

    private readonly ObservableCollection<MarkerRow> _markers = new();

    /// <summary>审片重渲进行中，防重入（与出片的 _busy 分开管各自的按钮）。</summary>
    private bool _reviewBusy;

    private CancellationTokenSource? _reviewCts;

    /// <summary>用户正在拖进度条（拖动期间计时器别回写滑块位置）。</summary>
    private bool _seekDragging;

    /// <summary>审片成片固定落这里 —— 组装是覆盖式的，永远只有"当前版本"。</summary>
    private string DraftPath => Path.Combine(AssembleDir, "审片-draft.mp4");

    /// <summary>审片成片有没有载进播放器（决定打点按钮给不给用）。</summary>
    private bool _filmLoaded;

    /// <summary>页面销毁时要停掉的计时器（本页不是 KeepAlive，重建就换新实例）。</summary>
    private DispatcherTimer? _reviewTimer;

    /// <summary>进页面：载历史标记 + 摸一遍审片成片的状态。</summary>
    private void ReviewOnLoaded()
    {
        try
        {
            GridMarkers.ItemsSource = _markers;
            LoadMarkers();
            RefreshMarkerStatuses();
            UpdateMarkUi();

            // 成片还在且没有被重渲冲掉 → 直接入播放器，用户进来就能看
            if (File.Exists(DraftPath))
            {
                ReviewPlayer.Source = new Uri(DraftPath);
                TxtNoFilm.Visibility = Visibility.Collapsed;
                RefreshDraftState();
            }
            else
            {
                RefreshDraftState();
            }
        }
        catch (Exception ex)
        {
            Log("审片台初始化出错：" + ex.Message);
        }

        // 进度条计时器：100ms 一次，拖动期间不回写
        _reviewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _reviewTimer.Tick += (_, _) =>
        {
            try
            {
                if (!_seekDragging && ReviewPlayer.NaturalDuration.HasTimeSpan)
                    SldSeek.Value = ReviewPlayer.Position.TotalSeconds;
                TxtTime.Text = $"{FmtTime(ReviewPlayer.Position.TotalSeconds)} / " +
                               $"{FmtTime(ReviewPlayer.NaturalDuration.HasTimeSpan ? ReviewPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0)}";
            }
            catch { /* 播放器可能正被释放 */ }
        };
        _reviewTimer.Start();

        ReviewPlayer.MediaOpened += (_, _) =>
        {
            _filmLoaded = true;
            TxtNoFilm.Visibility = Visibility.Collapsed;
            if (ReviewPlayer.NaturalDuration.HasTimeSpan)
                SldSeek.Maximum = ReviewPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            UpdateMarkUi();
        };
        ReviewPlayer.MediaFailed += (_, _) =>
        {
            Log("✗ 审片成片播放失败 —— 文件可能在组装中或已损坏，重新组装一次。");
        };

        // 本页不是 KeepAlive：离开时必须停表、停播，否则计时器抓着旧实例空转
        Unloaded += (_, _) =>
        {
            _reviewTimer?.Stop();
            try { ReviewPlayer.Stop(); ReviewPlayer.Close(); } catch { }
        };
    }

    private static string FmtTime(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalMinutes >= 60 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---- 组装审片成片 ----

    private async void BtnDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var blocks = CollectBlocks();
        if (blocks.Count == 0)
        {
            Log("✗ 没有可拼的镜头 —— 第④步还没有渲染产物。");
            return;
        }

        EnsureAlignRows();

        // ★ 播放器抓着旧文件时 ffmpeg 写不进去 —— 先彻底释放
        try { ReviewPlayer.Stop(); ReviewPlayer.Source = null; } catch { }
        _filmLoaded = false;

        Directory.CreateDirectory(AssembleDir);

        Log("────── 组装审片成片 ──────");
        SetBusy(true);
        BtnDraft.Content = "组装中…";
        bool ok = await AssembleCoreAsync(DraftPath, 18, blocks, isFinal: false);
        SetBusy(false);
        BtnDraft.Content = "① 组装审片成片";

        RefreshDraftState();

        if (ok)
        {
            ReviewPlayer.Source = new Uri(DraftPath);
            RefreshMarkerStatuses();     // 组装后各标记的"已重渲"状态要重算
        }
    }

    /// <summary>审片成片的新旧：有没有、多久、几镜比它新。</summary>
    private void RefreshDraftState()
    {
        try
        {
            if (!File.Exists(DraftPath))
            {
                TxtDraftState.Text = "还没组装过 —— 点左边按钮拼一版，看片全程不弹窗。";
                return;
            }

            var draft = File.GetLastWriteTime(DraftPath);
            int newer = 0;
            try
            {
                foreach (var b in CollectBlocks())
                    if (File.Exists(b.VideoPath) && File.GetLastWriteTime(b.VideoPath) > draft)
                        newer++;
            }
            catch { }

            var fi = new FileInfo(DraftPath);
            TxtDraftState.Text = $"审片成片：{draft:HH:mm} 组装 · {fi.Length / 1024.0 / 1024.0:F0} MB" +
                (newer > 0
                    ? $" · **{newer} 镜比它新** → 建议重新组装"
                    : " · 与当前渲染产物一致");
        }
        catch (Exception ex)
        {
            TxtDraftState.Text = "状态读取失败：" + ex.Message;
        }
    }

    // ---- 播放控制 ----

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => ToggleReviewPlay();

    private void Step4_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 正在打字（打点意见框等）→ 空格属于文字，不抢
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        if (e.Key == Key.Space)
        {
            e.Handled = true;
            ToggleReviewPlay();
        }
    }

    /// <summary>
    /// 播放/暂停二态切换（空格与按钮共用一条路 —— 两条入口必须一模一样快）。
    ///
    /// 【"跟手"的做法】切换只调 <c>Pause()</c>/<c>Play()</c>，**绝不碰 Position、
    /// 绝不重设 Source** —— 那两样任何一个都会触发重新定位/重新开流，
    /// 就是"按了空格卡一下"的根源。播到头再按 = 从头放（这是唯一的例外定位）。
    /// "正在播"的真源 = 按钮文案（MediaElement 没有 IsPlaying 属性）。
    /// </summary>
    private void ToggleReviewPlay()
    {
        if (ReviewPlayer.Source == null) return;

        if (BtnPlay.Content.ToString()!.Contains("暂停"))
        {
            ReviewPlayer.Pause();
            BtnPlay.Content = "▶ 播放";
            return;
        }

        // 播到头了 → 从头再放
        try
        {
            if (ReviewPlayer.NaturalDuration.HasTimeSpan &&
                ReviewPlayer.Position >= ReviewPlayer.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(50))
                ReviewPlayer.Position = TimeSpan.Zero;
        }
        catch { }

        ReviewPlayer.Play();
        BtnPlay.Content = "⏸ 暂停";
    }

    private void CmbSpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ReviewPlayer.SpeedRatio =
            double.TryParse((CmbSpeed.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                            System.Globalization.CultureInfo.InvariantCulture, out var sp)
                ? sp : 1.0;
    }

    private void SldSeek_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _seekDragging = true;

    private void SldSeek_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seekDragging = false;
        try { ReviewPlayer.Position = TimeSpan.FromSeconds(SldSeek.Value); } catch { }
    }

    // ---- 打点 ----

    private void TxtMarkNote_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            AddMarker();
        }
    }

    private void BtnMark_Click(object sender, RoutedEventArgs e) => AddMarker();

    private void AddMarker()
    {
        if (!_filmLoaded || !ReviewPlayer.NaturalDuration.HasTimeSpan)
        {
            Log("要先有审片成片才能打点 —— 点「① 组装审片成片」。");
            return;
        }

        double t = ReviewPlayer.Position.TotalSeconds;
        int shot = MapTimeToShotIndex(t);
        string note = (TxtMarkNote.Text ?? "").Trim();
        TxtMarkNote.Clear();

        _markers.Add(new MarkerRow
        {
            TimeSec = t,
            ShotIndex = shot,
            Note = note,
            CreatedAt = DateTime.Now,
        });

        SaveMarkers();
        RefreshMarkerStatuses();
        UpdateMarkUi();
        Log(shot > 0
            ? $"✓ 已打点 {FmtTime(t)} → #{shot:D3}" + (note.Length > 0 ? $"（{note}）" : "（未写意见，按保守打磨处理）")
            : $"⚠ 已打点 {FmtTime(t)}，但这一刻没有对应的镜头（超出画面范围），这条只做记录。");

        // 新标记在清单里可见
        GridMarkers.ScrollIntoView(_markers.Last());
    }

    /// <summary>
    /// 成片时间 → 镜号：按"逐块 conform 后的真实输出时长"累加窗口，
    /// 和 FFmpegAssembler.Assemble 的帧对齐公式逐帧一致（见本区域头注释）。
    /// </summary>
    private int MapTimeToShotIndex(double t)
    {
        try
        {
            var blocks = CollectBlocks().OrderBy(b => b.Index).ToList();
            if (blocks.Count == 0) return -1;

            var trim = CollectTrimFrames();
            double acc = 0;
            int last = blocks[^1].Index;

            foreach (var b in blocks)
            {
                int adj = trim != null && trim.TryGetValue(b.Index, out int a) ? a : 0;
                int f = Math.Max(1, FFmpegAssembler.UpToFrames(b.DesignSec, Fps) + adj);
                double len = FFmpegAssembler.FramesToSec(f, Fps);

                if (t < acc + len - 1e-6) return b.Index;
                acc += len;
            }
            return last;      // 落在末尾帧对齐余量里 → 算最后一镜
        }
        catch { return -1; }
    }

    private void BtnMarkerDel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MarkerRow row) return;
        _markers.Remove(row);
        SaveMarkers();
        RefreshMarkerStatuses();
        UpdateMarkUi();
    }

    private void BtnMarkClear_Click(object sender, RoutedEventArgs e)
    {
        if (_markers.Count == 0) return;
        _markers.Clear();
        SaveMarkers();
        UpdateMarkUi();
        Log("已清空全部标记（已写进第④页的意见不受影响）。");
    }

    // ---- 意见写回 + 批量重渲 ----

    private void BtnNotesWrite_Click(object sender, RoutedEventArgs e) => MergeNotesIntoOverrides();

    private async void BtnSendReshoot_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewBusy || _busy) return;

        var groups = _markers.Where(m => m.ShotIndex > 0)
                             .GroupBy(m => m.ShotIndex)
                             .OrderBy(g => g.Key)
                             .ToList();
        if (groups.Count == 0)
        {
            MessageBox.Show("还没有有效的镜头标记 —— 先看成片、打点。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // ① 意见先落盘（重渲任务包里的意见就是从这里读的）
        MergeNotesIntoOverrides();

        // ② 组一份批量单镜重渲任务包
        var project = WorkbenchWindow.Session.Current;
        if (project == null) return;

        var rows = (_s.Shots.Count > 0 ? _s.Shots : RenderTasksFile.Load(StageDir))
                   .ToDictionary(r => r.Index, r => r);

        string workDir = project.RenderStageDir;
        Directory.CreateDirectory(workDir);

        var sb = new StringBuilder();
        sb.AppendLine("# 审片台批量单镜重渲任务");
        sb.AppendLine();
        sb.AppendLine($"用户看完了整条成片，标了 **{groups.Count} 个镜头**要改。");
        sb.AppendLine("下面每一节（── 分隔）就是一份**完整的单镜重渲任务**，格式与单镜任务一致：");
        sb.AppendLine("逐节处理 —— 先 Read 该镜原实现、在它基础上改（不要重写）、渲染成 MP4、更新缩略图。");
        sb.AppendLine("**全部镜头做完后**再写回包，content 用人话汇总：每镜一两句（改了什么、怎么理解用户意见）。");
        sb.AppendLine();

        foreach (var g in groups)
        {
            if (!rows.TryGetValue(g.Key, out var row)) continue;   // 分镜表里已没有这镜（重排后）→ 跳过
            string note = string.Join("\n", g.OrderBy(m => m.TimeSec)
                                              .Select(m => m.Note.Length > 0 ? m.Note : "（用户没写意见，按保守打磨处理）"));
            sb.AppendLine(new string('─', 40));
            sb.AppendLine();
            sb.AppendLine(ReshootPrompt.BuildUserPrompt(
                row, _spec, workDir, RenderOutDir, StageDir, note, edits: null));
            sb.AppendLine();
        }

        var req = new AgentRequest
        {
            Kind = "reshoot",
            Title = $"审片台批量重渲 · {project.Name} · {groups.Count} 镜",
            SystemPrompt = ReshootPrompt.SystemPrompt(),
            UserPrompt = sb.ToString(),
            WorkDir = workDir,
            WaitMinutes = 120,
            RequestFileName = ReshootPrompt.RequestFile,
            ResponseFileName = ReshootPrompt.ResponseFile,
            PromptFileName = ReshootPrompt.PromptFile,
            Meta = new Dictionary<string, string>
            {
                ["project"] = project.Name,
                ["shotIndex"] = groups[0].Key.ToString(),       // 回包即时配对用第一镜（其余靠磁盘判据回绿）
                ["shotIndexes"] = string.Join(",", groups.Select(g => g.Key)),
                ["batch"] = "true",
                ["outputDir"] = RenderOutDir,
                ["canvas"] = $"{_spec.Width}x{_spec.Height}@{_spec.Fps}",
                ["engine"] = _spec.Engine,
            },
        };

        _reviewCts = new CancellationTokenSource();
        _reviewBusy = true;
        BtnSendReshoot.IsEnabled = false;
        BtnSendReshoot.Content = "重渲中…";
        Win?.Status($"审片台：已唤醒 Agent 重渲 {groups.Count} 镜");
        Log($"────── 唤醒 Agent 批量重渲（{groups.Count} 镜：{string.Join("、", groups.Select(g => "#" + g.Key.ToString("D3")))}）──────");
        Log("任务包：" + Path.Combine(workDir, ReshootPrompt.RequestFile));
        Log("等 Agent 逐镜改完（本页可以继续看片，回包来了会提示）。改完点「① 组装审片成片」看新版本。");

        try
        {
            var result = await AgentBridge.CallAsync(req, _s.Config, p =>
            {
                string text = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
                if (!string.IsNullOrWhiteSpace(text)) Log("  · " + text);
            }, _reviewCts.Token);

            if (result.Ok)
            {
                Log("✓ 批量重渲回包收到。Agent 汇总：");
                Log(string.Join("\n", result.Content
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Take(30))
                        .TrimEnd());
                Win?.Status("审片台：重渲完成 —— 重新组装审片成片看新版本");
            }
            else
            {
                Log("✗ 重渲失败：" + result.Error);
                Log("任务包还在 " + workDir + "，可以改完意见再点一次。");
                Win?.Status("审片台：重渲失败");
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
        }
        catch (Exception ex)
        {
            Log("✗ 唤醒出错：" + ex.Message);
        }
        finally
        {
            _reviewBusy = false;
            BtnSendReshoot.Content = "③ 唤醒 Agent 重渲";
            UpdateMarkUi();
            RefreshMarkerStatuses();
        }
    }

    /// <summary>
    /// 把标记里的意见**幂等地**合并进 shot-overrides.json（渲染页的「修改意见」）：
    /// 每镜的旧意见里，以前由审片台写进去的行（带 [审片 前缀）先摘掉，
    /// 再按时间顺序追加本次标记的意见 —— 用户手写的意见原样保留。
    /// </summary>
    private void MergeNotesIntoOverrides()
    {
        try
        {
            Directory.CreateDirectory(StageDir);
            string path = Path.Combine(StageDir, "shot-overrides.json");

            var notes = new Dictionary<int, string>();
            var times = new Dictionary<int, DateTime>();

            // 读旧的（渲染页写的格式：{ updatedAt, notes:{"7":"..."}, noteTimes:{"7":"..."} }）
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                var root = doc.RootElement;
                if (root.TryGetProperty("notes", out var ns))
                    foreach (var p in ns.EnumerateObject())
                        if (int.TryParse(p.Name, out int i) && p.Value.ValueKind == JsonValueKind.String)
                            notes[i] = p.Value.GetString() ?? "";
                if (root.TryGetProperty("noteTimes", out var ts))
                    foreach (var p in ts.EnumerateObject())
                        if (int.TryParse(p.Name, out int i) &&
                            DateTime.TryParse(p.Value.GetString(), null,
                                              System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                            times[i] = t;
            }

            var groups = _markers.Where(m => m.ShotIndex > 0)
                                 .GroupBy(m => m.ShotIndex)
                                 .ToDictionary(g => g.Key, g => g.OrderBy(m => m.TimeSec).ToList());
            if (groups.Count == 0)
            {
                Log("没有可写入的意见 —— 标记为空，或打点时没对应上镜头。");
                return;
            }

            foreach (var (idx, list) in groups)
            {
                var keptLines = new List<string>();
                if (notes.TryGetValue(idx, out var old))
                    keptLines.AddRange(old.Split('\n')
                        .Where(l => !l.TrimStart().StartsWith("[审片", StringComparison.Ordinal))
                        .Select(l => l.TrimEnd('\r')));
                while (keptLines.Count > 0 && keptLines[^1].Length == 0) keptLines.RemoveAt(keptLines.Count - 1);

                var mine = list.Select(m =>
                    $"[审片 {m.CreatedAt:MM-dd HH:mm}] {(m.Note.Length > 0 ? m.Note : "（没写意见，保守打磨）")}");
                notes[idx] = string.Join("\n", keptLines.Concat(mine));
                times[idx] = DateTime.Now;
            }

            var payload = new Dictionary<string, object?>
            {
                ["updatedAt"] = DateTime.Now.ToString("o"),
                ["note"] = "渲染页「镜头审阅台」里用户写的单镜修改意见（按镜号）。"
                           + "不是分镜设计的一部分，是「这一镜要改什么」的指令，重渲时原样交给 Agent。"
                           + "[审片 前缀的行来自 ⑤ 审片台的成片打点，重复合并时按前缀去旧换新。",
                ["noteTimes"] = times.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString("o")),
                ["notes"] = notes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };

            // ★ 无 BOM：这份文件会被外部 Agent 读（R-13 同款坑）
            File.WriteAllText(path,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ContractJson.Utf8NoBom);

            int n = groups.Count;
            Log($"✓ {n} 镜的修改意见已写入第④页（shot-overrides.json）—— 渲染页状态灯会变成「待重渲」。");
            RefreshMarkerStatuses();
            UpdateMarkUi();
        }
        catch (Exception ex)
        {
            Log("✗ 意见写入失败：" + ex.Message);
        }
    }

    // ---- 标记的存盘与状态 ----

    private void SaveMarkers()
    {
        try
        {
            Directory.CreateDirectory(StageDir);
            var payload = new
            {
                updatedAt = DateTime.Now.ToString("o"),
                note = "⑤ 审片台的成片打点（时间 → 镜号 → 意见）。镜号是打点那一刻按成片时间轴算的。",
                markers = _markers.Select(m => new
                {
                    at = m.TimeSec,
                    shotIndex = m.ShotIndex,
                    note = m.Note,
                    createdAt = m.CreatedAt.ToString("o"),
                }).ToList(),
            };
            File.WriteAllText(Path.Combine(StageDir, MarkerFile),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ContractJson.Utf8NoBom);
        }
        catch { /* 存不下不打断看片 */ }
    }

    private void LoadMarkers()
    {
        try
        {
            string path = Path.Combine(StageDir, MarkerFile);
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("markers", out var arr)) return;

            foreach (var m in arr.EnumerateArray())
            {
                var row = new MarkerRow
                {
                    TimeSec = m.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Number
                              ? at.GetDouble() : 0,
                    ShotIndex = m.TryGetProperty("shotIndex", out var si) && si.ValueKind == JsonValueKind.Number
                                ? si.GetInt32() : -1,
                    Note = m.TryGetProperty("note", out var nt) ? nt.GetString() ?? "" : "",
                    CreatedAt = m.TryGetProperty("createdAt", out var ca) &&
                                DateTime.TryParse(ca.GetString(), null,
                                                  System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                                ? dt : DateTime.Now,
                };
                _markers.Add(row);
            }
        }
        catch { /* 坏了当没有 */ }
    }

    /// <summary>每个标记的状态（磁盘事实，和渲染页同一套思路：比 mtime）。</summary>
    private void RefreshMarkerStatuses()
    {
        try
        {
            DateTime? overridesAt = null;
            string opath = Path.Combine(StageDir, "shot-overrides.json");
            if (File.Exists(opath)) overridesAt = File.GetLastWriteTime(opath);

            foreach (var m in _markers)
            {
                string? mp4 = m.ShotIndex > 0 ? RenderTasksFile.Mp4Of(RenderOutDir, m.ShotIndex) : null;
                if (mp4 != null && File.Exists(mp4) && File.GetLastWriteTime(mp4) > m.CreatedAt)
                    m.StatusText = "已重渲·待重组装";
                else if (overridesAt.HasValue && overridesAt.Value > m.CreatedAt)
                    m.StatusText = "意见已写入④";
                else
                    m.StatusText = m.ShotIndex > 0 ? "待处理" : "未对应镜头";
            }
        }
        catch { /* 状态算不出来不影响看片 */ }
    }

    /// <summary>标记区按钮的放行 / 灰掉 + 汇总文案。</summary>
    private void UpdateMarkUi()
    {
        int n = _markers.Count;
        int withNote = _markers.Count(m => m.Note.Length > 0);
        int mapped = _markers.Count(m => m.ShotIndex > 0);

        TxtMarkSummary.Text = n == 0
            ? "看成片时发现问题：空格暂停 → 点「打点」（或意见框回车）。空格=播放/暂停。"
            : $"{n} 条标记 · {mapped} 条对应到镜头 · {withNote} 条写了意见。";

        bool hasMarkers = n > 0;
        bool hasMapped = mapped > 0;
        BtnNotesWrite.IsEnabled = hasMapped && !_reviewBusy;
        BtnSendReshoot.IsEnabled = hasMapped && !_reviewBusy && _ready;
        BtnMarkClear.IsEnabled = hasMarkers;
        BtnMark.IsEnabled = _filmLoaded;
    }

    // ========================================================
    // 界面辅助
    // ========================================================

    private async void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        // 决策来自粗剪或精剪任一（KeepRanges() 两个口径都覆盖）
        if (!_s.HasDecision || _busy) return;

        switch (Mode)
        {
            case "xml": ExportXml(); return;
            case "film": await ExportFilmAsync(); return;
            default: await ExportAudioAsync(); return;
        }
    }

    /// <summary>
    /// 出片进行中：按钮灰掉。这些回调可能从后台线程进来（进度上报），
    /// 所以先确认在 UI 线程 —— 手册里那条"await 后不保证在 UI 线程"的坑。
    /// </summary>
    private void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetBusy(busy)));
            return;
        }
        _busy = busy;
        // ★ 出片结束要把按钮交回"放行判据"手里，不能无条件打开 ——
        //   没源素材/没决策时它本来就该灰着（见 RefreshReady）。
        BtnGo.IsEnabled = !busy && _ready;
        BtnGo.Content = busy ? "出片中…" : "开始出片";
        BtnAssemble.IsEnabled = !busy;
    }

    private void SetOutButtons(bool on)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetOutButtons(on)));
            return;
        }
        BtnOpenOut.IsEnabled = on;
        BtnReveal.IsEnabled = on;
    }

    private void Log(string line)
    {
        // 进度回调是新开线程上跑的（new Progress<T> 在控制台/后台线程上没有
        // SynchronizationContext），碰控件前必须先回 UI 线程。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(line)));
            return;
        }
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
    }

    private string PickDir()
    {
        if (!string.IsNullOrWhiteSpace(_s.Config.LastOutputDir) &&
            Directory.Exists(_s.Config.LastOutputDir))
            return _s.Config.LastOutputDir;
        return Path.GetDirectoryName(_s.SourcePath) ?? "";
    }

    private void BtnOpenOut_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_s.LastExportPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _s.LastExportPath, UseShellExecute = true });
        }
        catch { }
    }

    private void BtnReveal_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_s.LastExportPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_s.LastExportPath}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }
}

// ============================================================
// ⑤ 页「轻量剪辑区」的一行 —— 一块画面 vs 它的音频段
// ============================================================
//
// 【为什么音频那一列锁死】
//   口播是这条时间轴的基准：视频里每一句话什么时候说，由音频定。
//   画面能裁能补（裁掉多余的、冻最后一帧补齐少的），而音频一旦拉伸就变调，
//   也没法再和精剪源逐样本对账。所以只允许动画面这一侧。
//
// 【Δ 的颜色怎么定】
//   整帧余量（每块不到 1 帧）本来就是设计的一部分，标灰即可；
//   明显偏长标橙（画面浪费，可考虑裁）；偏短标红 ——
//   那说明渲染产物比音频还短，多半没按安全帧渲，是最该注意的情况。
public sealed class AlignRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public int Index { get; set; }
    public string VideoPath { get; set; } = "";
    public int Fps { get; set; } = 30;

    /// <summary>该块音频段长度（秒）—— conform 的基准，只读。</summary>
    public double AudioSec { get; set; }

    private bool _hasVideo;
    public bool HasVideo
    {
        get => _hasVideo;
        set
        {
            _hasVideo = value;
            Raise(nameof(ActualText));
            Raise(nameof(DeltaText));
            Raise(nameof(DeltaBrush));
        }
    }

    private double _actual;
    /// <summary>画面实测时长（秒）。还没探测时为 0。</summary>
    public double ActualSec
    {
        get => _actual;
        set
        {
            _actual = value;
            Raise(nameof(ActualText));
            Raise(nameof(DeltaText));
            Raise(nameof(DeltaBrush));
        }
    }

    private int _adjust;
    /// <summary>用户微调，单位帧。正 = 多留几帧，负 = 少留几帧。</summary>
    public int AdjustFrames
    {
        get => _adjust;
        set
        {
            if (_adjust == value) return;
            _adjust = value;
            Raise(nameof(AdjustFrames));
            Raise(nameof(TargetText));
        }
    }

    private int BaseFrames => FFmpegAssembler.UpToFrames(AudioSec, Fps);

    /// <summary>帧对齐后的目标时长（含微调）。conform 实际会做到的时长。</summary>
    public double TargetSec => FFmpegAssembler.FramesToSec(Math.Max(1, BaseFrames + AdjustFrames), Fps);

    public string IndexText => "#" + Index.ToString("D3");
    public string AudioText => AudioSec.ToString("F3");
    public string ActualText => !HasVideo ? "缺" : (ActualSec > 0.01 ? ActualSec.ToString("F3") : "待探测");
    public string TargetText => TargetSec.ToString("F3");

    /// <summary>画面 − 音频。正 = 画面偏长（待裁），负 = 画面偏短（待冻帧补齐）。</summary>
    public double DeltaSec => (HasVideo && ActualSec > 0.01) ? ActualSec - AudioSec : 0;

    public string DeltaText => !HasVideo
        ? "缺"
        : (ActualSec <= 0.01 ? "待探测" : DeltaSec.ToString("+0.000;-0.000;0.000"));

    // 静态只读画刷 —— 每格 get 都 new 一个太浪费（219 镜的教训）。
    private static readonly Brush BrNormal = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1));
    private static readonly Brush BrLong = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush BrShort = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush BrNone = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E));

    public Brush DeltaBrush
    {
        get
        {
            if (!HasVideo || ActualSec <= 0.01) return BrNone;
            double tol = 1.0 / (Fps > 0 ? Fps : 30);
            if (DeltaSec < -tol) return BrShort;        // 画面比音频还短 —— 该注意
            if (DeltaSec > tol * 3) return BrLong;      // 明显偏长
            return BrNormal;
        }
    }
}

// ============================================================
// ⑤ 页「审片台」的一行 —— 成片打点（2026-10-04 T-27）
// ============================================================
//
// 【一行是什么】看片时在某一个时刻发现的问题：
//   什么时间 + 这是第几镜（打点那一刻按成片时间轴算好）+ 一句话意见。
// 【状态列怎么算】只看磁盘事实（和渲染页同一套思路）：
//   该镜产物比标记新 = 已重渲；shot-overrides 比标记新 = 意见已写入；否则待处理。
public sealed class MarkerRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>打点时的成片时间（秒）。</summary>
    public double TimeSec { get; set; }

    /// <summary>对应的镜号（成片时间轴反推；-1 = 没对应上镜头，只做记录）。</summary>
    public int ShotIndex { get; set; } = -1;

    /// <summary>一句话意见（可空 = 保守打磨）。</summary>
    public string Note { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string TimeText => FmtTimeStatic(TimeSec);

    public string ShotText => ShotIndex > 0 ? "#" + ShotIndex.ToString("D3") : "—";

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); }
    }

    // 静态只读画刷 —— 别每格 get 都 new（219 镜的教训）
    private static readonly Brush BrPending = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1));
    private static readonly Brush BrWritten = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush BrDone = new SolidColorBrush(Color.FromRgb(0x4A, 0xD6, 0x8A));

    public Brush StatusBrush => StatusText switch
    {
        "已重渲·待重组装" => BrDone,
        "意见已写入④" => BrWritten,
        _ => BrPending,
    };

    private static string FmtTimeStatic(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalMinutes >= 60 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss\.f");
    }
}
