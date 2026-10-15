// ============================================================
// WorkbenchWindow.xaml.cs - 工作台主窗口（外壳）
// ============================================================
//
// 【这个文件只管四件事】
//   ① 画左侧导航栏 —— 内容从 NavTree 取，这里只负责"铺出来 + 响应点击"
//   ② 在 ContentHost 里换页面 —— 含 KeepAlive 页面的缓存与释放
//   ③ 项目的打开 / 关闭 —— 切换导航作用域（全局菜单 ↔ 工作流菜单）
//   ④ 保存 —— 手动保存、自动保存、Ctrl+S、关窗前提醒
//
// 【它不管什么】
//   每一页长什么样、怎么干活 → 各 StepView
//   有哪些菜单项、点它开哪页 → Navigation/NavTree.cs
//   项目怎么落盘、状态怎么搬   → Project/ProjectStore.cs、Project/ProjectSession.cs
//
// 【将来要改菜单，改哪里】
//   只改 NavTree.cs。加一项 = 加一个 new NavNode { ... }；
//   换顺序 = 调列表顺序。这个文件一行都不用动。
//
// 【两个作用域】
//   全局（没打开项目）：灵感 / 进行中 / 已归档
//   项目（打开项目后）：粗剪 / 精剪 / 分镜 / 渲染 / 导出 / 素材库
//   切作用域的动作就一句：拿新的 NavNode 列表重新铺一遍导航栏。
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiteCut.Core.Media;
using LiteCut.Workbench.Diagnostics;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.WritingDesk;

namespace LiteCut.Workbench;

public partial class WorkbenchWindow : Window, IWritingDeskHost
{
    // ========================================================
    // 全局单例：任何页面都能 WorkbenchWindow.State / .Session 拿到
    // ========================================================

    /// <summary>全流程共享的运行时状态。各页面都读这一份。</summary>
    public static WorkbenchState State { get; } = new();

    /// <summary>
    /// 「当前打开的是哪个项目」+ 内存状态与存档之间的搬运。
    /// 注意：声明在 State 之后 —— 静态字段是按声明顺序初始化的。
    /// </summary>
    public static ProjectSession Session { get; } = new(State);

    // ========================================================
    // 导航
    // ========================================================

    /// <summary>把 NavNode 列表铺成左侧按钮的渲染器</summary>
    private readonly NavHost _nav;

    /// <summary>KeepAlive 页面的实例缓存（键 = NavNode.Id）</summary>
    private readonly Dictionary<string, UserControl> _alive = new();

    /// <summary>当前显示的页面</summary>
    private UserControl? _currentView;

    /// <summary>当前页面对应的导航项 Id</summary>
    private string? _currentId;

    /// <summary>
    /// 现在铺的是哪一份菜单：true = 项目内工作流，false = 全局（灵感/进行中/已归档/复盘）。
    ///
    /// 【为什么不能拿 Session.HasProject 当判据】
    ///   ★ V1.14.0 起这两件事**不再等价**：打开一个**已归档**的项目时，
    ///   会话里确实有项目，但我们故意**不进**项目作用域 ——
    ///   因为归档项目唯一还有意义的动作是"看复盘 / 取回工程"，
    ///   而复盘已经搬到一级菜单了。
    ///   （继续用 HasProject 判的话，`GoTo("project.review")` 会被
    ///     "这页不属于当前作用域"拦下来，归档项目就打不开复盘页。）
    /// </summary>
    private bool _inProjectScope;

    // ========================================================
    // 定时器
    // ========================================================

    /// <summary>自动保存：有改动就落盘</summary>
    private readonly DispatcherTimer _autoSave;

    /// <summary>状态栏的钟</summary>
    private readonly DispatcherTimer _clock;

    /// <summary>自动保存间隔。够密到不丢活，够疏到不烦人。</summary>
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(20);

    // ========================================================
    // 全屏 / 退出
    // ========================================================

    private bool _fullscreen;
    private GridLength _savedNavWidth;
    private WindowState _savedWindowState;

    /// <summary>已经确认过要退了 —— 避免 OnClosing 里二次弹窗</summary>
    private bool _confirmedClose;

    /// <summary>
    /// ★ V1.14.0 调试模式会话（日志 + 文件监听 + 本机遥控口）。
    /// 构造时建好，但**只有配置里开了它才真的工作**（见 ApplyDebugMode）。
    /// 关着的时候它什么都不做、也不占端口。
    /// </summary>
    private readonly DebugSession _debug;

