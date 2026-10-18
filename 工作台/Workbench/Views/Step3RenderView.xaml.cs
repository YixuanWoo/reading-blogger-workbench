// ============================================================
// Step3RenderView.xaml.cs - 第④步：渲染 = 镜头审阅台
// ============================================================
//
// 【这一步的真实边界，先说清楚】
//   画面渲染交给 **HyperFrames**（HTML/CSS/GSAP 编排动效 → 逐镜头 MP4）。
//   工作台在这一步做的是"备料 + 派活 + 审阅 + 回扫"，不是自己画：
//     · 为每个镜头生成一张 1920×1080 的 HTML 卡片（含文案、时间、画面构思）
//     · 生成一份 render_tasks.json（谁渲染什么、多长、输出到哪、底图是哪张）
//     · 提供"回扫"：看看哪些已经渲染好了，缺哪些
//
// 【2026-09-30 改版：从"一张 219 行表格"变成"三栏审阅台"】
//   用户原话："屏幕空荡荡全是些文字。我不知道每个镜头实际播放出来什么样。"
//   表格能回答"有哪些镜头"，回答不了"这一镜长什么样"。所以：
//     左栏 = 有哪些镜头、哪个有问题（缩略图 + 状态灯，列表/网格两视图）
//     中栏 = 这一镜长什么样（真 MP4 播放 + 能改文案/构思/动效 + 写修改意见）
//     右栏 = 要不要重做（本镜状态 + 让 Agent 改这一镜 + 版本对比）
//
// 【★★ 重渲的本质 = 拉 Agent 来干活（用户 2026-09-30 拍板）】
//   用户原话："重渲显然是有某种问题需要重新搞……你要给我个地方，让我写提示词。
//   这样重新渲染才有意义。我是为了改镜头。这也就是说，这一步也需要 AI，调用你。"
//
//   所以「让 Agent 改这一镜」不是一个"重新跑一遍渲染器"的按钮，而是一次 **Agent 调用**：
//     任务包 = 用户的修改意见 + 这一镜原本的分镜设计 + 原 HTML 的**路径**（不贴源码，省 token）
//     交给 Agent 在原代码基础上改 → 只重渲这一镜 → 旧版存 .prev 供 A/B 对比
//   提示词与任务包契约见 Workbench/Agent/ReshootPrompt.cs。
//
//   任务包文件名是 agent-reshoot.json（**不是** agent-request.json）——
//   整批渲染和单镜重渲可能同时存在（一边补渲、一边调镜），同名会互相覆盖。
//
// 【成片规格（2026-09-29 拍板）】
//   所有视频统一铺同一张深蓝网格背景图、横屏 16:9、走 HyperFrames。
//   规格收在 RenderSpec 里（来源是全局配置，项目里留一份 render-spec.json）。
//
// 【写这个界面的三个坑（都是真踩过的）】
//   ① XAML 里写死 IsChecked/Text 初始值 → 控件树建到一半触发事件 → handler 摸到还没建的控件
//      → 整个页面起不来（进程级崩溃）。初始值一律放构造函数，handler 开头用 UiReady 守卫。
//   ② await 之后的续延不保证在 UI 线程 —— 碰控件的方法内部自己挡一道（见 Ui()）。
//   ③ 别给 219 个镜头各挂一个播放器 —— 只挂一个，选中哪镜才换源。
//      这是上次 C 盘崩盘（内存/页面文件被顶爆）的同类风险。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiteCut.Core.Contracts;
using LiteCut.Core.Media;
using LiteCut.Workbench.Ui;
using LiteCut.Workbench.Agent;
using Microsoft.Win32;

namespace LiteCut.Workbench.Views;

public partial class Step3RenderView : UserControl
{
    private readonly WorkbenchState _s = WorkbenchWindow.State;
    private readonly ObservableCollection<RenderShotVm> _vms = new();
    private readonly ListCollectionView _view;

    /// <summary>这次的成片规格（画幅、底图、渲染引擎）。</summary>
    private readonly RenderSpec _spec;

    /// <summary>项目里的 stages/04-render（缩略图、任务包、用户改动都落这儿）。</summary>
    private readonly string _stageDir;

    /// <summary>控件树建完了吗。XAML 加载期的事件回调一律靠它挡掉（见文件头的坑①）。</summary>
    private bool _uiReady;

    /// <summary>正在程序化填字段 —— 这期间不要标"已改"（否则选中一下就被当成修改过）。</summary>
    private bool _loadingShot;

    private RenderShotVm? _sel;
    private string _filter = "all";
    private bool _grid;
    private bool _loop = true;
    private bool _suppressSeek;

    // ---- 播放（只有一个播放器，选中才换源）----
    private DispatcherTimer? _playTimer;

    // ---- 整批渲染的实时反馈（跟分镜页同一套）----
    private CancellationTokenSource? _cts;
    private DateTime _busyStartedAt;
    private DateTime _lastProgressAt;
    private string _lastProgressKey = "";
    private readonly List<string> _agentLog = new();
    private DispatcherTimer? _busyTimer;

    // ---- 单镜重渲的实时反馈（跟整批那套分开，两件事各有各的进度）----
    private CancellationTokenSource? _reCts;
    private DateTime _reStartedAt;
    private DateTime _reProgressAt;
    private string _reProgressKey = "";
    private DispatcherTimer? _reTimer;
    private readonly List<string> _reLog = new();

    /// <summary>重渲失败过的镜头（本次会话内）。界面用它把状态灯点红。</summary>
    private readonly HashSet<int> _reshootFailed = new();

    /// <summary>用户写过的修改意见（按镜号）—— 存 shot-overrides.json，切页重进也在。</summary>
    private readonly Dictionary<int, string> _notes = new();

    /// <summary>
    /// ★ V1.10.10：每条修改意见**是什么时候写下的**（按镜号）。
    ///
    /// 【它解决什么】"这条意见落地了没有"以前只活在内存的 AppliedNote 里，
    ///   而且只有**工作台自己点的重渲**才会去设它 ——
    ///   换成外部 Agent 渲（"领任务"那条路），意见就永远挂着"待重渲"，**绿不了**。
    ///   用户 2026-10-03 的原话：「应该每次渲染出新东西，他变绿」。
    ///
    /// 【现在的判据 = 看磁盘事实】这一镜的 MP4 **比这条意见新** → 说明意见已经渲进去了。
    ///   谁渲的不重要，工作台/外部 Agent/手工跑脚本，一视同仁。
    /// </summary>
    private readonly Dictionary<int, DateTime> _noteTimes = new();

    /// <summary>老格式的 shot-overrides.json 没有逐条时间 → 拿整份文件的 updatedAt 兜底。</summary>
    private DateTime _notesStamp = DateTime.MinValue;

    /// <summary>修改意见有改动还没落盘（打字攒 800 毫秒再写，见 ScheduleNoteSave）。</summary>
    private bool _notesDirty;
    private DispatcherTimer? _noteSaveTimer;

    private const string NoteFile = "shot-overrides.json";

    /// <summary>输出目录的兜底缓存（只在"内存里没有设置"那条路上用，见 OutputDir）。</summary>
    private string? _outDirCache;

    /// <summary>渲染进行中的"活刷新"计时器（说明见构造函数末尾）。</summary>
    private DispatcherTimer? _liveTimer;

    public Step3RenderView()
    {
        InitializeComponent();

        // OutputDir = 渲染产物目录（<项目夹>\渲染产物）；_stageDir 另算（stages\04-render）。
        // 2026-10-01 前这里 OutputDir 传的是 RenderStageDir，_stageDir 再从 _spec.OutputDir
        // 反推回来 —— 「产物」和「过程文件」两个概念挤在同一个字段上，结果同一份
        // render-spec.json，分镜页与渲染页写出两个不同的 OutputDir（T-10）。现在拆开。
        _spec = RenderSpec.FromConfig(
            _s.Config,
            WorkbenchWindow.Session.Current?.Name
                ?? Path.GetFileNameWithoutExtension(_s.SourcePath),
            WorkbenchWindow.Session.Current?.RenderOutputDir ?? "",
            WorkbenchWindow.Session.Current?.Dir ?? "");

        // 渲染过程文件目录（任务包 / 提示词 / 日志 / 缩略图 / apply-preview）
        _stageDir = WorkbenchWindow.Session.Current?.RenderStageDir ?? "";

        // ★ 控件树已完整建好，从这里开始事件回调才是安全的
        _uiReady = true;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_vms);
        _view.Filter = o => Pass((RenderShotVm)o);
        ListShots.ItemsSource = _view;
        ListShots.ItemsPanel = (ItemsPanelTemplate)FindResource("TplListPanel");

        LoadShots();
        LoadNotes();
        _notesDirty = false;

        // ★ V1.10.10：这里以前是「有意见的镜头一律标 Edited（待重渲）」。
        //   那是个死循环陷阱 —— 意见渲完之后没人把它摘掉，状态灯永远橙着，
        //   用户看到的就是「明明渲完了，工作台里一片没变绿」。
        //   现在改由 RefreshOneStatus 统一算：**产物比意见新 = 意见已落地**。
        //   （判定见 IsNotePending；外部 Agent 渲的产物同样认。）

        TxtRoot.Text = "工程根：" + _s.Config.RenderAssetRoot + "　输出目录：" + OutputDir;
        TxtSpec.Text = "成片规格：" + _spec.Describe()
                       + (_spec.BackgroundExists ? "" : "   ⚠ 背景图找不到，卡片会退成纯色底");

        // 初始值放这儿设（不放 XAML，见文件头的坑①）
        ChipAll.IsChecked = true;

        // 播放器事件 + 计时器（管进度条与时间码）。
        // MediaElement 没有内建循环，播完自己回到 0 再播。
        Player.MediaOpened += (_, _) =>
        {
            if (_sel != null) TxtSubtitle.Text = _sel.Model.Text;
        };
        Player.MediaEnded += (_, _) =>
        {
            if (!_loop) { BtnPlay.Content = "播放"; return; }   // 播完回位：按钮回到"播放"态
            try { Player.Position = TimeSpan.Zero; Player.Play(); } catch { }
        };
        Player.MediaFailed += (_, _) =>
        {
            TxtNoVideo.Text = "这个 MP4 读不出来（文件可能损坏，或者还没渲完）。\n"
                              + "可以点右边「让 Agent 改这一镜」重渲一次。";
            TxtNoVideo.Visibility = Visibility.Visible;
        };
        EnsurePlayTimer();

        // 键盘（2026-10-04 用户要求）：空格 = 播放/暂停；R / Alt+R = 上一镜；F / Alt+F = 下一镜。
        // 挂 Preview（隧道）阶段 —— 焦点在哪都先到这儿，Handled=true 之后
        // 焦点上的按钮不会再把空格当"点击"吃掉。打字场景（文案/画面/意见框）单独放行。
        PreviewKeyDown += Step3_PreviewKeyDown;

        UpdateMeta();
        RefreshThumbs();
        RefreshAllStatus();
        RefreshAgentInfo();
        UpdateMeta();
        UpdateSelPanel();

