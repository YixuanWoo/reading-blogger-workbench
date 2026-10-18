// ============================================================
// StepFineCutView.xaml.cs - 第②步：精剪（编辑器内嵌版）
// ============================================================
//
// 【这一步在整条链里的位置】
//   ① 算法粗剪（80% 的体力活）→ ② 人工精剪（判断权在你）→ ③ 分镜 → …
//
// 【为什么编辑器直接长在页面里】
//   编辑器（EditorView）就是原工作台主窗口换了个壳。旧版是"点按钮
//   开独立窗口"，用户反馈：从工作台长出来的编辑器，凭什么还要
//   独立打开？所以现在直接铺满本页，全屏时吃满整窗。
//
// 【数据流】
//   进：State.KeepRanges()（粗剪结果，可能含用户在第①步的逐段修改）
//       → EditorView.LoadPreCutProject() 铺成时间轴块
//   出：切到其它步骤 / 点「应用到下游」时 → EditorView.GetTimelineKeeps()
//       → 和粗剪结果比对，有差异才覆盖 State.FineCutKeeps
//
// 【实例缓存】
//   编辑器在 WorkbenchWindow 里缓存成单例：切换步骤不销毁，
//   撤销历史、播放状态都保得住。离开本页时 SyncBack() 接回数据。
//
// 【为什么用"有差异才覆盖"】
//   用户可能只是切过来看了一眼就走。这时不该留下"已精剪"的标记，
//   否则第①步改了粗剪参数，下游却还在用一份"看起来精剪过"的旧数据。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteCut.Core.Contracts;
using LiteCut.Core.Media;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench.Views;

public partial class StepFineCutView : UserControl, INavPage, ISaveSync
{
    private static readonly WorkbenchState _s = WorkbenchWindow.State;

    /// <summary>内嵌编辑器。懒创建，随工作台常驻。</summary>
    private EditorView? _editor;

    /// <summary>编辑器里当前铺的是哪一版粗剪。换了引用 = 粗剪重跑过。</summary>
    private CutPlan? _loadedPlan;

    /// <summary>
    /// 工作台换了源文件 → 本页缓存的编辑器里还压着旧素材的时间轴，下次激活要清掉。
    ///
    /// 【为什么这件事不能省】
    ///   编辑器是 KeepAlive 的（撤销历史、播放状态要保住），切页不销毁。
    ///   换了素材之后如果不清，本页会拿"上一支片子剪好的块"当当前项目的
    ///   精剪结果，一路带进下游 —— 等于拿 A 片的区间去剪 B 片。
    /// </summary>
    public static bool PendingEditorReset { get; set; }

    /// <summary>
    /// **进本页那一刻**编辑器里的保留区间。
    ///
    /// 【为什么需要这个基线】
    ///   `SyncBack()`（离开本页时调）的老规矩是"编辑器里的时间轴和当前决策不一样就接管"。
    ///   这在"改了再走"的场景下是对的，但漏了一种情况：
    ///   用户在第①步明确点了「改用粗剪结果」，然后**只是来精剪页看一眼**就走 ——
    ///   编辑器里还是上次的精剪时间轴，和现在的粗剪决策当然不一样，
    ///   于是 SyncBack 又把下游**静悄悄抢回精剪**。用户会觉得"我明明切过去了，怎么又回去了"。
    ///
    ///   判别办法：比"进页时"和"离开时"编辑器里是不是同一份。
    ///   一样 = 这次没动过 → 不碰决策；不一样 = 真改了 → 接管。
    ///   这样"看一眼"和"改了"就能分开，跟"要切换必须明确点按钮"是同一个口径。
    /// </summary>
    private List<(double Start, double End)>? _enteredKeeps;

    public StepFineCutView()
    {
        InitializeComponent();
        _instance = this;
        Loaded += (s, e) => Activate();
    }

    /// <summary>本页唯一实例。KeepAlive 保证了最多一个（见 NavNode.KeepAlive）。</summary>
    private static StepFineCutView? _instance;