    public WorkbenchWindow()
    {
        InitializeComponent();

        _nav = new NavHost(NavList, OnNavPicked);

        // 项目打开/关闭/保存后，外壳要跟着改标题、面包屑、保存状态
        Session.ProjectChanged += (_, _) => RefreshShell();
        Session.Saved += (_, _) => RefreshShell();

        InitFFmpeg();

        if (State.Config.StartMaximized)
            WindowState = WindowState.Maximized;

        _autoSave = new DispatcherTimer { Interval = AutoSaveInterval };
        _autoSave.Tick += (_, _) => AutoSaveTick();
        _autoSave.Start();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnClockTick();
        _clock.Start();
        OnClockTick();

        StartUp();

        // ★ 调试模式放在最后开：那时界面已经铺好，日志里第一条才是有意义的"落在哪一页"
        _debug = new DebugSession(this);
        ApplyDebugMode();
    }

    /// <summary>调试模式会话（设置窗口要用它看端口状态）。</summary>
    public DebugSession Debug => _debug;

    /// <summary>当前停在哪一页（NavTree.Ids 里的值）—— 遥控口报状态要用。</summary>
    public string? CurrentNavId => _currentId;

    /// <summary>
    /// 按配置把调试模式打开 / 关掉。
    /// 启动时调一次；在设置窗口里改了开关，回来也调这个（改一处，两处都生效）。
    /// </summary>
    public void ApplyDebugMode()
    {
        bool want = State.Config.DebugEnabled;

        try
        {
            if (want && !_debug.Active)
            {
                bool ok = _debug.Start(State.Config.DebugPort, State.Config.DebugLogToFile);
                if (!ok)
                    Status("⚠ 调试模式已打开，但遥控口没起来：" + (_debug.LastError ?? "未知原因"));
            }
            else if (!want && _debug.Active)
            {
                _debug.Stop();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("切换调试模式失败", ex);
        }
    }

    // ========================================================
    // 启动落点
    // ========================================================

    /// <summary>
    /// 程序打开时落在哪一页 —— **只落在主界面，绝不自动打开上次的项目**。
    ///
    /// 【为什么把这个改掉】上一版是"上次在哪个项目里退出，这次就接着进那个项目"，
    /// 看起来贴心，实际是启动慢的元凶：
    ///   ① 打开项目会把几百个素材块全铺回时间轴（读源文件、建块、算布局），
    ///      还会连带创建精剪编辑器 —— 音频引擎、本地上传服务一起起来；
    ///   ② 这段活是在**窗口显示之前**干的，所以用户看到的是"双击了没反应"；
    ///   ③ 他自然会以为没点上，于是反复双击 → 开出好几个实例互相抢资源 → 越等越慢。
    ///
    /// 现在一律落在主界面（项目列表，几乎不耗时），加载推迟到**用户真的点进某个项目**
    /// 那一刻 —— 因果明确，等待也变得可预期（点的就是你等的那件事）。
    ///
    /// 上次在剪哪个项目仍然记着，但只用在状态栏提一句，绝不去加载它。
    /// </summary>
    private void StartUp()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        EnterGlobalScope();

        // 从 App 启动到现在（含 .NET 运行时和 WPF 初始化）的总耗时 —— 用户感知的就是这个数
        long sinceAppStart = Environment.TickCount64 - App.StartupTick;
        Logger.Info($"启动就绪：外壳 {sw.ElapsedMilliseconds} ms，从进程启动算 {sinceAppStart} ms");

        Status("就绪 —— 从「进行中」打开一个项目开工" + LastProjectHint());
    }

    /// <summary>
    /// "上次在剪：XXX" 的提示尾巴。项目已经被挪走/删掉时不显示。
    /// </summary>
    private string LastProjectHint()
    {
        string last = State.Config.LastProjectDir;
        if (string.IsNullOrWhiteSpace(last) || !System.IO.Directory.Exists(last)) return "";

        string name = System.IO.Path.GetFileName(last.TrimEnd('\\', '/'));
        return name.Length > 0 ? $"　（上次在剪：{name}）" : "";
    }

    // ========================================================
    // 作用域切换（全局菜单 ↔ 工作流菜单）
    // ========================================================

    /// <summary>铺全局菜单（灵感 / 进行中 / 已归档 / 复盘）并落到指定页（不指定就落默认页）。</summary>
    private void EnterGlobalScope(string? landOn = null)
    {
        var scope = NavTree.ForScope(false);
        _inProjectScope = false;
        TxtNavGroup.Text = "工作区";
        _nav.Render(scope, null);
        RefreshShell();

        GoTo(landOn ?? NavTree.PickLanding(insideProject: false, State.Config.LastNavId));
    }

    /// <summary>铺项目内的工作流菜单（粗剪 / 精剪 / …）并落到指定页（不指定就落默认页）。</summary>
    private void EnterProjectScope(string? landOn = null)
    {
        var scope = NavTree.ForScope(true);
        _inProjectScope = true;
        TxtNavGroup.Text = "工作流";
        _nav.Render(scope, null);
        RefreshShell();

        GoTo(landOn ?? NavTree.PickLanding(insideProject: true, State.Config.LastNavId));
    }