        // ---- 渲染进行中的"活刷新" ----
        // 【为什么要它】整批重渲时旧 MP4 原封不动留在原位（用户要边渲边看），
        //   所以工作台一打开，看到的计数只是"那一刻"的快照。渲染要跑两三个小时，
        //   数字一动不动，用户就会以为"工作台压根不知道我在重渲"。
        //   于是每 8 秒重算一遍：状态灯逐格变绿，「待重渲」的计数往下掉。
        //
        // 【为什么故意不调 _view.Refresh()】重建列表会把用户当前选中的那一镜抽走，
        //   连带触发 SelectionChanged 清空编辑框 —— 正在写的修改意见就没了。
        //   只改属性的话，列表里状态灯照样会变色（绑定是活的），且不打断任何操作。
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _liveTimer.Tick += (_, _) =>
        {
            if (!IsVisible || _vms.Count == 0) return;   // 页面没在前台就不折腾
            ReclaimRerendered();                          // ★ 边渲边绿：外面渲好一个就认领一个
            foreach (var v in _vms) RefreshOneStatus(v);
            UpdateMeta();
        };
        _liveTimer.Start();

        Unloaded += (_, _) => Teardown();
    }

    private WorkbenchWindow? Win => WorkbenchWindow.FindOwner(this);

    // ========================================================
    // 镜头数据：优先用内存里的分镜表，没有就退回读任务包
    // ========================================================
    //
    // 【为什么要有"退回读任务包"这条路】
    //   用户完全可能打开项目直接点进渲染页（分镜表是第③步的产物，在内存里）。
    //   如果这时 _s.Shots 是空的，页面就成了一张白纸 —— 明明磁盘上
    //   stages/04-render/render-tasks.json 里躺着 219 个镜头。
    //   所以：内存里没有，就把任务包里的镜头还原出来。
    private void LoadShots()
    {
        if (_s.Shots.Count > 0)
        {
            foreach (var m in _s.Shots) _vms.Add(new RenderShotVm(m));
            return;
        }

        var restored = TryLoadTasksFromDisk();
        foreach (var m in restored) _vms.Add(new RenderShotVm(m));
    }

    // 实现搬到 RenderTasksFile 了 —— 组接页要读同一份任务包，
    // 两处各写一遍迟早会走偏（这次块级任务表新增 durationSec 就是例子）。
    private List<ShotRow> TryLoadTasksFromDisk() => RenderTasksFile.Load(_stageDir);

    // ========================================================
    // 路径：产物 / 原 HTML / 缩略图 / 上一版
    // ========================================================

    /// <summary>渲染产物目录。
    /// 优先内存里的设置 → 项目里留的 render-spec.json（重开项目后照样认得）→ 按约定推。
    /// </summary>
    private string OutputDir
    {
        get
        {
            if (!string.IsNullOrEmpty(_s.RenderOutputDir)) return _s.RenderOutputDir;

            // 下面这条路要读一次 render-spec.json。活刷新（8 秒一轮 × 219 镜）
            // 会反复访问这个属性 —— 不缓存的话，光反复读 JSON 就能把界面拖卡。
            if (_outDirCache != null) return _outDirCache;

            var saved = RenderSpec.Load(_stageDir);
            if (saved != null && !string.IsNullOrWhiteSpace(saved.OutputDir)) return _outDirCache = saved.OutputDir;

            // 兜底：按 2026-10-01 的约定推 —— <项目夹>\渲染产物\。
            // （旧口径是 <引擎根>\renders\<素材名>，那是"在引擎目录里给每个项目建子夹"
            //   时代的写法；项目自包含之后引擎目录只放模板，产物一律待在项目肚子里。）
            string proj = WorkbenchWindow.Session.Current?.Dir ?? "";
            if (!string.IsNullOrWhiteSpace(proj)) return _outDirCache = RenderSpec.RenderOutputDirOf(proj);

            return _outDirCache = Path.Combine(_s.Config.RenderAssetRoot, "renders",
                Path.GetFileNameWithoutExtension(_s.SourcePath));
        }
    }

    private string ThumbDir => Path.Combine(_stageDir, "thumbs");

    private string Mp4Of(int idx) => Path.Combine(OutputDir, $"shot_{idx:D3}.mp4");
    private string PrevMp4Of(int idx) => Path.Combine(OutputDir, $"shot_{idx:D3}.prev.mp4");

    /// <summary>这一镜现在的实现文件（HTML）。找不到原实现就退回候选里的第一个。</summary>
    private string HtmlOf(int idx, bool onlyExisting)
    {
        foreach (var c in ReshootPrompt.SourceCandidates(_spec, OutputDir, idx))
            if (File.Exists(c)) return c;
        return onlyExisting ? "" : ReshootPrompt.SourceCandidates(_spec, OutputDir, idx)[0];
    }

    // ========================================================
    // 缩略图
    // ========================================================

    private void RefreshThumbs()
    {
        foreach (var v in _vms) v.Thumb = LoadThumb(v.Model.Index);
    }

    /// <summary>
    /// 读一张缩略图。用 IgnoreImageCache + OnLoad：重渲后覆盖同一个文件名，
    /// 不这么做 WPF 会把旧图从缓存里拿出来（表现为"重渲完了缩略图没变"）。
    /// </summary>
    private ImageSource? LoadThumb(int idx)
    {
        try
        {
            string p = Path.Combine(ThumbDir, $"t_{idx:D3}.jpg");
            if (!File.Exists(p)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 168;   // 219 张全解码会吃掉几十 MB，缩到左栏用的大小
            bmp.UriSource = new Uri(p);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void BtnGenThumbs_Click(object sender, RoutedEventArgs e)
    {
        if (_vms.Count == 0) { MessageBox.Show("还没有镜头。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        if (!LiteCut.Core.Media.FFmpegLocator.TryFind() || LiteCut.Core.Media.FFmpegLocator.FFmpegPath == null)
        {
            MessageBox.Show("找不到 ffmpeg，没法抽帧。\n\n把你的 ffmpeg.exe 放到 D:\\HyperFrames\\_tools\\bin\\ 下即可。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string ff = LiteCut.Core.Media.FFmpegLocator.FFmpegPath;
        Directory.CreateDirectory(ThumbDir);

        // 只补"缺的 / 过期的"（缩略图比 MP4 旧就重抽）。
        // 串行跑，不并发 —— 抽帧本身很轻，但没必要给自己找麻烦。
        var todo = _vms.Where(v =>
        {
            string mp4 = Mp4Of(v.Model.Index);
            if (!File.Exists(mp4)) return false;
            string t = Path.Combine(ThumbDir, $"t_{v.Model.Index:D3}.jpg");
            if (!File.Exists(t)) return true;
            return File.GetLastWriteTime(t) < File.GetLastWriteTime(mp4);
        }).ToList();

        if (todo.Count == 0)
        {
            Win?.Status("缩略图都是新的，不用重抽");
            RefreshThumbs();
            return;
        }

        Win?.Status($"正在抽 {todo.Count} 张缩略图…");
        int done = 0, fail = 0;

        Task.Run(() =>
        {
            foreach (var v in todo)
            {
                string mp4 = Mp4Of(v.Model.Index);
                string outp = Path.Combine(ThumbDir, $"t_{v.Model.Index:D3}.jpg");
                // 取中点帧：比首帧更能代表这一镜的画面
                double mid = Math.Max(0.05, v.Model.DurationSec / 2.0);

                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ff,
                        Arguments = $"-ss {mid.ToString("0.###", CultureInfo.InvariantCulture)} -i \"{mp4}\" " +
                                    $"-frames:v 1 -vf scale=320:-2 -q:v 4 -y \"{outp}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(20000);
                }
                catch { }

                if (File.Exists(outp)) done++; else fail++;
            }

            Ui(() =>
            {
                RefreshThumbs();
                Win?.Status($"缩略图完成：{done} 张" + (fail > 0 ? $"，{fail} 张失败" : ""));
            });
        });
    }

    // ========================================================
    // 状态
    // ========================================================

    /// <summary>按"有没有 MP4 + 内容对不对 + 用户改没改 + 重渲失败没"算每一镜的状态。</summary>
    private void RefreshAllStatus()
    {
        ReclaimRerendered();                       // ★ 先把"外面刚渲出来的新产物"认领掉
        foreach (var v in _vms) RefreshOneStatus(v);
        _view.Refresh();
        UpdateMeta();

        // 老项目第一次打开时清单是空的，这次顺手把"现有的产物"记成基线。
        // 说一声 —— 用户看到那句"以后会标待重渲"才知道这个能力存在。
        if (_manifestBaselineBuilt)
        {
            _manifestBaselineBuilt = false;
            Win?.Status("已为现有渲染产物建立内容基线（render-manifest.json）—— "
                        + "以后改了台词/设计，或拆合过镜头，这里会直接标出「待重渲」");
        }
    }

    private void RefreshOneStatus(RenderShotVm v)
    {
        int idx = v.Model.Index;
        bool rendered = File.Exists(Mp4Of(idx));

        v.Rendered = rendered;
        v.HasPrev = File.Exists(PrevMp4Of(idx));
        v.PrevTime = v.HasPrev ? File.GetLastWriteTime(PrevMp4Of(idx)) : (DateTime?)null;

        // ★ V1.1.0 / T-16：判据从"文件在不在"升级成"**内容对不对**"。
        //
        //   以前只看 shot_007.mp4 在不在。可"文件在"不等于"内容对"：
        //     · 你拆了一镜，第 7 位现在坐的是原第 6 镜的后半，
        //       而 shot_007.mp4 还是旧第 7 镜的画面 —— 文件名对得上，内容全错；
        //     · 你改了台词 / 画面构思，MP4 是改之前渲的 —— 该重渲。
        //   这两种情况以前完全看不出来（那个"静默产出错误成片"的坑就在这儿）。
        //
        //   现在：拿**永久 ID** 去清单里对 —— 镜号会变，ID 不变，所以拆合之后照样对得上。
        bool stale = false;
        string staleWhy = "";
        if (rendered && !string.IsNullOrEmpty(v.Model.Id) &&
            Manifest.TryGetValue(v.Model.Id, out var e))
        {
            string expect = $"shot_{idx:D3}.mp4";
            if (!string.Equals(e.File, expect, StringComparison.OrdinalIgnoreCase))
            {
                stale = true;
                staleWhy = "产物过期（镜号已对不上）";
            }
            else if (!string.Equals(e.Fingerprint, RenderManifest.FingerprintOf(v.Model),
                                    StringComparison.Ordinal))
            {
                stale = true;
                staleWhy = "待重渲（台词或设计改过）";
            }
        }
        else if (rendered)
        {
            // 没有清单条目 —— 退回老口径（实现 HTML 比 MP4 新）
            stale = IsImplNewer(idx);
            if (stale) staleWhy = "待重渲 · 产物比实现旧";
        }

        v.Stale = stale;
        v.StaleWhy = staleWhy;

        // ★ V1.10.10：这一镜到底要不要重渲，统一在这儿算完，别再散落在各处设 Edited。
        //   · FieldsDirty = 审阅台里把文案/构思/动效改过了（RecomputeEdited 采集的）
        //   · NotePending = 磁盘上那条修改意见还没渲进产物里（见 IsNotePending）
        v.NotePending = IsNotePending(v);
        v.Edited = v.FieldsDirty || v.NotePending;

        // 又成了待办（改了字段 / 写了新意见）→ 上次那次"重渲失败"的记录作废，重新排队
        if (v.Edited) _reshootFailed.Remove(idx);

        if (_reshootFailed.Contains(idx)) v.Status = RenderShotVm.ShotStat.Failed;
        else if (!rendered) v.Status = RenderShotVm.ShotStat.Missing;
        else if (v.Edited) v.Status = RenderShotVm.ShotStat.Edited;
        else if (stale) v.Status = RenderShotVm.ShotStat.Stale;
        else v.Status = RenderShotVm.ShotStat.Done;
    }

    /// <summary>
    /// ★ V1.10.10：这一镜的修改意见，磁盘上**落地了没有**。
    ///
    /// 【为什么必须看磁盘，不能只靠内存】
    ///   以前"意见已落地"只写在内存的 AppliedNote 上，而它只有一处会设 ——
    ///   AfterReshoot，也就是**工作台自己点「让 Agent 改这一镜」**那条路。
    ///   可真正干活的多半是外部 Agent（"领任务"那条路）：
    ///   它把 26 镜全渲完了，工作台却一条都不知道，于是 shot-overrides.json 里
    ///   那 20 条意见永远挂着"待重渲" → 用户看到的是一片橙，没有绿。
    ///
    /// 【判据】产物比意见新
    ///   MP4 的写入时间 ≥ 这条意见的写下时间 → 意见已经在那一版画面里了 → 不算待办。
    ///   反过来：用户刚写完意见、还没重渲（产物更旧）→ 老老实实挂"待重渲"。
    ///   老格式的 shot-overrides.json 没有逐条时间 → 用整份文件的 updatedAt 兜底。
    /// </summary>
    private bool IsNotePending(RenderShotVm v)
    {
        int idx = v.Model.Index;
        if (!_notes.TryGetValue(idx, out var note) || note.Trim().Length == 0) return false;

        if (!File.Exists(Mp4Of(idx))) return true;   // 压根没产物 → 当然还没落地

        // 本次会话里刚刚重渲成功过（AfterReshoot 会设）——比 mtime 更直接
        if (note.Trim() == (v.AppliedNote ?? "").Trim()) return false;

        DateTime wrote = _noteTimes.TryGetValue(idx, out var t) ? t : _notesStamp;
        if (wrote == DateTime.MinValue) return true;   // 连时间都查不到 → 保守当"还没落地"

        return File.GetLastWriteTime(Mp4Of(idx)) < wrote;
    }

    // ========================================================
    // 渲染清单（render-manifest.json）
    // ========================================================
    //
    // 【它答的是"磁盘上这个 MP4 到底是谁的产物、是不是旧的"】
    //   键是**永久 ID**，不是镜号 —— 镜号会随拆合重排，用镜号当键，
    //   拆一次这份清单自己就过期了；用 ID 当键，镜号怎么变它都对得上。
    //
    // 【老项目第一次打开怎么办】
    //   像「思辨与立场」那种早就渲好 51 镜、但从来没有清单的项目，
    //   一打开就把 51 镜全标成"待重渲"是**假警报**（那不是事实，
    //   只是"我们不知道它什么时候渲的"）。所以第一次见到产物就记成基线，
    //   之后任何改动都会被指纹抓到。

    private Dictionary<string, RenderManifestEntry>? _manifestCache;

    private Dictionary<string, RenderManifestEntry> Manifest
    {
        get
        {
            if (_manifestCache != null) return _manifestCache;

            var map = RenderManifest.Load(OutputDir);
            try
            {
                // 建基线用**界面上这张表**（_vms）而不是 _s.Shots ——
                // 用户可能没走分镜页、直接从任务包还原出来的镜头表（见 LoadShots），
                // 两边若不是同一批对象，指纹就会对不上，白白标一堆"待重渲"。
                var shots = _vms.Count > 0 ? _vms.Select(v => v.Model).ToList() : _s.Shots;
                if (RenderManifest.BackfillMissing(OutputDir, shots, map) > 0)
                {
                    RenderManifest.Save(OutputDir, map);
                    _manifestBaselineBuilt = true;
                }
            }
            catch { }
            return _manifestCache = map;
        }
    }

    /// <summary>
    /// ★ V1.10.10：把「外面刚渲出来的新产物」认领回清单。
    ///
    /// 【为什么需要】清单里记着"这一版产物是哪一刻渲的"。如果磁盘上某个 MP4 的写入时间
    ///   **晚于**那个时刻，只有一个解释：这一镜在清单之后又被渲过一次 ——
    ///   而且不管是谁渲的（工作台点的、外部 Agent 领任务渲的、手工跑 run_render 渲的）。
    ///   那就该按**当前镜头内容**重新登记，否则它会被永远拿旧指纹比，一直标"待重渲"。
    ///
    /// 【为什么这样判是安全的】
    ///   · 用户只改台词、没重渲 → MP4 的 mtime 没动 → 不认领 → 指纹对不上 → 照样标"待重渲" ✓
    ///   · 整批重渲时旧 MP4 原封不动留在原位（用户要边渲边看）→ mtime 没动 → 不认领 ✓
    ///   · 真正被重写覆盖的那一刻才认领 → 正是"渲好一个就绿一个" ✓
    ///
    /// 【为什么不会反复写盘】认领会把 RenderedAt 刷成"现在"，
    ///   下一次 mtime 就落在它之前了 —— 同一个文件只会被认领一次。
    /// </summary>
    private int ReclaimRerendered()
    {
        try
        {
            var map = Manifest;
            int n = 0;
            foreach (var v in _vms)
            {
                var r = v.Model;
                if (string.IsNullOrEmpty(r.Id)) continue;

                string mp4 = Mp4Of(r.Index);
                if (!File.Exists(mp4)) continue;
                if (!map.TryGetValue(r.Id, out var e) || e == null) continue;

                if (!DateTime.TryParse(e.RenderedAt, CultureInfo.InvariantCulture,
                                       DateTimeStyles.RoundtripKind, out var renderedAt)) continue;

                // 留 1 秒余量：文件系统的时间戳精度与"写入瞬间"总有点出入
                if (File.GetLastWriteTime(mp4) > renderedAt.AddSeconds(1))
                {
                    RenderManifest.Record(map, r, RenderManifest.Mp4NameOf(r.Index),
                                          File.GetLastWriteTime(mp4));
                    n++;
                }
            }
            if (n > 0) RenderManifest.Save(OutputDir, map);
            return n;
        }
        catch { return 0; }
    }

    private bool _manifestBaselineBuilt;

    /// <summary>产物刚变过（重渲完成 / 交接迁移）→ 让清单下次重新读。</summary>
    private void InvalidateManifest() => _manifestCache = null;

    /// <summary>把"这一镜刚渲好"记进清单（渲染 / 重渲成功后调）。</summary>
    private void RecordRendered(RenderShotVm v)
    {
        try
        {
            var map = Manifest;
            RenderManifest.Record(map, v.Model, $"shot_{v.Model.Index:D3}.mp4");
            RenderManifest.Save(OutputDir, map);
        }
        catch { }
    }

    /// <summary>
    /// 这一镜的产物算不算"旧货"：实现 HTML 比 MP4 新。
    /// 找不到实现文件时返回 false（无从判断，就当它是好的，别乱标"待重渲"吓人）。
    /// </summary>
    private bool IsImplNewer(int idx)
    {
        try
        {
            string mp4 = Mp4Of(idx);
            if (!File.Exists(mp4)) return false;

            string impl = HtmlOf(idx, onlyExisting: true);
            if (impl.Length == 0) return false;

            return File.GetLastWriteTime(impl) > File.GetLastWriteTime(mp4);
        }
        catch { return false; }
    }

    private void UpdateMeta()
    {
        int n = _vms.Count;
        double total = _vms.Sum(v => v.Model.DurationSec);
        int done = _vms.Count(v => v.Status == RenderShotVm.ShotStat.Done);
        int editUser = _vms.Count(v => v.Status == RenderShotVm.ShotStat.Edited);
        int editStale = _vms.Count(v => v.Status == RenderShotVm.ShotStat.Stale);
        int edit = editUser + editStale;
        int miss = _vms.Count(v => v.Status == RenderShotVm.ShotStat.Missing);
        int fail = _vms.Count(v => v.Status == RenderShotVm.ShotStat.Failed);
        int imgs = _vms.Count(v => v.Model.Image.Length > 0);

        string mmss = $"{(int)(total / 60):D2}.{(int)(total % 60):D2}";
        TxtMeta.Text = $"{_spec.Project} · {n} 镜 · 成片 {mmss}";

        ChipAll.Content = $"全部 {n}";
        ChipEdit.Content = $"待重渲 {edit}";
        ChipMissing.Content = $"未出片 {miss}";

        TxtListHint.Text = _grid
            ? $"网格视图 · {_vms.Count} 格，一屏扫完找「哪一格不对劲」"
            : "镜头列表 · 每镜一卡，点一下单独看";

        TxtSummary.Text = $"共 {n} 镜 · 已出片 {done} · 待重渲 {edit}（你改过 {editUser} / 产物落后 {editStale}）"
                          + $" · 未出片 {miss} · 重渲失败 {fail} · 需要配图 {imgs}"
                          + (_s.HasDecision ? $"　|　本次成片依据：{_s.ActiveDecisionLabel}" : "")
                          + $"　|　输出 {OutputDir}";
    }

    // ========================================================
    // 列表：筛选 / 视图 / 选中
    // ========================================================

    private bool Pass(RenderShotVm v) => _filter switch
    {
        // 「待重渲」= 用户改过 OR 产物已经落后于实现。两种都要拉 Agent 重渲。
        "edit" => v.Status == RenderShotVm.ShotStat.Edited || v.Status == RenderShotVm.ShotStat.Stale,
        "missing" => v.Status == RenderShotVm.ShotStat.Missing,
        _ => true,
    };

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        var t = (ToggleButton)sender;
        _filter = (string)t.Tag;

        // 手动画单选（WPF 的 ToggleButton 不带互斥）
        ChipAll.IsChecked = ReferenceEquals(t, ChipAll);
        ChipEdit.IsChecked = ReferenceEquals(t, ChipEdit);
        ChipMissing.IsChecked = ReferenceEquals(t, ChipMissing);
        if (t.IsChecked != true) t.IsChecked = true;   // 不允许全不选

        _view.Refresh();
        UpdateMeta();
    }

    private void BtnView_Click(object sender, RoutedEventArgs e)
    {
        _grid = !_grid;
        BtnView.Content = _grid ? "切到列表" : "切到网格";
        ListShots.ItemsPanel = (ItemsPanelTemplate)FindResource(_grid ? "TplGridPanel" : "TplListPanel");
        ListShots.ItemTemplate = (DataTemplate)FindResource(_grid ? "TplCell" : "TplRow");
        UpdateMeta();
    }

    private void ListShots_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (ListShots.SelectedItem is not RenderShotVm vm) return;
        if (ReferenceEquals(vm, _sel)) return;
        LoadShot(vm);
    }

    /// <summary>选中某一镜：换播放源、把字段填进去、刷新右栏与提示词预览。</summary>
    private void LoadShot(RenderShotVm vm)
    {
        _sel = vm;
        if (!ReferenceEquals(ListShots.SelectedItem, vm))
        {
            ListShots.SelectedItem = vm;
            ListShots.ScrollIntoView(vm);
        }

        string mp4 = Mp4Of(vm.Model.Index);
        bool has = File.Exists(mp4);

        FlushNotes();          // 切镜之前，上一镜还没落盘的修改意见先写下去

        _loadingShot = true;   // 这期间不要标"已改"
        try
        {
            TxtScript.Text = vm.Model.Text;
            TxtIdea.Text = vm.Model.Description;
            TxtMotion.Text = vm.Model.Motion;
            TxtNote.Text = _notes.TryGetValue(vm.Model.Index, out var n) ? n : "";

            // ★ 基线：这一镜"刚进来时"的值。RecomputeEdited 拿它判"用户改过没有"。
            CaptureOrigin(vm);
        }
        finally { _loadingShot = false; }

        Player.Stop();
        Player.Close();
        if (has)
        {
            Player.Source = new Uri(mp4);
            TxtNoVideo.Visibility = Visibility.Collapsed;
            SubtitleBox.Visibility = Visibility.Visible;
            TxtSubtitle.Text = vm.Model.Text;
        }
        else
        {
            Player.Source = null;
            TxtNoVideo.Text = $"#{vm.Model.Index:D3} 还没出片。\n点右边「让 Agent 改这一镜」，或在整批渲染里补这一镜。";
            TxtNoVideo.Visibility = Visibility.Visible;
            SubtitleBox.Visibility = Visibility.Collapsed;
        }

        Pos.Value = 0;
        TxtTime.Text = $"00.00 / {vm.Model.DurationSec:F2}";
        BtnPlay.Content = "播放";

        UpdateSelPanel();
        RefreshPromptPreview();
    }

    private void UpdateSelPanel()
    {
        var v = _sel;
        if (v == null)
        {
            TxtSelHead.Text = "（未选中）";
            TxtSelStatus.Text = "";
            TxtSelFiles.Text = "";
            TxtPaths.Text = "";
            TxtVerNow.Text = "当前：—";
            TxtVerPrev.Text = "上一版：无";
            BtnPlayPrev.IsEnabled = false;
            BtnReshoot.IsEnabled = false;
            return;
        }

        int i = v.Model.Index;
        string mp4 = Mp4Of(i);
        bool has = File.Exists(mp4);
        string html = HtmlOf(i, onlyExisting: true);

        TxtSelHead.Text = $"#{i:D3} / 共 {_vms.Count} 镜";
        TxtSelStatus.Text = $"{v.StatusLabel}　·　时长 {v.Model.DurationSec:F3}s　·　"
                            + $"成片位置 {TimeText.Format(v.Model.StartSec)}–{TimeText.Format(v.Model.EndSec)}"
                            + (v.Edited ? "\n（你改过这一镜的文案/构思/动效 —— 重渲前这几项会随任务一起交给 Agent）" : "")
                            + (v.Stale ? "\n（磁盘上这版还是旧的：实现改过之后没重渲，所以画面里看不到新内容。重渲一次就对了）" : "");

        var sb = new StringBuilder();
        sb.AppendLine("产物：" + (has ? $"shot_{i:D3}.mp4" : "（还没有）"));
        sb.AppendLine("原实现：" + (html.Length > 0 ? Path.GetFileName(html) + "（找得到）" : "（找不到，Agent 会按分镜重写）"));
        sb.AppendLine("配图：" + (v.Model.ImageFile.Length > 0
            ? v.Model.ImageFile
            : (v.Model.Image.Length > 0 ? "需要，但还没下载" : "本镜不需要")));
        if (v.Model.Image.Length > 0)
            sb.AppendLine("配图要求：" + v.Model.Image);
        TxtSelFiles.Text = sb.ToString().TrimEnd();

        var pb = new StringBuilder();
        pb.AppendLine(mp4);
        if (html.Length > 0) pb.AppendLine(html);
        if (File.Exists(PrevMp4Of(i))) pb.AppendLine(PrevMp4Of(i));
        TxtPaths.Text = pb.ToString().TrimEnd();

        TxtVerNow.Text = has
            ? $"当前：第 {File.GetLastWriteTime(mp4):MM-dd HH:mm} 版"
            : "当前：还没有";
        TxtVerPrev.Text = v.HasPrev
            ? $"上一版：{v.PrevTime:MM-dd HH:mm}（重渲前的旧版）"
            : "上一版：无（还没重渲过）";
        BtnPlayPrev.IsEnabled = v.HasPrev;

        BtnReshoot.IsEnabled = !IsAnyBusy();
    }

    // ========================================================
    // 播放
    // ========================================================

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Step3_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 正在打字（文案 / 画面构思 / 动效 / 修改意见框）→ 空格和字母都属于文字，不抢
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        switch (e.Key)
        {
            case Key.Space when !alt:
                e.Handled = true;
                TogglePlay();
                break;

            // R = 上一镜（Reverse）、F = 下一镜（Forward）。
            // 带不带 Alt 都认（Alt+R / Alt+F 是标准键位，裸 R/F 是跟手捷径）——
            // 打字场景已被上面的守卫拦住，列表/按钮焦点上按 R/F 不会误触。
            case Key.R:
                e.Handled = true;
                StepShot(-1);
                break;

            case Key.F:
                e.Handled = true;
                StepShot(+1);
                break;
        }
    }

    /// <summary>
    /// 播放/暂停二态切换（空格与按钮共用 —— 两条入口必须一模一样快）。
    ///
    /// 【"跟手"的做法】切换只调 <c>Pause()</c>/<c>Play()</c>，**绝不碰 Position、
    /// 绝不重设 Source** —— 那两样任何一个都会触发重新定位/重新开流，
    /// 就是"按了空格卡一下"的根源。播到头再按 = 从头放（唯一的例外定位）。
    /// "正在播"的真源 = 按钮文案（MediaElement 没有 IsPlaying 属性）。
    /// </summary>
    private void TogglePlay()
    {
        if (Player.Source == null) return;

        if (BtnPlay.Content.ToString()!.Contains("暂停"))
        {
            Player.Pause();
            BtnPlay.Content = "播放";
            return;
        }

        // 播完了再按 = 从头再播一遍（MediaElement 没有内建循环）
        try
        {
            if (Player.NaturalDuration.HasTimeSpan &&
                Player.Position >= Player.NaturalDuration.TimeSpan)
                Player.Position = TimeSpan.Zero;
        }
        catch { }

        Player.Play();
        BtnPlay.Content = "暂停";
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => StepShot(-1);
    private void BtnNext_Click(object sender, RoutedEventArgs e) => StepShot(+1);

    private void StepShot(int d)
    {
        if (_sel == null || _vms.Count == 0) return;

        int pos = _vms.IndexOf(_sel);
        pos = Math.Max(0, Math.Min(_vms.Count - 1, pos + d));
        var next = _vms[pos];
        if (ReferenceEquals(next, _sel)) return;

        LoadShot(next);
        Player.Play();
        BtnPlay.Content = "暂停";      // 文案当"正在播"真源，Play 之后必须跟上
    }

    private void BtnLoop_Click(object sender, RoutedEventArgs e)
    {
        _loop = !_loop;
        BtnLoop.Content = "循环 " + (_loop ? "开" : "关");
    }

    private void BtnPlayPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        string p = PrevMp4Of(_sel.Model.Index);
        if (!File.Exists(p)) { MessageBox.Show("这一镜还没有上一版。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        // 直接切到旧版播放 —— 用户就是想看"改之前长什么样"
        Player.Stop();
        Player.Source = new Uri(p);
        TxtNoVideo.Visibility = Visibility.Collapsed;
        TxtSubtitle.Text = "（上一版）" + _sel.Model.Text;
        SubtitleBox.Visibility = Visibility.Visible;
        Player.Play();
        BtnPlay.Content = "暂停";      // 文案当"正在播"真源，Play 之后必须跟上
        Win?.Status($"正在播放 #{_sel.Model.Index:D3} 重渲前的旧版");
    }

    private void EnsurePlayTimer()
    {
        if (_playTimer != null) return;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playTimer.Tick += (_, _) => TickPlay();
        _playTimer.Start();
    }

    private void TickPlay()
    {
        if (_sel == null) return;

        if (Player.Source == null) { TxtTime.Text = "00.00 / 00.00"; return; }

        TimeSpan dur;
        try { dur = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero; }
        catch { dur = TimeSpan.Zero; }

        double d = dur.TotalSeconds > 0 ? dur.TotalSeconds : _sel.Model.DurationSec;
        double cur = 0;
        try { cur = Player.Position.TotalSeconds; } catch { }

        _suppressSeek = true;
        Pos.Maximum = Math.Max(0.1, d);
        Pos.Value = Math.Max(0, Math.Min(Pos.Maximum, cur));
        _suppressSeek = false;

        TxtTime.Text = $"{cur:00.00} / {d:00.00}";
    }

    private void Pos_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressSeek) return;
        try
        {
            if (Player.Source != null && Player.NaturalDuration.HasTimeSpan)
                Player.Position = TimeSpan.FromSeconds(e.NewValue);
        }
        catch { }
    }

    // ========================================================
    // 微调字段（改了就是"待重渲"）
    // ========================================================

    private void TxtScript_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _loadingShot || _sel == null) return;
        if (_sel.Model.Text == TxtScript.Text) return;
        _sel.Model.Text = TxtScript.Text;          // 直接改分镜表 → 随项目一起保存
        RecomputeEdited(_sel);
    }

    private void TxtIdea_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _loadingShot || _sel == null) return;
        if (_sel.Model.Description == TxtIdea.Text) return;
        _sel.Model.Description = TxtIdea.Text;
        RecomputeEdited(_sel);
    }

    private void TxtMotion_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _loadingShot || _sel == null) return;
        if (_sel.Model.Motion == TxtMotion.Text) return;
        _sel.Model.Motion = TxtMotion.Text;
        RecomputeEdited(_sel);
    }

    /// <summary>
    /// 采集"编辑框里的字段跟**刚进这一镜时**不一样没有"，然后交给 RefreshOneStatus 统一算状态。
    ///
    /// 【为什么记的是"刚进来时的值"，而不是直接看有没有编辑过】
    ///   重渲成功之后用户的意见就落地了 —— 那时状态该回到"已出片"，
    ///   而不是永远挂着"待重渲"，让人以为还得再点一次。
    ///   所以每次重渲成功都会重新采一遍基线（见 AfterReshoot）。
    ///
    /// 【★ V1.10.10 起，这里不再自己设 v.Edited】
    ///   "要不要重渲"是两件事的并集：编辑框改过（FieldsDirty）＋ 磁盘上意见没落地（NotePending）。
    ///   第二件只有 RefreshOneStatus 看得到（要读清单和文件时间），
    ///   所以状态的算法**只留一处**，免得两处各算各的、又走偏。
    /// </summary>
    private void RecomputeEdited(RenderShotVm v)
    {
        v.FieldsDirty = TxtScript.Text != v.OrigScript
                        || TxtIdea.Text != v.OrigIdea
                        || TxtMotion.Text != v.OrigMotion;

        RefreshOneStatus(v);
        _view.Refresh();
        UpdateMeta();
        UpdateSelPanel();
        RefreshPromptPreview();
    }

    /// <summary>把"这一镜刚进来时"的字段值记下来当基线（每镜只采一次）。</summary>
    private void CaptureOrigin(RenderShotVm v)
    {
        if (v.OriginCaptured) return;
        v.OrigScript = TxtScript.Text;
        v.OrigIdea = TxtIdea.Text;
        v.OrigMotion = TxtMotion.Text;
        v.OriginCaptured = true;
    }

    // ========================================================
    // 修改意见（这一步的核心：不满意在哪，写在这儿，交给 Agent）
    // ========================================================

    private void TxtNote_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _loadingShot || _sel == null) return;

        int i = _sel.Model.Index;
        string val = TxtNote.Text ?? "";
        string prev = _notes.TryGetValue(i, out var p0) ? p0 : "";

        if (val.Trim().Length == 0)
        {
            if (_notes.Remove(i)) { _noteTimes.Remove(i); ScheduleNoteSave(); }
        }
        else
        {
            _notes[i] = val;
            // ★ V1.10.10：只有**内容真的变了**才刷新写下时间 ——
            //   选中某一镜时程序化填框不算改（_loadingShot 已经挡了一道，这里再保一层）。
            if (prev != val) _noteTimes[i] = DateTime.Now;
            ScheduleNoteSave();
        }

        RecomputeEdited(_sel);
    }

    private void Hint_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        var btn = (Button)sender;
        string txt = (string)btn.Tag;

        string cur = TxtNote.Text ?? "";
        if (cur.Trim().Length == 0) TxtNote.Text = txt;
        else if (!cur.Contains(txt)) TxtNote.Text = cur.TrimEnd() + "\n" + txt;

        TxtNote.CaretIndex = (TxtNote.Text ?? "").Length;
        TxtNote.Focus();
    }

    private void ExpPrompt_Expanded(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        RefreshPromptPreview();
    }

    /// <summary>
    /// 把"这次重渲会交给 Agent 的东西"原样显示出来。
    /// 用户要能自己核对，而不是盲点一个按钮。
    /// </summary>
    private void RefreshPromptPreview()
    {
        if (!_uiReady) return;

        if (_sel == null) { TxtPromptPreview.Text = "（先选中一个镜头）"; return; }

        try
        {
            string user = TxtNote?.Text ?? "";
            string body = ReshootPrompt.BuildUserPrompt(
                _sel.Model, _spec, _stageDir, OutputDir, _stageDir, user, CollectEdits());

            TxtPromptPreview.Text =
                "───── 系统提示词 ─────\r\n" + ReshootPrompt.SystemPrompt() +
                "\r\n───── 任务正文 ─────\r\n" + body;
        }
        catch (Exception ex)
        {
            TxtPromptPreview.Text = "（提示词生成失败：" + ex.Message + "）";
        }
    }

    /// <summary>用户在审阅台里直接改过、跟"刚进这一镜时"不一样的字段。空 = 没改。</summary>
    private Dictionary<string, string> CollectEdits()
    {
        var d = new Dictionary<string, string>();
        if (_sel == null) return d;

        if (TxtScript.Text != _sel.OrigScript) d["口播文案"] = TxtScript.Text;
        if (TxtIdea.Text != _sel.OrigIdea)
            d["画面构思"] = string.IsNullOrWhiteSpace(TxtIdea.Text) ? "（清空了）" : TxtIdea.Text;
        if (TxtMotion.Text != _sel.OrigMotion)
            d["动效"] = string.IsNullOrWhiteSpace(TxtMotion.Text) ? "（清空了）" : TxtMotion.Text;
        return d;
    }

    // ---- 修改意见的落盘（切页重进、重开项目都还在）----

    private void LoadNotes()
    {
        try
        {
            string p = Path.Combine(_stageDir, NoteFile);
            if (!File.Exists(p)) return;

            _notesStamp = File.GetLastWriteTime(p);   // 兜底用（老格式没有逐条时间）

            using var doc = JsonDocument.Parse(File.ReadAllText(p, Encoding.UTF8));
            var root = doc.RootElement;

            if (root.TryGetProperty("updatedAt", out var u) &&
                DateTime.TryParse(u.GetString(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind, out var ut))
                _notesStamp = ut;

            if (root.TryGetProperty("notes", out var notes))
                foreach (var prop in notes.EnumerateObject())
                    if (int.TryParse(prop.Name, out int idx) && prop.Value.ValueKind == JsonValueKind.String)
                        _notes[idx] = prop.Value.GetString() ?? "";

            // ★ V1.10.10：逐条写下时间。老文件没有这一段 → 一律退回 _notesStamp。
            if (root.TryGetProperty("noteTimes", out var times))
                foreach (var prop in times.EnumerateObject())
                    if (int.TryParse(prop.Name, out int idx) &&
                        DateTime.TryParse(prop.Value.GetString(), CultureInfo.InvariantCulture,
                                          DateTimeStyles.RoundtripKind, out var t))
                        _noteTimes[idx] = t;
        }
        catch { /* 文件坏了不影响使用 */ }
    }

    private void SaveNotes()
    {
        try
        {
            Directory.CreateDirectory(_stageDir);
            var payload = new
            {
                updatedAt = DateTime.Now.ToString("o"),
                note = "渲染页「镜头审阅台」里用户写的单镜修改意见（按镜号）。"
                       + "不是分镜设计的一部分，是「这一镜要改什么」的指令，重渲时原样交给 Agent。",
                // ★ V1.10.10：noteTimes = 每条意见的写下时刻。工作台靠它跟产物的写入时间比，
                //   判断"这条意见是不是已经渲进去了" —— 不然意见渲完也摘不掉，状态灯永远橙着。
                noteTimes = _noteTimes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString("o")),
                notes = _notes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };
            // ★ 无 BOM：这份「单镜修改意见」重渲时原样交给 Agent 读，带 BOM 会让解析器报错。
            File.WriteAllText(Path.Combine(_stageDir, NoteFile),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ContractJson.Utf8NoBom);

            _notesDirty = false;
        }
        catch { /* 存不下不打断用户 */ }
    }

    /// <summary>
    /// 打字的每一键都落盘太浪费 —— 攒 800 毫秒再写一次。
    /// 但切镜、离开页面的时候必须把它冲掉，否则最后一句修改意见会丢。
    /// </summary>
    private void ScheduleNoteSave()
    {
        _notesDirty = true;
        if (_noteSaveTimer == null)
        {
            _noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _noteSaveTimer.Tick += (_, _) => { _noteSaveTimer!.Stop(); SaveNotes(); };
        }
        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    /// <summary>切镜 / 离开页面时把还没落盘的修改意见冲掉（否则最后几笔会丢）。</summary>
    private void FlushNotes()
    {
        _noteSaveTimer?.Stop();
        if (_notesDirty) SaveNotes();
    }

    // ========================================================
    // ★★ 让 Agent 改这一镜（单镜重渲）
    // ========================================================

    private async void BtnReshoot_Click(object sender, RoutedEventArgs e)
    {
        var project = WorkbenchWindow.Session.Current;
        if (_sel == null || project == null)
        {
            MessageBox.Show("要先打开一个项目 —— 任务包要落到项目的 stages\\04-render 里。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string note = (TxtNote.Text ?? "").Trim();
        string htmlNow = HtmlOf(_sel.Model.Index, onlyExisting: true);

        // 没写修改意见也允许重渲（只做保守打磨），但要说清会发生什么
        if (note.Length == 0)
        {
            var r = MessageBox.Show(
                $"你还没写「这一镜要改什么」。\n\n" +
                $"这样重渲会走保守路线：Agent 只检查明显毛病（文字压边、动效跳变、首尾接不上），" +
                $"不做设计上的改动。\n\n继续吗？（想精确改，先在中栏的输入框里写几句）",
                "还没写修改意见", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }

        string workDir = project.RenderStageDir;
        Directory.CreateDirectory(workDir);

        var req = new AgentRequest
        {
            Kind = "reshoot",
            Title = ReshootPrompt.BuildTitle(project.Name, _sel.Model.Index),
            SystemPrompt = ReshootPrompt.SystemPrompt(),
            UserPrompt = ReshootPrompt.BuildUserPrompt(
                _sel.Model, _spec, workDir, OutputDir, _stageDir, note, CollectEdits()),
            WorkDir = workDir,
            WaitMinutes = 60,                       // 单镜不需要 12 小时
            RequestFileName = ReshootPrompt.RequestFile,
            ResponseFileName = ReshootPrompt.ResponseFile,
            PromptFileName = ReshootPrompt.PromptFile,
            Meta = new Dictionary<string, string>
            {
                ["project"] = project.Name,
                ["shotIndex"] = _sel.Model.Index.ToString(),
                ["durationSec"] = _sel.Model.DurationSec.ToString("0.###", CultureInfo.InvariantCulture),
                ["hasUserNote"] = (note.Length > 0).ToString(),
                ["originalHtml"] = htmlNow,
                ["outputDir"] = OutputDir,
                ["canvas"] = $"{_spec.Width}x{_spec.Height}@{_spec.Fps}",
                ["background"] = _spec.Background,
                ["engine"] = _spec.Engine,
            },
        };

        int idx = _sel.Model.Index;
        var vm = _sel;

        _reCts = new CancellationTokenSource();
        _reStartedAt = DateTime.Now;
        _reProgressAt = DateTime.Now;
        _reProgressKey = "";
        _reLog.Clear();
        _reshootFailed.Remove(idx);

        var cfg = _s.Config;
        ReLog(AgentBridge.UseHttp(cfg)
            ? $"通道：直连 {cfg.AgentModel}（重渲要动文件，一般仍由任务包执行者做）"
            : "通道：任务包文件 —— 等 Agent 领取（WorkBuddy 里说「领任务」）");
        ReLog($"#{idx:D3} · " + (note.Length > 0 ? "带修改意见" : "无修改意见（保守打磨）")
              + (htmlNow.Length > 0 ? " · 有原实现可改" : " · 找不到原实现"));

        SetReBusy(true);

        // 实时进度钩子：单镜重渲的进展 = 有没有新 MP4 + Agent 写的进度文字
        string WatchDetail()
        {
            var parts = new List<string>();
            string prog = "";
            try
            {
                string pf = Path.Combine(workDir, "agent-progress.json");
                if (File.Exists(pf))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pf, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("text", out var t)) prog = t.GetString() ?? "";
                }
            }
            catch { }

            if (prog.Length > 0) parts.Add(prog);
            string prev = PrevMp4Of(idx);
            if (File.Exists(prev)) parts.Add("旧版已备份");
            return string.Join(" · ", parts);
        }

        try
        {
            var result = await AgentBridge.CallAsync(req, cfg, p =>
            {
                Ui(() =>
                {
                    string text = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
                    TxtReProgress.Text = text;

                    if (text != _reProgressKey)
                    {
                        _reProgressKey = text;
                        _reProgressAt = DateTime.Now;
                        ReLog(text);
                    }
                });
            }, _reCts.Token, WatchDetail);

            Ui(() =>
            {
                SetReBusy(false);

                if (!result.Ok)
                {
                    _reshootFailed.Add(idx);
                    ReLog("失败：" + result.Error);
                    RefreshOneStatus(vm);
                    _view.Refresh();
                    UpdateMeta();
                    UpdateSelPanel();
                    Win?.Status($"#{idx:D3} 重渲失败");
                    MessageBox.Show($"#{idx:D3} 重渲失败：\n\n{result.Error}\n\n" +
                                    "任务包还在 " + workDir + "，Agent 可以继续处理；" +
                                    "也可以改完修改意见再点一次。",
                                    "重渲失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ReLog("完成：" + Trim1(result.Content, 120));
                AfterReshoot(idx, vm, result.Content);
            });
        }
        catch (OperationCanceledException)
        {
            Ui(() =>
            {
                SetReBusy(false);
                ReLog("已取消");
                Win?.Status($"#{idx:D3} 重渲已取消");
            });
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                SetReBusy(false);
                ReLog("出错：" + ex.Message);
                Win?.Status("重渲出错：" + ex.Message);
            });
        }
    }

    /// <summary>单镜重渲回来了：刷新这一镜的状态与缩略图，把结果讲给用户听。</summary>
    private void AfterReshoot(int idx, RenderShotVm vm, string summary)
    {
        string mp4 = Mp4Of(idx);
        bool ok = File.Exists(mp4);

        if (ok)
        {
            // ★ 这一镜的意见已经落地了：
            //   ① 记住"已落地的意见原文"，免得同一条意见被当成"还没落地"；
            //   ② 重新采一次字段基线 —— 之后状态回到"已出片"，而不是永远挂着"待重渲"。
            vm.AppliedNote = (_notes.TryGetValue(idx, out var n) ? n : "").Trim();
            vm.Thumb = LoadThumb(idx);          // 缩略图变了，立刻反映到左栏

            // ③ 记进度清单：磁盘上这一镜的产物 = **现在这一版内容**。
            //    不记的话，下次 RefreshOneStatus 会拿"渲染那一刻的旧指纹"去比，
            //    明明刚渲完却还标着「待重渲」（这会把人逼疯）。
            InvalidateManifest();
            RecordRendered(vm);
        }

        RefreshOneStatus(vm);
        _view.Refresh();
        UpdateMeta();
        UpdateSelPanel();

        // 重新播一次新版本，用户马上能看到改没改好
        if (ok && ReferenceEquals(_sel, vm))
        {
            Player.Stop();
            Player.Source = new Uri(mp4);
            TxtNoVideo.Visibility = Visibility.Collapsed;
            SubtitleBox.Visibility = Visibility.Visible;
            Player.Play();
        }

        // 基线重采（在界面上还要再点一次"重渲"的话，判断从这一刻起算）
        if (ok)
        {
            vm.OriginCaptured = false;
            if (ReferenceEquals(_sel, vm))
            {
                _loadingShot = true;
                try { CaptureOrigin(vm); } finally { _loadingShot = false; }
            }
            RecomputeEditedQuiet(vm);
        }

        string msg = $"#{idx:D3} 重渲完成。\n\n" +
                     (vm.HasPrev ? "旧版已备份成 shot_" + idx.ToString("D3") + ".prev.mp4，右栏可以点「看上一版」对比。\n\n" : "") +
                     "Agent 汇总：\n" + Trim1(summary, 500);

        Win?.Status($"#{idx:D3} 重渲完成 —— " + (vm.HasPrev ? "可对比上一版" : ""));
        MessageBox.Show(msg, "重渲回包", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>只重算状态、不触发提示词预览刷新（重渲收尾时用，免得把界面搅乱）。</summary>
    private void RecomputeEditedQuiet(RenderShotVm v)
    {
        v.FieldsDirty = TxtScript.Text != v.OrigScript
                        || TxtIdea.Text != v.OrigIdea
                        || TxtMotion.Text != v.OrigMotion;

        RefreshOneStatus(v);
        _view.Refresh();
        UpdateMeta();
        UpdateSelPanel();
    }

    private void BtnCancelR_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        TxtProgress.Text = "正在取消…";
    }

    private void BtnCancelRe_Click(object sender, RoutedEventArgs e)
    {
        _reCts?.Cancel();
        TxtReProgress.Text = "正在取消…";
    }

    // ========================================================
    // 整批渲染（保留原有能力：给还缺的镜头一次性派活）
    // ========================================================

    private async void BtnRenderAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_vms.Count == 0) return;

        var project = WorkbenchWindow.Session.Current;
        string projName = project?.Name ?? Path.GetFileNameWithoutExtension(_s.SourcePath);

        if (!GenerateTaskPackage(notify: false)) return;
        string outDir = _s.RenderOutputDir;

        string workDir = project?.RenderStageDir
                         ?? Path.Combine(Path.GetTempPath(), "litecut-render", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(workDir);

        int needImg = _vms.Count(v => v.Model.Image.Length > 0);

        var req = new AgentRequest
        {
            Kind = "render",
            Title = $"镜头渲染 · {projName} · {_vms.Count} 个镜头",
            SystemPrompt = RenderPrompt.SystemPrompt(_spec),
            UserPrompt = RenderPrompt.BuildUserPrompt(
                projName, _spec, workDir, outDir, _vms.Select(v => v.Model).ToList(), _stageDir,
                project?.AssetsDir ?? ""),
            WorkDir = workDir,
            WaitMinutes = 720,
            Meta = new Dictionary<string, string>
            {
                ["project"] = projName,
                ["shots"] = _vms.Count.ToString(),
                ["needImages"] = needImg.ToString(),
                ["canvas"] = $"{_spec.Width}x{_spec.Height}@{_spec.Fps}",
                ["background"] = _spec.Background,
                ["engine"] = _spec.Engine,
                ["outputDir"] = outDir,
            },
        };

        _cts = new CancellationTokenSource();
        _busyStartedAt = DateTime.Now;
        _lastProgressAt = DateTime.Now;
        _lastProgressKey = "";
        _agentLog.Clear();

        var cfg = _s.Config;
        AgentLogR(AgentBridge.UseHttp(cfg)
            ? $"通道：直连 {cfg.AgentModel}（渲染类任务一般仍由任务包执行者完成）"
            : "通道：任务包文件 —— 等 Agent 领取（WorkBuddy 里说「领任务」）");
        AgentLogR($"任务：{_vms.Count} 镜 · {needImg} 张搜图 · 输出 {outDir}");

        SetBusy(true);

        string WatchDetail()
        {
            int done = CountRendered(outDir);
            string prog = "";
            try
            {
                string pf = Path.Combine(workDir, "agent-progress.json");
                if (File.Exists(pf))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pf, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("text", out var t)) prog = t.GetString() ?? "";
                }
            }
            catch { }

            return done > 0
                ? $"已出片 {done}/{_vms.Count}" + (prog.Length > 0 ? " · " + prog : "")
                : prog;
        }

        try
        {
            var result = await AgentBridge.CallAsync(req, cfg, p =>
            {
                Ui(() =>
                {
                    string text = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
                    TxtProgress.Text = text;
                    if (text != _lastProgressKey)
                    {
                        _lastProgressKey = text;
                        _lastProgressAt = DateTime.Now;
                        AgentLogR(text);
                    }
                });
            }, _cts.Token, WatchDetail);

            Ui(() =>
            {
                SetBusy(false);
                if (!result.Ok)
                {
                    AgentLogR("失败：" + result.Error);
                    Win?.Status("Agent 渲染失败：" + result.Error);
                    MessageBox.Show("Agent 渲染失败：\n\n" + result.Error + "\n\n" +
                                    "任务包还在 " + workDir + "，Agent 可以继续处理这个任务。",
                                    "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                AgentLogR("完成：" + Trim1(result.Content, 120));
                AfterRenderDone(workDir, result.Content);
            });
        }
        catch (OperationCanceledException)
        {
            Ui(() =>
            {
                SetBusy(false);
                AgentLogR("已取消 —— 任务包保留在 " + workDir);
                RefreshAllStatus();
                UpdateMeta();
            });
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                SetBusy(false);
                Win?.Status("调用出错：" + ex.Message);
                AgentLogR("调用出错：" + ex.Message);
            });
        }
    }

    /// <summary>回包到了：回扫输出目录 + 读图片清单填回 ImageFile。</summary>
    private void AfterRenderDone(string workDir, string summary)
    {
        int rendered = CountRendered(OutputDir);
        int gotImg = 0;

        try
        {
            string ip = Path.Combine(workDir, "agent-images.json");
            if (File.Exists(ip))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ip, Encoding.UTF8));
                var byIndex = new Dictionary<int, string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    int idx = el.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    string file = el.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
                    if (idx > 0 && file.Length > 0) byIndex[idx] = file;
                }

                foreach (var v in _vms)
                    if (byIndex.TryGetValue(v.Model.Index, out var f) && v.Model.ImageFile != f)
                    {
                        v.Model.ImageFile = f;
                        gotImg++;
                    }
            }
        }
        catch { }

        // ★ 整批渲染回来了：给所有出片的镜头登记清单。
        //   依据是"这次的任务包就是拿当前镜头表（_s.Shots）生成的" ——
        //   所以现在磁盘上这些 MP4 就是当前这版内容的产物。
        //   （这一步很重要：不登记的话，它们会一直被拿旧指纹去比，永远标着「待重渲」。）
        InvalidateManifest();
        foreach (var v in _vms)
            if (File.Exists(Mp4Of(v.Model.Index))) RecordRendered(v);

        RefreshAllStatus();
        RefreshThumbs();
        UpdateMeta();

        string msg = $"渲染完成：{rendered}/{_vms.Count} 镜已出片。" +
                     (gotImg > 0 ? $"\n图片清单已回填 {gotImg} 条。" : "") +
                     $"\n\nAgent 汇总：\n{Trim1(summary, 400)}" +
                     $"\n\n输出目录：{OutputDir}";
        Win?.Status($"渲染完成 {rendered}/{_vms.Count} 镜 —— 到第④步组接就能拼片");
        MessageBox.Show(msg, "渲染回包", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private int CountRendered(string outDir)
    {
        int n = 0;
        if (!Directory.Exists(outDir)) return 0;
        foreach (var v in _vms)
            if (File.Exists(Path.Combine(outDir, $"shot_{v.Model.Index:D3}.mp4"))) n++;
        return n;
    }

    // ========================================================
    // 任务包生成 / 回扫
    // ========================================================

    private void BtnGenTasks_Click(object sender, RoutedEventArgs e) => GenerateTaskPackage(notify: true);

    private bool GenerateTaskPackage(bool notify)
    {
        if (_vms.Count == 0) return false;

        string projName = Path.GetFileNameWithoutExtension(_s.SourcePath);
        if (string.IsNullOrWhiteSpace(projName)) projName = "untitled";

        // 2026-10-01 起：渲染产物落项目文件夹（<项目夹>\渲染产物\），
        // 不再写进引擎目录（D:\HyperFrames）—— 项目拷走就是完整的。
        string outDir;
        var projectForOut = WorkbenchWindow.Session.Current;
        if (projectForOut != null && !string.IsNullOrWhiteSpace(projectForOut.Dir))
            outDir = Path.Combine(projectForOut.Dir, "渲染产物");
        else
        {
            string root = _s.Config.RenderAssetRoot;
            if (string.IsNullOrWhiteSpace(root)) root = @"D:\HyperFrames";
            outDir = Path.Combine(root, "renders", projName);
        }
        _s.RenderOutputDir = outDir;

        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            MessageBox.Show("建不了输出目录：" + ex.Message, "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        // ★ 卡片底稿：只补缺的，**别覆盖已有的实现** ——
        //   重开一次任务包会把渲染执行者改过的实现冲掉。
        //   工作台生成的这张只是"底稿"，真正的实现由渲染执行者写。
        int made = 0;
        foreach (var v in _vms)
        {
            string cardPath = Path.Combine(outDir, $"shot_{v.Model.Index:D3}.html");
            if (File.Exists(cardPath)) continue;
            File.WriteAllText(cardPath, BuildCardHtml(v.Model, _spec), ContractJson.Utf8NoBom);
            made++;
        }

        // ★ 任务包：走 RenderTaskPackage 这个**唯一写入点**（V1.1.0 起分镜页交接
        //   镜头表时也要重写它，两处各写一遍迟早走偏 —— 与 T-10 同一类教训）。
        //   这里传的是**已交接版**（_vms 就是从 _s.Shots 来的），不是编辑中的草稿。
        string projLabel = string.IsNullOrWhiteSpace(_spec.Project) ? projName : _spec.Project;
        string taskPath = RenderTaskPackage.Write(
            _vms.Select(v => v.Model).ToList(), _spec, outDir, _stageDir, projLabel, _s.DecisionDurationSec,
            WorkbenchWindow.Session.Current?.AssetsDir ?? "");

        var project = WorkbenchWindow.Session.Current;
        if (project != null)
        {
            try
            {
                var specForDisk = RenderSpec.FromConfig(_s.Config, project.Name, outDir, project.Dir);
                RenderSpec.Write(project.RenderStageDir, specForDisk);
            }
            catch { }
        }

        TxtRoot.Text = "引擎根：" + _s.Config.RenderAssetRoot + "　渲染产物：" + outDir;
        UpdateMeta();

        if (notify)
        {
            Win?.Status($"已生成 {made} 张卡片到 {outDir}");
            MessageBox.Show($"任务包已就绪。{Environment.NewLine}{Environment.NewLine}" +
                            $"画幅：{_spec.Width}×{_spec.Height} · {_spec.Fps}fps{Environment.NewLine}" +
                            $"底图：{_spec.BackgroundName}{Environment.NewLine}" +
                            $"渲染：{RenderEngines.Label(_spec.Engine)}{Environment.NewLine}{Environment.NewLine}" +
                            $"新生成卡片：{made} 张（已有的不覆盖）{Environment.NewLine}" +
                            $"目录：{outDir}{Environment.NewLine}{Environment.NewLine}" +
                            "可以点「交给 Agent 渲染（整批）」，也可以自己渲好后回来扫描。",
                            "渲染任务包已就绪", MessageBoxButton.OK, MessageBoxImage.Information);
            TryOpenFolder(outDir);
        }

        return true;
    }

    private void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        RefreshAllStatus();
        RefreshThumbs();
        int found = CountRendered(OutputDir);

        Win?.Status(found > 0
            ? $"已渲染 {found}/{_vms.Count} 镜 —— 缺的可以点「交给 Agent 渲染（整批）」补"
            : "输出目录里还没有 MP4");
        UpdateMeta();
    }

    // ========================================================
    // 忙碌状态 / 计时 / 日志
    // ========================================================

    // ========================================================
    // 【T-06】应用到下游 —— 只「宣布 + 算 Δ 预览」，不碰任何文件
    // ========================================================
    //
    // 业务隔离：conform（裁帧/补帧）本质属「组装」，放第⑤步才顺 ——
    //   ④ 若提前改写块，等于 mutate 源、多一代 h264 损耗、重渲还难对账。
    //   所以这里**只读**：逐个 ffprobe 拿实测时长，把 Δ 摆出来给人看。
    //   完整逐块清单写 stages/04-render/apply-preview.json，渲染产物一个字节不动。
    private async void BtnApplyDown_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyBusy() || _vms.Count == 0) return;

        int fps = _spec.Fps > 0 ? _spec.Fps : 30;
        string outDir = OutputDir;

        // 与第⑤步同一套口径：块的成片窗口按"时长累加"排（表里万一有缝也不会整体错位）
        var blocks = new List<FilmBlock>();
        double cursor = 0;
        foreach (var v in _vms.OrderBy(v => v.Model.Index))
        {
            double dur = v.Model.DurationSec;
            if (dur <= 0.01) continue;
            blocks.Add(new FilmBlock
            {
                Index = v.Model.Index,
                DesignStartSec = cursor,
                DesignEndSec = cursor + dur,
                VideoPath = Mp4Of(v.Model.Index),
            });
            cursor += dur;
        }
        if (blocks.Count == 0) return;

        BtnApplyDown.IsEnabled = false;
        ProgressRow.Visibility = Visibility.Visible;
        BarRender.IsIndeterminate = true;
        TxtLog.Visibility = Visibility.Collapsed;
        TxtProgress.Text = $"正在逐块探测时长…（0/{blocks.Count}）";

        var prog = new Progress<int>(i => TxtProgress.Text = $"正在逐块探测时长…（{i}/{blocks.Count}）");
        List<FFmpegAssembler.BlockDelta> deltas;
        try
        {
            deltas = await Task.Run(() => FFmpegAssembler.PreviewDelta(blocks, fps, prog));
        }
        catch (Exception ex)
        {
            BarRender.IsIndeterminate = false;
            TxtProgress.Text = "✗ 探测失败：" + ex.Message;
            BtnApplyDown.IsEnabled = true;
            return;
        }
        BtnApplyDown.IsEnabled = true;
        BarRender.IsIndeterminate = false;
        BarRender.Value = 100;

        ReportApply(deltas, outDir, fps);
    }

    /// <summary>把 Δ 预览摆明白：界面给汇总，完整清单写 stages 留痕。</summary>
    private void ReportApply(List<FFmpegAssembler.BlockDelta> deltas, string outDir, int fps)
    {
        if (deltas.Count == 0) { TxtProgress.Text = "没有可应用的块。"; return; }

        var have = deltas.Where(d => d.HasVideo).ToList();
        var miss = deltas.Where(d => !d.HasVideo).Select(d => d.Index).ToList();

        double audioTotal = deltas.Sum(d => d.AudioSec);
        double targetTotal = deltas.Sum(d => d.TargetSec);
        double avg = have.Count > 0 ? have.Average(d => d.DeltaSec) : 0;
        double maxPos = have.Count > 0 ? have.Max(d => d.DeltaSec) : 0;
        double minNeg = have.Count > 0 ? have.Min(d => d.DeltaSec) : 0;

        string head = miss.Count == 0
            ? $"✓ 已应用到下游：{have.Count} 块宣布为第⑤步「画面成片」的正式源"
            : $"⚠ 已应用到下游，但 {miss.Count} 块没有产物（第⑤步会拼不上）";
        TxtProgress.Text =
            head + "\n"
            + $"逐块 Δ：平均 {avg * 1000:+0.0;-0.0}ms · 最大 +{maxPos * 1000:F0}ms · 最小 {minNeg * 1000:F0}ms"
            + "（正 = 画面偏长待裁 / 负 = 偏短待冻帧补齐）\n"
            + (miss.Count > 0
                ? "缺产物：" + string.Join("、", miss.Take(12).Select(i => "#" + i.ToString("D3")))
                  + (miss.Count > 12 ? $" …（共 {miss.Count} 块）" : "") + "\n"
                : "")
            + $"目标总长 {FFmpegAssembler.Fmt(targetTotal)}"
            + $"（音频 {FFmpegAssembler.Fmt(audioTotal)} ＋ 帧对齐余量 {(targetTotal - audioTotal) * 1000:F0}ms）"
            + "　·　产物文件未做任何改动";

        // 留痕：只写 stages 下的元数据，绝不碰渲染产物
        try
        {
            if (!string.IsNullOrWhiteSpace(_stageDir))
            {
                Directory.CreateDirectory(_stageDir);
                var note = new
                {
                    应用时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    输出目录 = outDir,
                    帧率 = fps,
                    块数 = deltas.Count,
                    已出片块数 = have.Count,
                    缺产物镜号 = miss,
                    音频总长秒 = Math.Round(audioTotal, 3),
                    目标总长秒 = Math.Round(targetTotal, 3),
                    平均Δ毫秒 = Math.Round(avg * 1000, 1),
                    逐块 = deltas.Select(d => new
                    {
                        镜号 = d.Index,
                        有产物 = d.HasVideo,
                        画面实测秒 = Math.Round(d.ActualSec, 4),
                        音频段秒 = Math.Round(d.AudioSec, 4),
                        目标秒 = Math.Round(d.TargetSec, 4),
                        Δ毫秒 = Math.Round(d.DeltaSec * 1000, 1),
                    }).ToList(),
                };
                string p = Path.Combine(_stageDir, "apply-preview.json");
                File.WriteAllText(p, JsonSerializer.Serialize(note, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                AgentLogR("应用到下游：逐块 Δ 清单已写 " + Path.GetFileName(p));
            }
        }
        catch (Exception ex)
        {
            AgentLogR("应用到下游：清单写盘失败（不影响使用）—— " + ex.Message);
        }
    }

    private bool IsAnyBusy() => _cts != null || _reCts != null;

    private void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => SetBusy(busy))); return; }

        if (!busy) _cts = null;

        BtnRenderAgent.IsEnabled = !busy && _vms.Count > 0;
        BtnCancelR.IsEnabled = busy;
        BtnGenTasks.IsEnabled = !busy && _vms.Count > 0;
        BtnScan.IsEnabled = !busy;
        BtnReshoot.IsEnabled = !busy && _reCts == null && _sel != null;
        ProgressRow.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BarRender.IsIndeterminate = true;

        if (busy)
        {
            TxtProgress.Text = "准备任务包…";
            TxtElapsed.Text = "已用时 00:00";
            TxtLog.Visibility = Visibility.Collapsed;
            StartBusyTimer();
        }
        else StopBusyTimer();
    }

    private void AgentLogR(string msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AgentLogR(msg))); return; }

        _agentLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
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

    private void StopBusyTimer() { _busyTimer?.Stop(); _busyTimer = null; }

    private void TickBusyClock()
    {
        var total = DateTime.Now - _busyStartedAt;
        var since = DateTime.Now - _lastProgressAt;
        TxtElapsed.Text = $"已用时 {total:hh\\:mm\\:ss} · 最后进展 {since.TotalSeconds:0} 秒前";

        if (since.TotalMinutes > 15)
        {
            TxtElapsed.Foreground = (Brush)FindResource("Danger");
            TxtElapsed.Text += " —— 超过一刻钟没有新出片，建议取消后重新派任务";
        }
        else if (since.TotalMinutes > 5)
        {
            TxtElapsed.Foreground = (Brush)FindResource("Warn");
            TxtElapsed.Text += "（有一会儿没动静了）";
        }
        else TxtElapsed.Foreground = (Brush)FindResource("TextDim");
    }

    // ---- 单镜重渲那一套 ----

    private void SetReBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => SetReBusy(busy))); return; }

        if (!busy) _reCts = null;

        BtnReshoot.IsEnabled = !busy && _cts == null && _sel != null;
        BtnCancelRe.IsEnabled = busy;
        BtnRenderAgent.IsEnabled = !busy && _cts == null && _vms.Count > 0;
        ReProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (busy)
        {
            TxtReProgress.Text = "准备任务包…";
            TxtReElapsed.Text = "已用时 00:00";
            TxtReLog.Visibility = Visibility.Collapsed;
            StartReTimer();
        }
        else StopReTimer();
    }

    private void ReLog(string msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ReLog(msg))); return; }

        _reLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_reLog.Count > 8) _reLog.RemoveAt(0);

        TxtReLog.Text = string.Join("\n", _reLog);
        TxtReLog.Visibility = _reLog.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartReTimer()
    {
        StopReTimer();
        _reTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reTimer.Tick += (_, _) => TickReClock();
        _reTimer.Start();
    }

    private void StopReTimer() { _reTimer?.Stop(); _reTimer = null; }

    private void TickReClock()
    {
        var total = DateTime.Now - _reStartedAt;
        var since = DateTime.Now - _reProgressAt;
        TxtReElapsed.Text = $"已用时 {total:hh\\:mm\\:ss} · 最后进展 {since.TotalSeconds:0} 秒前";

        if (since.TotalMinutes > 8)
        {
            TxtReElapsed.Foreground = (Brush)FindResource("Danger");
            TxtReElapsed.Text += " —— 太久没动静，可以取消重来";
        }
        else if (since.TotalMinutes > 3)
        {
            TxtReElapsed.Foreground = (Brush)FindResource("Warn");
            TxtReElapsed.Text += "（有一会儿没动静了）";
        }
        else TxtReElapsed.Foreground = (Brush)FindResource("TextDim");
    }

    // ========================================================
    // 杂项
    // ========================================================

    private void MidCol_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_uiReady) return;
        // 预览区跟着中栏宽度走 16:9，但别超过 320 高（再高就把下面的编辑区挤没了）
        double w = MidCol.ActualWidth - 20;
        if (w <= 20) return;
        PreviewBox.Height = Math.Max(150, Math.Min(w * 9.0 / 16.0, 320));
    }

    private void RefreshAgentInfo()
    {
        var cfg = _s.Config;
        if (AgentBridge.UseHttp(cfg))
        {
            TxtAgentInfo.Text = $"已配置直连：{cfg.AgentModel}";
            TxtAgentInfo.Foreground = (Brush)FindResource("TextDim");
        }
        else
        {
            TxtAgentInfo.Text = "走任务包通道：派活到 stages\\04-render，Agent 领取（说「领任务」）";
            TxtAgentInfo.Foreground = (Brush)FindResource("Warn");
        }
    }

    private void BtnOpenOut_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(OutputDir))
        {
            MessageBox.Show("输出目录还不存在：" + OutputDir, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TryOpenFolder(OutputDir);
    }

    private void BtnOpenHtml_Click(object sender, RoutedEventArgs e)
    {
        if (_sel == null) return;
        string h = HtmlOf(_sel.Model.Index, onlyExisting: true);
        if (h.Length == 0)
        {
            MessageBox.Show("找不到这一镜的 HTML 实现。\n\n找过这些位置：\n"
                            + string.Join("\n", ReshootPrompt.SourceCandidates(_spec, OutputDir, _sel.Model.Index)),
                            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = h, UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>把一段界面操作送回 UI 线程执行（从哪调都安全，见文件头的坑②）。</summary>
    private void Ui(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a);
    }

    private static string Trim1(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    private void Teardown()
    {
        FlushNotes();          // 还没落盘的修改意见先写下去
        StopBusyTimer();
        StopReTimer();
        _liveTimer?.Stop();
        _liveTimer = null;
        _playTimer?.Stop();
        _playTimer = null;
        try { Player.Stop(); Player.Close(); Player.Source = null; } catch { }
    }

    private static void TryOpenFolder(string dir)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    /// <summary>
    /// 生成一张"底稿"卡片的 HTML。
    /// 真正的画面实现由渲染执行者写；这张只是给执行者的起点，也是"没有执行者时"的兜底。
    /// </summary>
    private static string BuildCardHtml(ShotRow shot, RenderSpec spec)
    {
        string idea = string.IsNullOrWhiteSpace(shot.Description) ? "（待补画面构思）" : shot.Description;

        int w = spec.Width > 0 ? spec.Width : 1920;
        int h = spec.Height > 0 ? spec.Height : 1080;
        double sc = w / 1280.0;

        string bgUri = spec.BackgroundUri;
        string bgCss = bgUri.Length > 0
            ? $"background-image:url(\"{bgUri}\");\n    background-size:cover;\n    background-position:center;"
            : "";

        return $@"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<title>镜头 {shot.Index:D2}</title>
<style>
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  body {{
    width:{w}px; height:{h}px; overflow:hidden; position:relative;
    background-color:#0b1020;
    {bgCss}
    color:#f3ecdc;
    font-family:'Noto Serif SC','Songti SC','Microsoft YaHei',serif;
    display:flex; flex-direction:column;
    justify-content:center; align-items:center; padding:{80 * sc:0}px;
  }}
  .idx {{
    position:absolute; top:{32 * sc:0}px; left:{40 * sc:0}px;
    font-size:{20 * sc:0}px; color:rgba(243,236,220,.42); letter-spacing:.1em;
  }}
  .dur {{
    position:absolute; top:{32 * sc:0}px; right:{40 * sc:0}px;
    font-size:{20 * sc:0}px; color:rgba(243,236,220,.42);
  }}
  .text {{
    font-size:{44 * sc:0}px; line-height:1.7; text-align:center;
    max-width:{w * 0.78:0}px; letter-spacing:.03em;
    text-shadow:0 2px 24px rgba(0,0,0,.55);
  }}
  .idea {{
    position:absolute; bottom:{36 * sc:0}px; left:0; right:0; text-align:center;
    font-size:{19 * sc:0}px; color:rgba(243,236,220,.48); font-style:italic;
  }}
</style>
</head>
<body>
  <div class=""idx"">镜头 {shot.Index:D2}</div>
  <div class=""dur"">{shot.DurationSec:F1}s</div>
  <div class=""text"">{System.Net.WebUtility.HtmlEncode(shot.Text)}</div>
  <div class=""idea"">{System.Net.WebUtility.HtmlEncode(idea)}</div>
</body>
</html>";
    }
}

// ============================================================
// 审阅台的一个镜头
// ============================================================
public sealed class RenderShotVm : INotifyPropertyChanged
{
    /// <summary>
    /// 这一镜在审阅台里的状态。
    /// 名字是 ShotStat 不是 Status —— 因为下面那个同名属性才该叫 Status，
    /// C# 不允许类型和成员同名（会报 CS0102）。
    /// </summary>
    public enum ShotStat { Done, Edited, Stale, Missing, Failed }

    public ShotRow Model { get; }

    public RenderShotVm(ShotRow model) => Model = model;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => Model.Index;
    public string IndexText => Model.Index.ToString("D3");
    public string HeadText => $"#{Model.Index:D3}";
    public string CellText => Model.Index.ToString("D3");

    public string SubText =>
        $"{Model.DurationSec:F2}s · " +
        $"{TimeText.Format(Model.StartSec)}–{TimeText.Format(Model.EndSec)}";

    private ShotStat _status = ShotStat.Missing;
    public ShotStat Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(Status));
            Raise(nameof(StatusLabel));
            Raise(nameof(StatusBrush));
        }
    }

    public string StatusLabel => Status switch
    {
        ShotStat.Done => "已出片",
        ShotStat.Edited => "待重渲",
        ShotStat.Stale => StaleWhy.Length > 0 ? StaleWhy : "待重渲",
        ShotStat.Failed => "重渲失败",
        _ => "未出片",
    };

    // 状态灯的颜色。做成静态只读 —— 每次 get 都 new 一个画刷，219 个格子刷起来太浪费。
    private static readonly Brush BrOk = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush BrEdit = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush BrFail = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush BrNone = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E));

    public Brush StatusBrush => Status switch
    {
        ShotStat.Done => BrOk,
        ShotStat.Edited => BrEdit,
        ShotStat.Stale => BrEdit,     // 跟"你改过"同一种待办：都要重渲，所以同色
        ShotStat.Failed => BrFail,
        _ => BrNone,
    };

    // ---- 审阅台的编辑基线 ----
    //
    // 【为什么要有"基线"】
    //   判"这一镜要不要重渲"不能只看"编辑框被改过" ——
    //   重渲成功之后用户的意见已经落地了，状态该回到"已出片"，
    //   而不是永远挂着"待重渲"让人以为还得再点一次。
    //   所以每个镜头记一份"刚进来时的值"，重渲成功后再采一遍。
    /// <summary>第一次把这一镜载进编辑框时采的基线（每镜只采一次）。</summary>
    public bool OriginCaptured { get; set; }
    public string OrigScript { get; set; } = "";
    public string OrigIdea { get; set; } = "";
    public string OrigMotion { get; set; } = "";

    /// <summary>已经重渲落地过的那句修改意见（跟当前输入框里的比，一样 = 不用再渲）。</summary>
    public string AppliedNote { get; set; } = "";

    /// <summary>★ V1.10.10：审阅台里的文案/构思/动效跟"刚进这一镜时"不一样（编辑框层面的改动）。</summary>
    public bool FieldsDirty { get; set; }

    /// <summary>
    /// ★ V1.10.10：磁盘上那条修改意见**还没渲进产物里**（产物比意见旧，或者压根没产物）。
    /// 跟 FieldsDirty 一起决定要不要重渲 —— 两个都算"待重渲"，但来源完全不同。
    /// </summary>
    public bool NotePending { get; set; }

    private bool _edited;
    public bool Edited
    {
        get => _edited;
        set { if (_edited == value) return; _edited = value; Raise(nameof(Edited)); }
    }

    private bool _rendered;
    public bool Rendered
    {
        get => _rendered;
        set { if (_rendered == value) return; _rendered = value; Raise(nameof(Rendered)); }
    }

    // ---- 产物是不是"旧货" ----
    //
    // 【为什么不能只看"文件在不在"】
    //   整批重渲时，旧版 MP4 还留在原位（用户要边渲边看），
    //   光看"文件存在"会把 219 镜全判成"已出片"，用户就看不到"还欠多少镜没渲"。
    //   真正的判据是：**MP4 的修改时间 < 这一镜实现 HTML 的修改时间** ——
    //   说明实现被改过、但还没拿它重渲过。
    //   这把尺子必须跟引擎侧 run_render.py 的 is_fresh() 一模一样，
    //   否则会出现"工作台说已渲好、渲染脚本却还在重渲"的自相矛盾。
    private bool _stale;
    public bool Stale
    {
        get => _stale;
        set { if (_stale == value) return; _stale = value; Raise(nameof(Stale)); Raise(nameof(StatusLabel)); }
    }

    /// <summary>
    /// "为什么算旧货"的人话（V1.1.0 起）。三种来源：
    ///   · 产物过期（镜号已对不上）—— 拆合之后文件还没搬到位
    ///   · 待重渲（台词或设计改过）—— 内容指纹对不上
    ///   · 待重渲 · 产物比实现旧 —— 老口径（还没建清单的项目）
    /// 空串 = 没有具体原因，就用默认文案。
    /// </summary>
    public string StaleWhy { get; set; } = "";

    private bool _hasPrev;
    public bool HasPrev
    {
        get => _hasPrev;
        set { if (_hasPrev == value) return; _hasPrev = value; Raise(nameof(HasPrev)); }
    }

    public DateTime? PrevTime { get; set; }

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; Raise(nameof(Thumb)); }
    }

    private void Raise(string n) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