    /// <summary>
    /// 录音台要重建母带之前调一次：让编辑器松手 + 标记"下次进页必须重铺"。
    ///
    /// 【为什么走静态方法】
    ///   录音台拿不到本页实例（导航页是外壳建的），而 KeepAlive 让本页可能
    ///   一直活着并压着播放状态和样本缓存。放这里比在录音台里到处找实例干净。
    /// </summary>
    public static void PrepareForNewMaster()
    {
        if (_instance != null)
        {
            _instance._editor?.ReleaseAudio();
            _instance._loadedPlan = null;
            _instance._enteredKeeps = null;
        }
        PendingEditorReset = true;
    }

    private WorkbenchWindow? Win => WorkbenchWindow.FindOwner(this);

    /// <summary>
    /// 内嵌编辑器（懒创建）。用属性而不是直连字段，是为了让
    /// "还原项目时间轴"和"按粗剪结果铺时间轴"两条路都能一句话拿到编辑器。
    /// </summary>
    private EditorView Editor
    {
        get
        {
            if (_editor == null)
            {
                _editor = new EditorView();
                EditorHost.Children.Add(_editor);
            }
            return _editor;
        }
    }

    // ========================================================
    // 导航生命周期（由工作台外壳调用）
    // ========================================================

    /// <summary>导航到本页</summary>
    public void OnEnter() => Activate();

    /// <summary>离开本页：把时间轴交回共享状态</summary>
    public void OnLeave() => SyncBack();

    /// <summary>
    /// 点「保存」时被外壳叫一下：保证存档里拿到的是最新的时间轴，
    /// 而不是"上次切页时"的那一份。
    /// </summary>
    public void SyncBeforeSave() => CaptureToState();

    /// <summary>
    /// 应用退出时由 WorkbenchWindow 调用：释放编辑器的音频引擎和上传服务。
    /// </summary>
    public void ShutdownEditor() => _editor?.Shutdown();

    // ========================================================
    // 激活（每次本页显示时调用；Loaded 也会触发，幂等）
    // ========================================================

    public void Activate()
    {
        // ⓪ 换过源文件 → 先把编辑器里残留的旧时间轴清掉（详见 PendingEditorReset 的说明）
        if (PendingEditorReset)
        {
            PendingEditorReset = false;
            _editor?.ClearTimeline();
            _loadedPlan = null;
        }

        // ① 打开项目时带来的时间轴快照 → 原样还原。这是最高优先级：
        //    哪怕没有粗剪决策（Plan），只要存过时间轴就得还回来。
        if (TryRestoreFromProject()) return;

        // ② 判断"有没有东西可显示"。
        //
        //    ★ 不能只看 _s.HasPlan —— 这是个真踩过的坑：
        //      本页加进可视树时 WPF 会先触发 OnEnter（外壳调 Activate），
        //      布局完成后 Loaded 又会再触发一次 Activate。
        //      第二次进来时快照已经消费掉了，若此刻只认 _s.HasPlan，
        //      一个"存过时间轴但没存粗剪决策"的项目就会被判成没内容，
        //      刚还原好的时间轴当场被擦成占位图。
        //      把"编辑器里已经有块"也算作有内容，这个坑就没了。
        bool hasContent = _s.HasPlan || (_editor != null && !_editor.IsEmpty);

        if (!hasContent)
        {
            Placeholder.Visibility = Visibility.Visible;
            EditorHost.Visibility = Visibility.Collapsed;
            TxtPlanSummary.Text = "";
            TxtFineCutStatus.Text = "";
            TxtDownstream.Text = "";
            CoarseUpdateBar.Visibility = Visibility.Collapsed;
            BtnApply.IsEnabled = false;
            BtnRevert.Visibility = Visibility.Collapsed;
            BtnFullscreen.Visibility = Visibility.Collapsed;
            _enteredKeeps = null;
            return;
        }

        Placeholder.Visibility = Visibility.Collapsed;
        EditorHost.Visibility = Visibility.Visible;
        // "应用到下游"把当前时间轴变成下游用的剪辑决策。以前只认粗剪
        // （HasPlan），但纯精剪项目（直接导素材手工剪、没跑过粗剪）的
        // 决策就是时间轴本身 —— 也该能应用。走到这里 hasContent 必为真，
        // 所以按钮直接放开。
        BtnApply.IsEnabled = true;
        BtnFullscreen.Visibility = Visibility.Visible;

        // ③ 粗剪和当前时间轴的关系 —— 这条以前写得会吃数据，2026-09-29 改过：
        //
        //    旧行为：粗剪换了引用就 `LoadPreCutProject(...)` 直接重铺。
        //            于是"回第①步重跑一次粗剪"会把你在本页手工挪过的块、
        //            调过的音量、加的淡入淡出**全部清掉**，而且没有任何提示。
        //
        //    新规矩：粗剪更新**只提示，不动手**。
        //            · 编辑器还是空的 → 没什么可丢，直接铺（顺手就把事办了）
        //            · 编辑器里有东西 → 只亮提示条，要不要重铺你自己点
        if (_s.HasPlan && Editor.IsEmpty)
        {
            Editor.LoadPreCutProject(_s.SourcePath, ToTuples(_s.KeepRanges()));
            _loadedPlan = _s.Plan;
            HideCoarseUpdate();
        }
        else if (_s.HasPlan && !ReferenceEquals(_loadedPlan, _s.Plan))
        {
            ShowCoarseUpdate();
        }
        else
        {
            HideCoarseUpdate();
        }

        // 记下"进页那一刻的样子"，离开时用它判断用户到底动没动手（见 _enteredKeeps）
        _enteredKeeps = _editor != null ? _editor.GetTimelineKeeps() : null;

        RefreshSummary();
    }