    /// <summary>
    /// 按"刚打开的这个项目是什么状态"选作用域：
    ///   已归档 → 留在**全局**，落到「复盘」（归档项目只剩"看数据 / 取回工程"有意义）
    ///   其余   → 进项目工作流（写作台 / 粗剪 / …）
    /// </summary>
    private void EnterScopeForCurrentProject()
    {
        if (Session.Current?.IsArchived == true) EnterGlobalScope(NavTree.Ids.Review);
        else EnterProjectScope();
    }

    // ========================================================
    // 导航
    // ========================================================

    /// <summary>左侧导航被点了（由 NavHost 回调进来）</summary>
    private void OnNavPicked(NavNode node) => GoTo(node.Id);

    /// <summary>
    /// 跳到某个导航项。外面（比如素材库）也用这个跳页。
    /// 已经是当前页时只补一次 OnEnter（页面自己保证幂等），不重建控件。
    /// </summary>
    public void GoTo(string navId)
    {
        var node = NavTree.Find(navId);
        if (node == null) return;

        // 作用域不对（没开项目却想进工作流页）→ 不当操作，忽略
        if (!IsInCurrentScope(node.Id))
        {
            Status("先打开一个项目，才能进工作流");
            return;
        }

        // ---- 已经在这一页：只补一次 OnEnter ----
        if (_currentId == node.Id && _currentView != null &&
            ContentHost.Children.Contains(_currentView))
        {
            _nav.SetActive(node.Id);
            if (_currentView is INavPage same) same.OnEnter();
            return;
        }

        // ---- 离开旧页面（给它一次把编辑交回共享状态的机会）----
        DetachCurrent();

        // ---- 铺新页面 ----
        UserControl view = Resolve(node);

        // ★ 写作台：把宿主注入给它（它拿 .md 路径、发状态、问未保存确认全靠这个）。
        //   通过 IWritingDeskHost 接线，主窗口不硬编码写作台具体类。
        if (view is WritingDeskView wd) wd.AttachHost(this);

        ContentHost.Children.Add(view);
        _currentView = view;
        _currentId = node.Id;

        _nav.SetActive(node.Id);
        RefreshShell();

        if (view is INavPage entering) entering.OnEnter();

        RememberNav(node.Id);

        DebugLog.Write("nav", $"切到「{node.Title}」", new { id = node.Id, project = Session.Current?.Model?.Name });
    }

    /// <summary>
    /// 取导航项对应的页面实例。
    /// KeepAlive 的复用（精剪编辑器里压着撤销历史和播放状态，重建就全丢）；
    /// 其余的每次重建 —— 这样粗剪页离开时会触发 Unloaded，把播放停掉。
    /// </summary>
    private UserControl Resolve(NavNode node)
    {
        if (node.KeepAlive && _alive.TryGetValue(node.Id, out var cached))
            return cached;

        UserControl view = node.CreateView?.Invoke() ?? BuildMissingView(node);
        if (node.KeepAlive) _alive[node.Id] = view;
        return view;
    }

    /// <summary>
    /// 把当前页从界面上摘下来，并让它把编辑交回共享状态。
    /// 切换页面、切作用域、关项目之前都必须先过这一关 ——
    /// 否则精剪页里没落地的改动会被直接丢掉。
    /// </summary>
    private void DetachCurrent()
    {
        if (_currentView == null) return;

        if (_currentView is INavPage leaving) leaving.OnLeave();

        ContentHost.Children.Remove(_currentView);
        _currentView = null;
        _currentId = null;
    }

    /// <summary>
    /// 丢掉所有 KeepAlive 页面。切项目 / 关项目时必须调 ——
    /// 那些页面里压着上一个项目的数据，留着就会串味。
    ///
    /// 【注意】丢之前必须先让它们释放资源（尤其是精剪编辑器：
    /// 它占着音频引擎和一个本地上传服务，直接扔掉会泄漏进程和端口）。
    /// </summary>
    private void DropKeepAlive()
    {
        foreach (var v in _alive.Values)
            if (v is Views.StepFineCutView f) f.ShutdownEditor();

        _alive.Clear();
    }

    /// <summary>这一页是不是属于当前作用域</summary>
    private bool IsInCurrentScope(string navId)
    {
        foreach (var n in NavTree.ForScope(_inProjectScope))
            if (n.Id == navId) return true;
        return false;
    }

    /// <summary>记住停在哪儿（下次启动落回同一页）</summary>
    private void RememberNav(string navId)
    {
        State.Config.LastNavId = navId;
        State.SaveConfig();
    }

