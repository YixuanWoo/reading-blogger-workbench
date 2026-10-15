// ============================================================
// ProjectListView.xaml.cs - 项目卡片墙
// ============================================================
//
// 【一页两用】
//   "进行中"和"已归档"的卡片长得一模一样，差别只有：
//     · 筛哪一批项目
//     · 按钮是"归档"还是"取回工程"
//   所以做成同一个页面类 + 一个构造参数，而不是复制两份。
//
// 【数据从哪来】
//   每次都重新扫磁盘（ProjectStore.LoadAll）。不缓存的原因：
//   项目可能被用户在资源管理器里改名/移走/删掉，缓存会显示幽灵项目。
//   扫目录的成本对这个量级可以忽略。
//
// 【★ V1.13.0：「归档」不再是"改个状态字段"】
//   改造前它一个文件都不动（原文："归档不会动任何文件，只是把它从
//   「进行中」挪开"），项目越攒越多、本地硬盘越来越满。
//   现在它真的搬：整包复制到移动硬盘 → 逐个校验指纹 → 全过了才删本地大件。
//   项目在工作台里**照旧读得到**，只是读不到原视频、成片这些重件。
//   具体怎么搬、留什么删什么 → Project\ArchiveService.cs（那里有完整规则）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Ui;

namespace LiteCut.Workbench.Views;

public partial class ProjectListView : UserControl, INavPage
{
    /// <summary>这个页面看的是哪一档项目</summary>
    public enum Filter { Active, Archived }

    private readonly Filter _filter;

    /// <summary>卡片 ↔ 项目 的对应关系（按文件夹路径索引）</summary>
    private readonly Dictionary<string, ProjectRef> _byDir = new();

    /// <summary>当前显示的那批卡片（ItemsControl 的数据源）</summary>
    private readonly List<ProjectCard> _cards = new();

    public ProjectListView(Filter filter)
    {
        _filter = filter;
        InitializeComponent();

        TxtTitle.Text = filter == Filter.Active ? "进行中" : "已归档";
        ListCards.ItemsSource = _cards;

        Loaded += (s, e) => Refresh();
    }

    // ========================================================
    // 导航生命周期
    // ========================================================

    public void OnEnter() => Refresh();

    public void OnLeave() { }

    // ========================================================
    // 扫描 + 铺卡片
    // ========================================================

    public void Refresh()
    {
        string root = ProjectStore.RootDir;
        TxtRoot.Text = _filter == Filter.Active
            ? "项目位置：" + root
            : "归档位置：" + WorkbenchWindow.State.Config.ArchiveRoot + "　（本地只留小桩，工程在移动硬盘上）";

        _byDir.Clear();
        _cards.Clear();

        List<ProjectRef> all;
        try
        {
            all = ProjectStore.LoadAll();
        }
        catch (Exception ex)
        {
            TxtRoot.Text = "读不了项目目录：" + ex.Message;
            UpdateEmptyState();
            return;
        }

        bool wantArchived = _filter == Filter.Archived;

        foreach (var r in all)
        {
            bool archived = r.IsArchived;
            if (archived != wantArchived) continue;

            _byDir[r.Dir] = r;
            _cards.Add(BuildCard(r, archived));
        }

        // ItemsSource 是同一个 List 实例，改内容不会自动刷新 → 手动通知
        ListCards.ItemsSource = null;
        ListCards.ItemsSource = _cards;

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = _cards.Count == 0;
        TxtEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ScrollCards.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        if (!empty) return;

        TxtEmpty.Text = _filter == Filter.Active
            ? "还没有进行中的项目。\n\n点右上角「＋ 新建项目」立项，\n或者在「灵感」里把一个想法变成项目。"
            : "已归档的项目会出现在这里。\n\n" +
              "归档 = 整包搬到移动硬盘 + 本地只留小桩\n" +
              "（工程档案、脚本、分镜代码、封面小图、复盘数据）。\n" +
              "想接着剪，点卡片上的「取回工程」。";
    }

    private static ProjectCard BuildCard(ProjectRef r, bool archived)
    {
        var m = r.Model;
        double progress = ProjectStore.EstimateProgress(m);

        string meta;
        if (archived && m.Archive != null)
        {
            var a = m.Archive;
            meta = $"归档 {a.ArchivedAt}　·　工程在 {a.VolumeLabel}（{ArchivePlan.Human(a.TotalBytes)}）";
        }
        else
        {
            meta = $"完成 {Math.Round(progress * 100)}%  ·  最后编辑 {FormatTime(m.UpdatedAt)}";
        }

        return new ProjectCard
        {
            Dir = r.Dir,
            Name = m.Name,
            Note = string.IsNullOrWhiteSpace(m.Note) ? "" : m.Note,
            NoteVisibility = string.IsNullOrWhiteSpace(m.Note) ? Visibility.Collapsed : Visibility.Visible,
            StatusText = archived ? "已归档" : "进行中",
            Progress = archived ? 100 : Math.Round(progress * 100),
            Meta = meta,
            ArchiveButtonText = archived ? "取回工程" : "归档",
            OpenButtonText = archived ? "看复盘" : "打开",
        };
    }