    /// <summary>点亮"粗剪已更新"提示条。</summary>
    private void ShowCoarseUpdate()
    {
        int segs = _s.Plan!.ComputeTimeline().Count;
        double total = _s.Plan.ComputeTimeline().Sum(t => t.SourceEndSec - t.SourceStartSec);
        TxtCoarseUpdate.Text =
            $"⚠ 录音台重新铺了母带（{segs} 段 · {WorkbenchState.FormatDuration(total)}），" +
            "但当前时间轴还是你上次精剪的结果 —— 精剪成果不会被自动覆盖。" +
            "想换成新母带的铺排，点右边按钮（会丢弃当前时间轴）。";
        CoarseUpdateBar.Visibility = Visibility.Visible;
    }

    private void HideCoarseUpdate() => CoarseUpdateBar.Visibility = Visibility.Collapsed;

    /// <summary>用户明确要求：用录音台新铺的母带重铺时间轴。</summary>
    private void BtnReloadCoarse_Click(object sender, RoutedEventArgs e)
    {
        if (!_s.HasPlan) return;

        var r = MessageBox.Show(
            "用录音台最新铺的母带重新铺一遍时间轴？\n\n" +
            "当前时间轴上的手工改动（挪过的块、裁过的边界、音量和淡化）会全部丢弃。\n" +
            "这一步不能撤销。",
            "用新母带重铺", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        Editor.LoadPreCutProject(_s.SourcePath, ToTuples(_s.KeepRanges()));
        _loadedPlan = _s.Plan;
        HideCoarseUpdate();
        _enteredKeeps = Editor.GetTimelineKeeps();
        RefreshSummary();
        Win?.Status("已按录音台最新铺的母带重铺时间轴");
    }

    /// <summary>
    /// 打开项目后第一次进本页：把存档里的完整时间轴还原出来。
    /// 快照只消费一次（消费后清空），之后进页就走正常的增量刷新。
    /// </summary>
    private bool TryRestoreFromProject()
    {
        var pending = _s.PendingTimelineSnapshot;
        if (pending == null || pending.Count == 0) return false;
        _s.PendingTimelineSnapshot = null;

        Placeholder.Visibility = Visibility.Collapsed;
        EditorHost.Visibility = Visibility.Visible;
        // 同 Activate 里的口径：有内容就能应用，不要求必须跑过粗剪
        BtnApply.IsEnabled = true;
        BtnFullscreen.Visibility = Visibility.Visible;

        var snap = new TimelineSnapshot
        {
            PlayheadSec = _s.TimelinePlayheadSec,
            ViewStartSec = _s.TimelineViewStartSec,
            SecPerPixel = _s.TimelineSecPerPixel,
        };
        foreach (var p in pending) snap.Clips.Add(ToClip(p));

        int missing = Editor.RestoreTimeline(snap);

        // 记下"当前铺的是哪一版粗剪"，否则下一句判断会以为粗剪换了、给出多余提示
        _loadedPlan = _s.Plan;
        HideCoarseUpdate();
        _enteredKeeps = _editor?.GetTimelineKeeps();
        RefreshSummary();

        string msg = $"已还原上次的精剪时间轴：{snap.Clips.Count} 块";
        if (missing > 0) msg += $"（{missing} 块的源文件找不到了，块还在但放不出声）";
        Win?.Status(msg);
        return true;
    }

    // ========================================================
    // 数据接回（离开本页 / 点「应用到下游」时）
    // ========================================================

    /// <summary>
    /// 把编辑器里的时间轴完整存进共享状态（供"保存项目"用）。
    ///
    /// 【为什么不在这里判断"有没有改动"】
    ///   有没有改动交给 ProjectSession 的指纹比对去管。
    ///   这里多做一次比较，就多一处可能判断错的地方。
    ///   存一份完整块列表很便宜（就是复制几个对象）。
    /// </summary>
    public void CaptureToState()
    {
        if (_editor == null) return;

        var snap = _editor.CaptureTimeline();
        _s.TimelineSnapshot = snap.Clips.Select(ToPart).ToList();
        _s.TimelinePlayheadSec = snap.PlayheadSec;
        _s.TimelineViewStartSec = snap.ViewStartSec;
        _s.TimelineSecPerPixel = snap.SecPerPixel;
        // 盖戳：这份时间轴是剪当前这个源文件的。换素材后它自动作废。
        _s.TimelineSourcePath = _s.SourcePath;
    }

    /// <summary>
    /// 把编辑器里的时间轴读回工作台。有差异才覆盖精剪覆盖层。
    /// 由 WorkbenchWindow 在切换视图前调用。
    /// </summary>
    public void SyncBack()
    {
        if (_editor == null) return;

        // 先无条件把完整时间轴存下来（存档要的是全量信息）
        CaptureToState();

        // 【2026-09-29 放开 HasPlan 门槛】
        //   以前没有粗剪决策时直接返回，纯精剪项目（直接导素材手工剪）的
        //   结果永远传不到下游。决策层的口径是"精剪 > 粗剪"，没有粗剪时
        //   精剪就是唯一的决策层 —— 更该接回。比对基准 KeepRanges() 两种情况都覆盖。
        List<(double Start, double End)> after;
        try
        {
            after = _editor.GetTimelineKeeps();
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取精剪结果失败：" + ex.Message, "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (after.Count == 0)
        {
            // 用户把所有块都删了 —— 大概率是误操作，不覆盖现有决策
            Win?.Status("编辑器里没有保留任何片段 —— 已忽略本次结果（原有决策不变）");
            return;
        }

        // ---- 本次进页之后到底动过手没有？----
        //  没动过 = 只是来看了一眼 → 不要动下游的控制权。
        //  （否则用户在第①步点了「改用粗剪结果」，来精剪页看一眼就把粗剪选择冲掉了。）
        if (_enteredKeeps != null && SameTimeline(_enteredKeeps, after))
            return;

        // ---- 和当前决策比对：有差异才接管 ----
        var before = ToTuples(_s.KeepRanges());
        if (SameTimeline(before, after))
        {
            _enteredKeeps = after;   // 顺手对齐基线
            return;                  // 没改动，什么都不用做
        }

        double total = after.Sum(k => k.Item2 - k.Item1);
        _s.FineCutKeeps = after.Select(k => new FFmpegExporter.Range(k.Start, k.End)).ToList();
        _enteredKeeps = after;

        Win?.Status($"精剪结果已接管：{after.Count} 段，共 {Fmt(total)}" +
                    " —— 分镜/渲染/导出将使用精剪后的时间轴");
        RefreshSummary();
    }

    // ========================================================
    // 按钮
    // ========================================================

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        // 纯精剪项目（没跑过粗剪）也能应用 —— 决策就是时间轴本身
        if (_editor == null) return;

        var after = _editor.GetTimelineKeeps();
        if (after.Count == 0)
        {
            Win?.Status("时间轴上没有任何片段 —— 请先恢复内容，或点「还原为粗剪」");
            return;
        }

        // 先把完整时间轴存进共享状态 —— 顺序很重要：
        // 后面 RefreshSummary 要读 HasFineCut，而"完整快照要不要盖源文件戳"
        // 也得在应用之前定下来。
        CaptureToState();

        var before = ToTuples(_s.KeepRanges());
        bool same = SameTimeline(before, after);

        if (same && _s.ActiveDecision == DecisionSource.Fine)
        {
            // 确实没改动，而且下游本来就在用它 —— 如实说，不要装作干了活
            Win?.Status($"时间轴没有改动，下游继续使用{_s.ActiveDecisionLabel}");
            RefreshSummary();
            return;
        }

        double total = after.Sum(k => k.Item2 - k.Item1);
        _s.FineCutKeeps = after.Select(k => new FFmpegExporter.Range(k.Start, k.End)).ToList();
        _enteredKeeps = after;   // 已经明确应用过了，离开本页不该再被判成"改了"
        RefreshSummary();

        // 【2026-09-29】这里原来只说一句话就完了，用户点了按钮看界面毫无变化，
        //   以为"什么都没发生"（他报的就是这个）。现在至少有三处可见反馈：
        //   按钮文案变成"✓ 已应用到下游"、顶部出现"下游当前使用：精剪 · …"、
        //   状态栏这句话里带上具体段数和时长。
        BtnApply.Content = "✓ 已应用到下游";
        Win?.Status($"已应用到下游：精剪 · {after.Count} 段 · {WorkbenchState.FormatDuration(total)}"
                    + " —— 分镜 / 渲染 / 组接导出现在都按这份在走");
    }

    private void BtnRevert_Click(object sender, RoutedEventArgs e)
    {
        // 没跑过粗剪的项目没有"粗剪结果"可还原 —— 还原后下游就没有任何决策了
        string msg = _s.HasPlan
            ? "丢弃精剪修改，还原成录音台刚铺好的样子？"
            : "这个项目还没有录音铺排，丢弃精剪决策后下游将没有可用内容。确定丢弃？";
        var r = MessageBox.Show(msg, "精剪",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        _s.FineCutKeeps = null;
        Win?.Status(_s.HasPlan ? "已还原为录音铺排" : "已丢弃精剪决策（本项目没有录音铺排）");

        // 编辑器同步重铺成录音台给的母带铺排
        if (_editor != null && _s.HasPlan)
        {
            _editor.LoadPreCutProject(_s.SourcePath, ToTuples(_s.KeepRanges()));
            _loadedPlan = _s.Plan;
            HideCoarseUpdate();
            _enteredKeeps = _editor.GetTimelineKeeps();
        }
        BtnApply.Content = "应用到下游";
        RefreshSummary();
    }

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
    {
        Win?.ToggleFullscreen();
    }

    // ========================================================
    // 概览刷新
    // ========================================================

    private void RefreshSummary()
    {
        if (_s.HasPlan)
        {
            var keeps = _s.Plan!.ComputeTimeline();
            double total = keeps.Sum(t => t.SourceEndSec - t.SourceStartSec);
            double srcDur = _s.SourceDurationSec;
            string src = srcDur > 0 ? $"源素材 {Fmt(srcDur)}" : "";
            TxtPlanSummary.Text = $"录音铺排：{keeps.Count} 段 · {Fmt(total)} {src}".TrimEnd();
        }
        else
        {
            TxtPlanSummary.Text = "";
        }

        if (_s.FineCutKeeps is { Count: > 0 } fine)
        {
            double total = fine.Sum(k => k.End - k.Start);
            TxtFineCutStatus.Text = $"● 已精剪：{fine.Count} 段 · {Fmt(total)}";
            BtnRevert.Visibility = Visibility.Visible;
        }
        else
        {
            TxtFineCutStatus.Text = _s.HasPlan ? "未精剪 · 用录音铺排" : "";
            BtnRevert.Visibility = Visibility.Collapsed;
        }

        // 下游此刻到底在用哪一层 —— 以前这是个看不见的推论，用户点了按钮
        // 却"看不到任何变化"（2026-09-29 报的）。现在直接写在标题栏上。
        string label = _s.ActiveDecisionLabel;
        TxtDownstream.Text = _s.ActiveDecision switch
        {
            DecisionSource.Fine => $"下游正在使用：{label}",
            DecisionSource.Coarse => $"下游正在使用：{label}（点「应用到下游」改用精剪结果）",
            _ => "下游尚未应用任何决策",
        };
        TxtDownstream.Foreground = (Brush)FindResource(
            _s.ActiveDecision == DecisionSource.Fine ? "Ok" : "TextDim");

        // 应用按钮的文案跟着实际状态走，不要一直写"应用到下游"让人以为还没应用
        if (_s.ActiveDecision == DecisionSource.Fine)
            BtnApply.Content = "✓ 已应用到下游";
        else if (BtnApply.Content as string == "✓ 已应用到下游")
            BtnApply.Content = "应用到下游";

        Win?.RefreshDecisionIndicator();
    }

    // ========================================================
    // 工具
    // ========================================================

    private static List<(double Start, double End)> ToTuples(IEnumerable<FFmpegExporter.Range> ranges)
        => ranges.Select(r => (r.Start, r.End)).ToList();

    // ---- 编辑器的时间轴块 ←→ 项目存档块 ----
    // 两边刻意用了不同的类型：编辑器不该知道"项目存档长什么样"，
    // 存档格式将来变了也不用动编辑器。转换只在这一个地方。

    private static ClipPart ToPart(TimelineClip c) => new()
    {
        Id = c.Id.ToString(),
        SourcePath = c.SourcePath,
        SourceName = c.SourceName,
        SourceStartSec = c.SourceStartSec,
        SourceEndSec = c.SourceEndSec,
        SourceTotalSec = c.SourceTotalSec,
        TimelineStartSec = c.TimelineStartSec,
        VolumeDb = c.VolumeDb,
        FadeInSec = c.FadeInSec,
        FadeOutSec = c.FadeOutSec,
    };

    private static TimelineClip ToClip(ClipPart p) => new()
    {
        Id = Guid.TryParse(p.Id, out var id) ? id : Guid.NewGuid(),
        SourcePath = p.SourcePath,
        SourceName = p.SourceName,
        SourceStartSec = p.SourceStartSec,
        SourceEndSec = p.SourceEndSec,
        SourceTotalSec = p.SourceTotalSec,
        TimelineStartSec = p.TimelineStartSec,
        VolumeDb = p.VolumeDb,
        FadeInSec = p.FadeInSec,
        FadeOutSec = p.FadeOutSec,
    };

    /// <summary>两条时间轴是否相同（段数、每段边界都在容差内）。</summary>
    private static bool SameTimeline(IReadOnlyList<(double, double)> a,
                                     IReadOnlyList<(double, double)> b)
    {
        if (a.Count != b.Count) return false;
        const double eps = 0.02;   // 20ms 容差：浮点累加的正常抖动
        for (int i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].Item1 - b[i].Item1) > eps) return false;
            if (Math.Abs(a[i].Item2 - b[i].Item2) > eps) return false;
        }
        return true;
    }

    private static string Fmt(double sec)
    {
        TimeSpan t = TimeSpan.FromSeconds(sec);
        return t.TotalMinutes >= 1 ? $"{t.Minutes}分{t.Seconds:00}秒" : $"{sec:F1}秒";
    }
}