    /// <summary>
    /// 菜单项忘了挂页面时的占位页。
    /// 有了它，"加了菜单忘了写页面"只会看到一句说明，而不是整个程序崩掉。
    /// </summary>
    private static UserControl BuildMissingView(NavNode node) => new()
    {
        Content = new TextBlock
        {
            Text = $"「{node.Title}」这个页面还没做。\n\n" +
                   "它在 NavTree 里已经登记好位置了 ——\n" +
                   "写好页面类之后，挂到这个菜单项的 CreateView 上就生效。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)),
            FontSize = 13,
            LineHeight = 24,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    // ========================================================
    // 项目的打开 / 关闭
    // ========================================================

    /// <summary>
    /// 打开一个项目：存好手上这个 → 换掉内存状态 → 收起全局菜单、显示工作流菜单。
    /// 项目列表页、灵感页的「立项」都走这里。
    /// </summary>
    public void OpenProject(ProjectRef r)
    {
        if (r == null) return;

        // 点开的就是当前这个 → 不用重来，切到工作流就行
        if (Session.Current != null &&
            string.Equals(Session.Current.Dir, r.Dir, StringComparison.OrdinalIgnoreCase))
        {
            EnterScopeForCurrentProject();
            return;
        }

        // ---- 先把上一个收拾干净：交回编辑 → 存盘 → 摘页面 → 丢缓存 ----
        DetachCurrent();
        SaveNow(manual: false);
        DropKeepAlive();
        if (Session.HasProject) Session.Close();

        // ---- 灌入新项目 ----
        try
        {
            Session.Open(r);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开项目失败：" + ex.Message, "打开项目",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            EnterGlobalScope();
            return;
        }

        State.Config.LastProjectDir = r.Dir;
        State.SaveConfig();

        EnterScopeForCurrentProject();
        Status($"已打开项目：{r.Name}");
    }

    /// <summary>
    /// 关掉当前项目，回到全局菜单（会先保存）。
    /// </summary>
    /// <param name="landOn">回到全局菜单后落哪一页；不指定就落默认页。</param>
    private void CloseProject(string? landOn = null)
    {
        DetachCurrent();
        SaveNow(manual: false);
        DropKeepAlive();

        if (Session.HasProject) Session.Close();

        State.Config.LastProjectDir = "";
        State.SaveConfig();

        EnterGlobalScope(landOn);
    }

    /// <summary>左上角「← 全部项目」</summary>
    private void BtnBackToProjects_Click(object sender, RoutedEventArgs e)
    {
        CloseProject(landOn: NavTree.Ids.ProjectsActive);
        Status("已回到项目列表");
    }

    /// <summary>
    /// ★ V1.13.0：复盘页里点了「取回工程」、工程从移动硬盘拷回来之后调用。
    ///
    /// 【为什么要有这一步】取回之前，本地这个项目是个"桩"——只有档案和数据，
    ///   没有源视频；内存里那份状态也是按桩灌的。取回之后文件齐了，
    ///   必须**重新灌一次**，否则界面还停在"归档项目"的样子，
    ///   点了剪辑页也是空的（用户会以为取回没成功）。
    /// </summary>
    public void AfterProjectRestored(ProjectRef r)
    {
        if (r == null) return;

        DetachCurrent();
        SaveNow(manual: false);
        DropKeepAlive();
        if (Session.HasProject) Session.Close();

        try
        {
            Session.Open(r);
        }
        catch (Exception ex)
        {
            MessageBox.Show("取回后重新打开项目失败：" + ex.Message, "取回工程",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            EnterGlobalScope();
            return;
        }

        State.Config.LastProjectDir = r.Dir;
        State.SaveConfig();

        EnterProjectScope();   // 不再是归档项目了 → 自动回到工作流菜单的默认页
        Status($"已取回工程：{r.Model.Name} —— 现在可以接着剪了");
    }

    // ========================================================
    // 保存
    // ========================================================

    /// <summary>顶栏「保存」按钮</summary>
    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveNow(manual: true);

    /// <summary>
    /// 外部（比如素材库导入了文件）告诉你"内容有变化"。
    ///
    /// 【注意】项目的"有没有改动"是自动算出来的（状态指纹比对，见 ProjectSession），
    /// 不靠手动打标记 —— 手动标记漏一处就会"看着存过了其实没存"。
    /// 所以这里只做一件事：让外壳重新算一次并刷新显示。
    /// </summary>
    public void MarkDirty() => RefreshShell();

    /// <summary>
    /// 保存。
    /// 会先把页面上还没落地的编辑推进共享状态（精剪编辑器里刚拖完的那一下），
    /// 再做脏检查 / 写盘。
    /// </summary>
    /// <param name="manual">true = 用户主动点的，就算没改动也写一次盘（让人安心）</param>
    private bool SaveNow(bool manual)
    {
        if (!Session.HasProject)
        {
            if (manual) Status("还没有打开项目 —— 先在「进行中」里打开或新建一个");
            return false;
        }

        SyncCurrent();

        // ★★ V1.10.6 稿子（script.md）纳入项目保存的同一条管线。
        //   【为什么要合到一起 —— 用户的原话】
        //     "稿子也是项目的一部分啊。自动保存，或者手动保存项目的时候，
        //      为什么不能把稿件变化同时保存呢？"
        //   → 之前稿子有自己的按钮和自己的定时器 = 同一件事两套机制，
        //     既让人困惑（两个保存按钮），又可能各存各的。
        //   → 现在：**项目里有什么要存，就一起存。** 保存对用户只有一个动作。
        bool scriptWrote = SaveScriptIfCurrent();

        bool wrote = Session.Save(force: manual);
        RefreshShell();

        if (manual)
        {
            string name = Session.Current!.Model.Name;
            if (scriptWrote)
                Status($"已保存：{name}（含稿子 → script.md）");
            else
                Status(wrote ? $"已保存：{name}" : "没有需要保存的改动");
        }

        return wrote || scriptWrote;
    }

    /// <summary>
    /// 当前页是写作台时，把稿子一并存盘。返回是否真的写了。
    /// （不是写作台就直接返回 false —— 稿子不存在，无从谈起。）
    /// </summary>
    private bool SaveScriptIfCurrent()
        => _currentView is IWritingDeskPage wd && wd.Save();

    /// <summary>把当前页面上还没交回共享状态的编辑推回去。</summary>
    private void SyncCurrent()
    {
        if (_currentView is ISaveSync s) s.SyncBeforeSave();
    }

    /// <summary>自动保存：有改动才写盘，没改动什么都不做。</summary>
    private void AutoSaveTick()
    {
        if (_confirmedClose || !Session.HasProject) return;

        // 精剪页的改动压在编辑器里，先让它交回状态，否则永远算不出"有改动"
        SyncCurrent();

        // ★ V1.10.6 稿子也在这条自动保存里（与项目配置同一条管线、同一个 20 秒节奏）
        bool scriptWrote = SaveScriptIfCurrent();

        if (!Session.IsDirty)
        {
            // 项目配置没改动但稿子存了 → 说一声，否则用户以为没生效
            if (scriptWrote) Status($"稿子已自动保存 {DateTime.Now:HH:mm:ss}");
            return;
        }

        if (Session.Save())
            Status($"已自动保存 {DateTime.Now:HH:mm:ss}（Ctrl+S 可随时手动保存）");
        RefreshShell();
    }

    /// <summary>
    /// 状态栏的钟。顺便兼职一件事：定期重算"有没有未保存的改动"。
    ///
    /// 【为什么要兼职】
    ///   外壳不知道各页面什么时候改了数据（没有全局的"状态变了"事件）。
    ///   没有这个兜底，用户改完东西顶栏还显示"已保存 HH:mm:ss"，
    ///   看起来像存过了 —— 这种误导比不显示更糟。
    ///
    /// 【为什么隔一秒算一次不心疼】
    ///   算一次 = 把状态序列化成 JSON 再取哈希（几十 KB 的字符串）。
    ///   对这个量级是亚毫秒级的活，一秒一次完全无感。
    /// </summary>
    private void OnClockTick()
    {
        TxtClock.Text = DateTime.Now.ToString("HH:mm:ss");
        RefreshSaveState();
    }

    // ========================================================
    // 外壳显示刷新（标题 / 面包屑 / 保存状态 / 当前项目卡片）
    // ========================================================

    private void RefreshShell()
    {
        bool has = Session.HasProject;
        var proj = Session.Current;

        // ---- 当前项目卡片 & 保存按钮：没开项目时整块收起 ----
        ProjectBox.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        // ★ V1.10.6 恢复显示「保存项目」—— 它现在是**唯一的保存入口**。
        //   V1.10.5 我曾把它撤掉（因为当时稿子只靠手动、想补自动保存）；
        //   现在稿子已并入同一条管线（SaveNow 会连稿子一起存），所以它回来了，
        //   而且是全系统**唯一**一个保存按钮 —— 写作台页内不再有自己的保存。
        BtnSave.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        if (has && proj != null)
        {
            TxtProjectName.Text = proj.Model.Name;

            double progress = ProjectStore.EstimateProgress(proj.Model);
            TxtProjectMeta.Text = $"完成 {progress * 100:F0}%";
            Title = $"{proj.Model.Name} — 翊瑄的工作台";
        }
        else
        {
            Title = "翊瑄的工作台";
        }

        // ---- 面包屑：有项目时是「项目 › 当前页」，没项目时就是当前页 ----
        var node = _currentId != null ? NavTree.Find(_currentId) : null;
        string leaf = node?.Title ?? "";

        TxtBreadcrumb.Text = has && proj != null
            ? (leaf.Length > 0 ? $"{proj.Model.Name}   ›   {leaf}" : proj.Model.Name)
            : leaf;

        RefreshDecisionIndicator();
        RefreshSaveState();
    }

    /// <summary>
    /// 刷新顶栏那句"下游按哪一份剪辑决策在走"。
    ///
    /// 【为什么放在顶栏而不是各页自己写】
    ///   这套工作台是四步流水线，"现在按哪份剪"是贯穿全局的状态 ——
    ///   写在每一页里就会出现"同一件事四种说法"，而且新加页面容易忘。
    ///   放顶栏只有一个来源，任何时候都看得见。
    ///
    /// 页面在应用完决策后可以直接调它（公开方法），不必等切页。
    /// </summary>
    public void RefreshDecisionIndicator()
    {
        if (TxtDecisionBadge == null) return;

        if (!Session.HasProject || !State.HasDecision)
        {
            TxtDecisionBadge.Text = "";
            return;
        }

        string who = State.ActiveDecision == DecisionSource.Fine ? "精剪" : "粗剪";
        TxtDecisionBadge.Text = $"下游使用：{State.ActiveDecisionLabel}（{who}决策）";
        TxtDecisionBadge.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
    }

    /// <summary>只刷"有没有未保存的改动"那一小块。</summary>
    private void RefreshSaveState()
    {
        if (TxtSaveState == null) return;   // 构造函数早期可能还没初始化完

        if (!Session.HasProject)
        {
            TxtSaveState.Text = "";
            return;
        }

        if (Session.IsDirty)
        {
            TxtSaveState.Text = "● 有改动 · 20 秒内自动保存";
            TxtSaveState.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        }
        else
        {
            // ★ V1.10.5：说清"自动存的"，否则用户找不到那个手动保存按钮会以为没地方存
            TxtSaveState.Text = Session.LastSavedAt is { } t
                ? $"已自动保存 {t:HH:mm:ss}"
                : "已自动保存";
            TxtSaveState.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E));
        }
    }

    // ========================================================
    // ffmpeg 初始化
    // ========================================================

    /// <summary>
    /// 找到 ffmpeg 并把结果写到左下角。
    /// 这个文件决定了后面所有步骤能不能干活 —— 所以放在最显眼的地方。
    /// </summary>
    private void InitFFmpeg()
    {
        // 用户之前指定过 → 优先用它
        if (!string.IsNullOrWhiteSpace(State.Config.FFmpegPath))
            FFmpegLocator.Configure(State.Config.FFmpegPath);

        if (FFmpegLocator.TryFind())
        {
            string msg = FFmpegLocator.Describe();
            TxtFfmpegStatus.Text = msg.Length > 60 ? msg[..60] + "…" : msg;
            TxtFfmpegStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));

            // 第一次自动搜到的，记下来，下次直接命中
            if (string.IsNullOrWhiteSpace(State.Config.FFmpegPath) && FFmpegLocator.FFmpegPath != null)
            {
                State.Config.FFmpegPath = FFmpegLocator.FFmpegPath;
                State.SaveConfig();
            }
        }
        else
        {
            TxtFfmpegStatus.Text = "⚠ 没找到 ffmpeg，导出功能不可用。请点下方按钮指定位置。";
            TxtFfmpegStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
    }