    private static string FormatTime(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return t.ToString("yyyy-MM-dd");
    }

    // ========================================================
    // 操作
    // ========================================================

    private WorkbenchWindow? Shell => WorkbenchWindow.FindOwner(this);

    private ProjectRef? RefOf(object sender)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ProjectCard card) return null;
        return _byDir.TryGetValue(card.Dir, out var r) ? r : null;
    }

    /// <summary>双击卡片 = 打开项目</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        Open(RefOf(sender));
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e) => Open(RefOf(sender));

    private void Open(ProjectRef? r)
    {
        if (r == null) return;
        Shell?.OpenProject(r);   // 已归档的项目会自动落到「复盘」页（见 NavTree.PickLanding）
    }

    // ========================================================
    // 归档 / 取回
    // ========================================================

    private void BtnArchive_Click(object sender, RoutedEventArgs e)
    {
        var r = RefOf(sender);
        if (r == null) return;

        // 已归档的卡片，这个按钮就是「取回工程」
        if (r.IsArchived)
        {
            RestoreNow(r);
            return;
        }

        // ---- ① 算计划（纯只读；失败原因都是人话）----
        ArchivePlan plan;
        try
        {
            plan = ArchiveService.Prepare(r);
        }
        catch (Exception ex)
        {
            MessageBox.Show("归档前的检查出错了：\n\n" + ex.Message,
                            "归档", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!plan.Ok)
        {
            MessageBox.Show(plan.Reason, "暂时没法归档", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // ---- ② 二次确认清单（把"要删什么"明明白白摆出来）----
        string confirm =
            $"把「{r.Model.Name}」整包搬到移动硬盘？\n\n" +
            $"【搬到硬盘】{plan.Disk.Describe()}\n" +
            $"　目标：{plan.TargetDir}\n" +
            $"　{plan.MoveCount} 个文件 / {ArchivePlan.Human(plan.MoveBytes)}\n\n" +
            $"【本地删除】{plan.DeleteCount} 个文件 / {ArchivePlan.Human(plan.DeleteBytes)}\n" +
            "　· 分镜渲染出来的画面（逐镜 mp4、卡片图）\n" +
            "　· 历史备份、渲染工作区、成片与其它大件（硬盘上都有）\n\n" +
            $"【本地保留】{plan.KeepCount} 个文件 / {ArchivePlan.Human(plan.KeepBytes)}\n" +
            "　· 工程档案、脚本、分镜稿与镜头代码、封面小图、复盘数据\n\n" +
            $"成片（跟着工程留在硬盘）：{plan.Films.Count} 个\n" +
            "　" + string.Join("\n　", plan.Films.Take(3)) +
            (plan.Films.Count > 3 ? $"\n　…还有 {plan.Films.Count - 3} 个" : "") + "\n\n" +
            "顺序是死的：先复制 → 逐个校验指纹 → 全部通过才删本地。\n" +
            "中途出错或硬盘掉线 → 立刻中止，本地一个文件都不删。";

        if (MessageBox.Show(confirm, "确认归档", MessageBoxButton.OKCancel,
                            MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        // ---- ③ 执行：模态进度窗把整个工作台锁住（防手滑），进度实时可见 ----
        //   为什么用模态窗而不是"逐个按钮 IsEnabled=false"：
        //   模态是系统级拦截输入，主窗上任何按钮、导航、标题栏都点不到，
        //   将来新加按钮也不会漏。进度窗本身关不掉（Alt+F4 也拦）。
        Shell?.Status("正在归档…");

        ArchiveResult res = ProgressDialog.Run(
            Window.GetWindow(this),
            $"正在归档「{r.Model.Name}」",
            "先复制到硬盘 → 再逐个校验指纹 → 全部通过才删本地。\n" +
            "这期间工作台会被锁住，免得手滑点到正在搬的文件。",
            "★ 本地文件一个都不会删（还没走到删除那一步），工程原样都在。\n" +
            "硬盘上已经复制过去的那部分会留着，下次归档自动覆盖，不用管它。",
            (report, ct) => ArchiveService.Execute(r, plan, report, ct));

        if (!res.Ok)
        {
            MessageBox.Show(res.Message,
                            res.Cancelled ? "归档已中止" : "归档没有完成",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            Shell?.Status(res.Cancelled ? "归档已中止" : "归档失败");
            Refresh();
            return;
        }

        // ---- ④ 收尾：台账入账（后台，不阻塞）+ 告诉用户结果 ----
        ArchiveService.TryUpdateLedger();

        Refresh();

        var tail = MessageBox.Show(
            $"✅ 已归档：{r.Model.Name}\n\n" +
            $"搬到硬盘：{res.FileCount} 个文件 / {ArchivePlan.Human(res.Bytes)}\n" +
            $"目标目录：{res.TargetDir}\n\n" +
            "本地已经只剩小桩（工程档案 + 脚本 + 封面 + 复盘数据），\n" +
            "项目在工作台里照旧看得到，想接着剪点「取回工程」。\n\n" +
            "文件总库台账已在后台开始更新（把硬盘上新增的文件入账）。\n\n" +
            "要现在去硬盘上看看吗？",
            "归档完成", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (tail == MessageBoxResult.Yes) ArchiveService.OpenFolder(res.TargetDir);

        Shell?.Status($"已归档：{r.Model.Name}");
    }

    private void RestoreNow(ProjectRef r)
    {
        var a = r.Model.Archive;
        if (a == null)
        {
            MessageBox.Show("这个项目没有归档记录，取不了。", "取回工程",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ok = MessageBox.Show(
            $"把工程从移动硬盘取回来？\n\n" +
            $"· 从：{a.TargetDir}\n" +
            $"· 拷回：{r.Dir}\n\n" +
            "取回后项目回到「进行中」，可以接着剪、接着重渲。\n" +
            "硬盘上那份**保留不动**（它仍然是你的归档备份）。\n" +
            "本地保留的复盘数据不会被硬盘上的旧版覆盖。",
            "取回工程", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        Shell?.Status("正在取回工程…");

        ArchiveResult res = ProgressDialog.Run(
            Window.GetWindow(this),
            $"正在取回「{r.Model.Name}」",
            "把工程从移动硬盘整包拷回本地。这期间工作台会被锁住。",
            "★ 取回是「只拷不删」，停下不会弄丢任何东西。\n" +
            "但本地可能会半新半旧，再点一次「取回工程」就能补全。",
            (report, ct) => ArchiveService.Restore(r, report, ct));

        if (!res.Ok)
        {
            MessageBox.Show(res.Message,
                            res.Cancelled ? "取回已中止" : "取回没有完成",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            Shell?.Status(res.Cancelled ? "取回已中止" : "取回失败");
            Refresh();
            return;
        }

        Refresh();
        MessageBox.Show(res.Message, "取回工程", MessageBoxButton.OK, MessageBoxImage.Information);
        Shell?.Status($"已取回：{r.Model.Name}");
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        var r = RefOf(sender);
        if (r == null) return;

        string extra = r.IsArchived
            ? "\n\n⚠ 注意：这只是把**本地这一小份**挪走，\n" +
              "工程本体还在移动硬盘上（不会跟着删）。\n" +
              "真要清理硬盘上那份，请自己去资源管理器处理。"
            : "";

        var ok = MessageBox.Show(
            $"把项目「{r.Model.Name}」移出列表？\n\n" +
            "项目文件夹不会被真的删掉，只是被挪到项目目录下的 _已删除 里，\n" +
            "反悔了可以在资源管理器里把它挪回来。" + extra,
            "移除项目", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            ProjectStore.MoveToTrash(r);
            Shell?.Status($"已移除：{r.Model.Name}（文件在 _已删除 里）");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("移除失败：" + ex.Message, "移除项目",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        string? name = TextPrompt.Show(Shell, "新建项目", "给这一期起个名字（后面还能改）：");
        if (name == null) return;

        try
        {
            var r = ProjectStore.Create(name, channel: "", note: "");
            Shell?.Status($"已新建项目：{r.Name}");
            Shell?.OpenProject(r);
        }
        catch (Exception ex)
        {
            MessageBox.Show("新建失败：" + ex.Message, "新建项目",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnChangeRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "项目放在哪个目录下（每个项目是它下面的一个子文件夹）",
            Multiselect = false,
        };
        string cur = ProjectStore.RootDir;
        if (System.IO.Directory.Exists(cur)) dlg.InitialDirectory = cur;
        if (dlg.ShowDialog() != true) return;

        ProjectStore.RootDir = dlg.FolderName;
        Shell?.Status("项目位置已改为：" + dlg.FolderName);
        Refresh();
    }

    // ========================================================
    // 卡片的数据载体（只用于绑定显示）
    // ========================================================

    public sealed class ProjectCard
    {
        public string Dir { get; set; } = "";
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
        public Visibility NoteVisibility { get; set; } = Visibility.Collapsed;
        public string StatusText { get; set; } = "";
        public double Progress { get; set; }
        public string Meta { get; set; } = "";
        public string ArchiveButtonText { get; set; } = "";
        public string OpenButtonText { get; set; } = "打开";
    }
}