    // ========================================================
    // ★ V1.14.0 顶栏菜单条：文件 / 设置
    // ========================================================

    /// <summary>
    /// 左键点「文件」—— 把下拉菜单弹出来。
    ///
    /// 【为什么要写这一行代码】
    ///   WPF 的 ContextMenu 默认只认**右键**。而"文件"这个按钮是给人左键点的，
    ///   所以得手动把菜单打开，并且指定它贴在按钮下方（PlacementTarget）。
    /// </summary>
    private void BtnFile_Click(object sender, RoutedEventArgs e)
    {
        if (MenuFile == null) return;
        MenuFile.PlacementTarget = BtnFile;
        MenuFile.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MenuFile.IsOpen = true;
    }

    /// <summary>「导入外部项目」—— 本期灰着，点了给个说明（免得以为按钮坏了）。</summary>
    private void MiImportProject_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "这个功能还没做。\n\n" +
            "它要做的事：选一个已经存在的项目文件夹（比如别人给你的、或者从移动硬盘拷回来的），" +
            "读出里面的 project.json，直接挂进「进行中」列表 —— \n" +
            "而不是像「新建项目」那样在项目根目录下重新建一个文件夹。\n\n" +
            "现在要临时看别人的项目，可以手动把文件夹放进项目根目录（文件 → 打开项目根目录）。",
            "导入外部项目", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>「打开项目根目录」—— 在资源管理器里打开「项目」夹。</summary>
    private void MiOpenProjectRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ProjectStore.EnsureRoot();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ProjectStore.RootDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开项目根目录：" + ex.Message, "打开目录",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>「打开日志文件夹」—— 排障第一现场。</summary>
    private void MiOpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string dir = Path.Combine(exeDir, "logs");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开日志文件夹：" + ex.Message, "打开目录",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 「设置」—— 打开设置窗口。
    /// 关掉之后要把可能被改掉的东西重新应用一遍（ffmpeg 位置 / 调试模式 / 保存状态）。
    /// </summary>
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Settings.SettingsWindow(State.Config) { Owner = this };
        dlg.ShowDialog();

        InitFFmpeg();          // ffmpeg 路径可能被改过
        ApplyDebugMode();      // 调试开关可能被拨过
        RefreshShell();

        Status(dlg.Saved ? "设置已保存" : "设置没有改动");
    }

    private void BtnPickFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "可执行程序|ffmpeg.exe|所有文件|*.*",
            FileName = "ffmpeg.exe",
        };

        string? startDir = string.IsNullOrWhiteSpace(State.Config.FFmpegPath)
            ? @"D:\HyperFrames\_tools\bin"
            : System.IO.Path.GetDirectoryName(State.Config.FFmpegPath);
        if (startDir != null && System.IO.Directory.Exists(startDir)) dlg.InitialDirectory = startDir;

        if (dlg.ShowDialog() != true) return;

        State.Config.FFmpegPath = dlg.FileName;
        State.SaveConfig();

        FFmpegLocator.Configure(dlg.FileName);
        FFmpegLocator.TryFind();
        InitFFmpeg();
        Status("已更新 ffmpeg 位置");
    }

    // ========================================================
    // 全屏模式（精剪编辑器用）
    // ========================================================
    //
    // 进入：收起左导航 / 顶栏 / 状态栏，无边框最大化 —— 编辑器吃满整窗。
    // 退出：F11 再按一次，或 Esc。窗口恢复正常边框和尺寸。

    /// <summary>切换全屏。精剪页的「⛶ 全屏」按钮和 F11 都走这里。</summary>
    public void ToggleFullscreen()
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_fullscreen) return;
        _fullscreen = true;

        _savedNavWidth = NavColumn.Width;
        _savedWindowState = WindowState;

        NavColumn.Width = new GridLength(0);
        NavPanel.Visibility = Visibility.Collapsed;
        TopBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;

        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
    }

    private void ExitFullscreen()
    {
        if (!_fullscreen) return;
        _fullscreen = false;

        NavColumn.Width = _savedNavWidth;
        NavPanel.Visibility = Visibility.Visible;
        TopBar.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;

        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = _savedWindowState;
    }

    // ========================================================
    // 快捷键
    // ========================================================

    /// <summary>Ctrl+S 保存；F11 切全屏；Esc 退出全屏。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SaveNow(manual: true);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    // ========================================================
    // 退出清理
    // ========================================================

    /// <summary>
    /// 关窗前：先把编辑交回状态，有未保存的改动就问一句。
    /// 保存失败时**不退出** —— 剪了半天的东西不能因为一次写盘失败就没了。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_confirmedClose)
        {
            base.OnClosing(e);
            return;
        }

        // 精剪编辑器里的改动还压在控件里，先交回状态再判断
        SyncCurrent();

        // ★★ V1.10.4：稿源（script.md）有未保存改动时，退出前必须拦一下。
        //   这一环原来**完全没有**：Session.IsDirty 只管 project.json，
        //   写了一半的稿子直接跟着程序一起消失 —— 用户重开发现"稿子呢？"就是这个。
        //   写作台通过 IWritingDeskPage 接口参与拦截，不硬编码具体类。
        if (_currentView is IWritingDeskPage wd && wd.HasUnsavedChanges)
        {
            var rs = MessageBox.Show(
                "稿子还有改动没保存。\n\n要保存后再退出吗？",
                "退出工作台", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (rs == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (rs == MessageBoxResult.Yes && !wd.Save())
            {
                MessageBox.Show(
                    "稿子保存失败 —— 这次先不退出。\n\n" +
                    "检查一下磁盘空间，或者文件是不是被别的程序占用了。",
                    "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Cancel = true;
                return;
            }
        }

        if (Session.HasProject && Session.IsDirty)
        {
            string name = Session.Current!.Model.Name;
            var r = MessageBox.Show(
                $"项目「{name}」还有没保存的改动。\n\n要保存后再退出吗？",
                "退出工作台", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (r == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (r == MessageBoxResult.Yes && !Session.Save(force: true))
            {
                MessageBox.Show(
                    "保存没成功 —— 这次先不退出。\n\n" +
                    "检查一下磁盘空间，或者项目文件是不是被别的程序占用了。",
                    "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Cancel = true;
                return;
            }
        }
        else if (Session.HasProject)
        {
            // 没有改动也顺手落一次盘：把"停在哪个项目 / 哪一页"这类信息写准
            State.SaveConfig();
        }

        _confirmedClose = true;
        _autoSave.Stop();
        _clock.Stop();
        base.OnClosing(e);
    }

    /// <summary>窗口真的关了 → 释放精剪编辑器占用的音频引擎和上传服务。</summary>
    protected override void OnClosed(EventArgs e)
    {
        // _currentView 可能同时也在 _alive 里，去重免得 Shutdown 两次
        var views = new HashSet<UserControl>(_alive.Values);
        if (_currentView != null) views.Add(_currentView);

        foreach (var v in views)
            if (v is Views.StepFineCutView f) f.ShutdownEditor();

        // ★ 调试模式占着一个监听端口和一个文件监听 —— 退出时必须松开，
        //   否则下次启动会报"端口被占"，而且监听句柄会吊着进程。
        try { _debug.Dispose(); } catch { }

        base.OnClosed(e);
    }

    // ========================================================
    // 状态栏
    // ========================================================

    /// <summary>
    /// 底部状态栏写字。任何页面都能通过 Owner 拿到窗口调这个方法。
    ///
    /// ★ 顺手记一条调试流水：状态栏是程序"告诉人刚刚发生了什么"的地方
    ///   （"已保存一条新的复盘数据""出片失败：…"）。Agent 复盘操作时，
    ///   读这条流水就知道每一步的结果，不用人盯着屏幕看。
    /// </summary>
    public void Status(string msg)
    {
        TxtStatus.Text = msg;
        DebugLog.Write("status", msg);
    }

    // ========================================================
    // ★ IWritingDeskHost 实现：写作台需要的宿主能力
    // ========================================================
    // 写作台(WritingDesk)模块通过这个接口拿路径、发状态、问未保存确认，
    // 主窗口只实现接口，不跟写作台内部耦合。

    /// <summary>当前项目的稿源路径（&lt;项目&gt;\script.md）。没打开项目返回空串。</summary>
    string IWritingDeskHost.MdPath
    {
        get
        {
            var r = Session.Current;
            if (r == null) return "";
            return Path.Combine(r.Dir, "script.md");
        }
    }

    /// <summary>写作台的本机视图状态文件（&lt;项目&gt;\stages\00-script\view-state.json）。</summary>
    string IWritingDeskHost.StatePath
    {
        get
        {
            var r = Session.Current;
            if (r == null) return "";
            return Path.Combine(r.ScriptDir, "view-state.json");
        }
    }

    /// <summary>有未保存改动时问用户：是/否/取消 → 返回是否放行重读/离开。</summary>
    bool IWritingDeskHost.ConfirmDiscardUnsaved()
    {
        var ans = MessageBox.Show(
            "稿子有改动还没保存。\n\n" +
            "·选「是」：先存到 script.md，再重新读取（推荐）\n" +
            "·选「否」：丢弃改动，按磁盘上的内容重读\n" +
            "·选「取消」：哪也不去，内存里的改动保持不动",
            "有未保存的改动",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (ans == MessageBoxResult.Cancel) return false;
        if (ans == MessageBoxResult.Yes && !SaveScriptIfCurrent())
        {
            MessageBox.Show("保存失败，已取消重读 —— 内存里的改动还在。",
                            "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        return true;
    }

    /// <summary>左侧导航是否处于沉浸式收起（由写作台驱动）。全屏时不认为是沉浸式。</summary>
    bool IWritingDeskHost.NavigationCollapsed
        => !_fullscreen && NavPanel.Visibility == Visibility.Collapsed;

    /// <summary>
    /// 沉浸式创作：收起/展开左侧工作台导航。
    /// 只动左侧导航列，顶栏与状态栏保留（跟全屏是两回事）；全屏期间不掺和。
    /// </summary>
    void IWritingDeskHost.SetNavigationCollapsed(bool collapsed)
    {
        if (_fullscreen) return;
        if (collapsed)
        {
            NavColumn.Width = new GridLength(0);
            NavPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            NavColumn.Width = new GridLength(204);
            NavPanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 给子页面用的便捷方法：找到宿主窗口。
    /// </summary>
    public static WorkbenchWindow? FindOwner(FrameworkElement child)
    {
        for (DependencyObject? d = child; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is WorkbenchWindow w) return w;
        }
        return null;
    }
}
