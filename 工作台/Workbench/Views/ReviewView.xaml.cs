// ============================================================
// ReviewView.xaml.cs - ★ V1.13.0 复盘页
// ============================================================
//
// 【它做三件事】
//   ① 把 review.json 读出来铺到界面上（表单 + 快照列表）
//   ② 填完保存（新增一条 / 更新选中的那条）
//   ③ 已归档的项目：显示归档说明条 + 「取回工程 / 打开归档文件夹」
//
// 【为什么"保存"是直接写文件、不走项目保存那条管线】
//   项目那条管线（Session.Save）管的是 project.json 里的剪辑状态，
//   每次保存都会"新建一个对象再逐字段搬"，ReviewFile 塞进去只会让两边更纠缠。
//   复盘数据是**独立的一份数据**，直接读写自己的 review.json 最清楚：
//   也不影响"有没有未保存改动"的判断（否则填个播放量会让项目显示"有改动"）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Ui;

namespace LiteCut.Workbench.Views;

public partial class ReviewView : UserControl, INavPage
{
    /// <summary>当前正在编辑的那一条的 Id（空 = 正在新建一条）</summary>
    private string _editingId = "";

    /// <summary>正在铺项目选择器 —— 期间忽略它的 SelectionChanged，免得白加载一次。</summary>
    private bool _picking;

    private ProjectRef? _project;
    private ReviewFile _file = new();

    public ReviewView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    // ========================================================
    // 导航生命周期
    // ========================================================

    public void OnEnter() => Reload();
    public void OnLeave() { }   // 没有"没保存的编辑"会丢 —— 保存动作是显式的

    private WorkbenchWindow? Shell => WorkbenchWindow.FindOwner(this);

    // ========================================================
    // 读 → 铺界面
    // ========================================================

    private void Reload()
    {
        BuildProjectPicker();

        if (PickProject.SelectedItem is not PickItem it)
        {
            // 一个项目都没有 —— 这一页自然没东西可看
            _project = null;
            TxtSub.Text = "还没有任何项目 —— 先去「进行中」立一个项，再回来复盘。";
            ArchiveBar.Visibility = Visibility.Collapsed;
            ListRecords.ItemsSource = null;
            return;
        }

        LoadFor(it.Ref);
    }

    /// <summary>
    /// 把所有项目铺进顶上那个选择器（进行中在前、已归档在后）。
    ///
    /// 【为什么一级页面要自己带一个选择器】
    ///   复盘提到一级之后，进这一页时**未必**开着项目。
    ///   而复盘数据是长在项目里的（<项目>\review\review.json），
    ///   所以必须先知道"看哪一份"。默认帮用户选"刚打开的那个"，
    ///   没打开过任何项目就选列表第一个 —— 不让用户面对一个空页。
    ///
    /// 【★ 重铺时必须保住"现在选的那个"】
    ///   赋值 ItemsSource 会把 SelectedItem 清成 null，所以以前这里一律重选默认项 ——
    ///   于是 Reload 一次就把人选的片子顶掉了。从账号页点「打开 ›」钻进来时最容易撞上：
    ///   ShowProject 先选中 A，紧接着 WPF 补发一次 Loaded → Reload → 重铺 → 变回默认的 B，
    ///   结果成了"点 A 进来看到 B"。现在优先保住当前选择，没有才退到默认。
    /// </summary>
    private void BuildProjectPicker()
    {
        var items = ProjectStore.LoadAll()
                                .OrderBy(p => p.IsArchived ? 1 : 0)
                                .Select(p => new PickItem(
                                    p, p.Model.Name + (p.IsArchived ? "（已归档）" : "")))
                                .ToList();

        // ★ 先记下"现在选的是谁"，再动 ItemsSource（一赋值就没了）
        string? want = (PickProject.SelectedItem as PickItem)?.Ref.Dir
                       ?? WorkbenchWindow.Session.Current?.Dir;

        _picking = true;   // 铺列表期间别触发"选择变了 → 重新加载"
        try
        {
            PickProject.ItemsSource = items;

            var hit = items.FirstOrDefault(x =>
                string.Equals(x.Ref.Dir, want, StringComparison.OrdinalIgnoreCase));
            PickProject.SelectedItem = hit ?? items.FirstOrDefault();
        }
        finally
        {
            _picking = false;
        }
    }

    private void PickProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_picking) return;
        if (PickProject.SelectedItem is PickItem it) LoadFor(it.Ref);
    }

    /// <summary>
    /// ★ V1.15.0 从账号页点某一行的「打开 ›」钻进来时，**直接指定看哪一支片**。
    ///
    /// 【为什么必须由外面指定，而不是靠"默认选中的那个"】
    ///   一级页默认选的是"上次打开过的项目"。用户从列表里点的是**另一支**，
    ///   如果这里不显式指过去，就会出现"点了 A 的「打开」，进去却是 B"。
    ///   顺带把选择器也拨到同一项 —— 否则下拉里显示的还是旧的那个，
    ///   人一看就以为进错了页。
    /// </summary>
    public void ShowProject(ProjectRef p)
    {
        if (p == null) return;

        BuildProjectPicker();

        foreach (var it in PickProject.Items.OfType<PickItem>())
        {
            if (!string.Equals(it.Ref.Dir, p.Dir, StringComparison.OrdinalIgnoreCase)) continue;

            _picking = true;
            try { PickProject.SelectedItem = it; }
            finally { _picking = false; }
            break;
        }

        LoadFor(p);
    }

    /// <summary>把某个项目的数据读出来铺到界面上。</summary>
    private void LoadFor(ProjectRef p)
    {
        _project = p;
        _file = ReviewStore.Load(p);
        _editingId = "";

        // 默认日期：统计日填今天，发布日留空让用户填
        InPublish.Text = "";
        InStats.Text = ReviewStore.Today;
        InDays.Text = "";

        LoadShell();
        RefreshList();
        ClearForm();
        UpdateArchiveBar();
    }

    /// <summary>选择器里的一行。ToString 就是下拉里显示的文字。</summary>
    private sealed record PickItem(ProjectRef Ref, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>顶部那句"这是哪个项目的复盘"。</summary>
    private void LoadShell()
    {
        if (_project == null) return;
        var m = _project.Model;
        TxtSub.Text = $"项目：{m.Name}　·　立项 {m.CreatedAt:yyyy-MM-dd}　·　" +
                       $"已录 {_file.Records.Count} 条快照　·　" +
                       "数据存在项目自己的 review\\review.json 里（本地）。";
    }

    /// <summary>已归档项目才显示说明条。</summary>
    private void UpdateArchiveBar()
    {
        if (_project == null || !_project.IsArchived || _project.Model.Archive == null)
        {
            ArchiveBar.Visibility = Visibility.Collapsed;
            return;
        }

        var a = _project.Model.Archive;
        TxtArchiveInfo.Text =
            $"本项目已归档于 {a.ArchivedAt} —— 源视频、成片、逐镜画面都搬到移动硬盘了，" +
            $"本地只留这份档案与复盘数据。工程在：{a.TargetDir}";
        ArchiveBar.Visibility = Visibility.Visible;
    }

    private void RefreshList()
    {
        var rows = _file.Records
                        .OrderByDescending(r => r.StatsDate)
                        .ToList();
        ListRecords.ItemsSource = rows;
    }

    // ========================================================
    // 表单 ↔ 数据
    // ========================================================

    private void ClearForm()
    {
        _editingId = "";
        BtnSave.Content = "保存为新快照";

        InPlatform.Text = "抖音";
        InPublish.Text = "";
        InStats.Text = ReviewStore.Today;
        InDays.Text = "";

        InPlay.Text = InFinish.Text = InLike.Text = InFollow.Text = "";
        InFavorite.Text = InComment.Text = InShare.Text = InHome.Text = "";

        InGood.Text = InMissed.Text = InNext.Text = "";
    }

    private void LoadForm(ReviewRecord r)
    {
        _editingId = r.Id;
        BtnSave.Content = "保存修改（更新这一条）";

        InPlatform.Text = r.Platform;
        InPublish.Text = r.PublishDate;
        InStats.Text = r.StatsDate;
        InDays.Text = r.DaysAfter > 0 ? r.DaysAfter.ToString() : "";

        InPlay.Text = r.Play > 0 ? r.Play.ToString() : "";
        InFinish.Text = r.FinishRate > 0 ? r.FinishRate.ToString(CultureInfo.InvariantCulture) : "";
        InLike.Text = r.Like > 0 ? r.Like.ToString() : "";
        InFollow.Text = r.Follow > 0 ? r.Follow.ToString() : "";
        InFavorite.Text = r.Favorite > 0 ? r.Favorite.ToString() : "";
        InComment.Text = r.Comment > 0 ? r.Comment.ToString() : "";
        InShare.Text = r.Share > 0 ? r.Share.ToString() : "";
        InHome.Text = r.HomeVisit > 0 ? r.HomeVisit.ToString() : "";

        InGood.Text = r.GoodAt;
        InMissed.Text = r.Missed;
        InNext.Text = r.NextChange;
    }

    private static long ParseLong(string s)
        => long.TryParse(s?.Trim().Replace(",", "").Replace("，", ""), out var v) ? v : 0;

    private static double ParseDouble(string s)
        => double.TryParse(s?.Trim().Replace("%", ""), NumberStyles.Float,
                           CultureInfo.InvariantCulture, out var v) ? v : 0;

    private ReviewRecord HarvestForm()
    {
        string pub = InPublish.Text.Trim();
        string stats = InStats.Text.Trim();
        int days = (int)ParseLong(InDays.Text);
        if (days <= 0 && pub.Length > 0 && stats.Length > 0)
            days = ReviewStore.DaysBetween(pub, stats);   // 没填就按两个日期算

        return new ReviewRecord
        {
            Id = _editingId.Length > 0 ? _editingId : Guid.NewGuid().ToString("N"),
            Platform = string.IsNullOrWhiteSpace(InPlatform.Text) ? "抖音" : InPlatform.Text.Trim(),
            PublishDate = pub,
            StatsDate = stats,
            DaysAfter = days,
            Play = ParseLong(InPlay.Text),
            FinishRate = ParseDouble(InFinish.Text),
            Like = ParseLong(InLike.Text),
            Follow = ParseLong(InFollow.Text),
            Favorite = ParseLong(InFavorite.Text),
            Comment = ParseLong(InComment.Text),
            Share = ParseLong(InShare.Text),
            HomeVisit = ParseLong(InHome.Text),
            GoodAt = InGood.Text.Trim(),
            Missed = InMissed.Text.Trim(),
            NextChange = InNext.Text.Trim(),
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
    }

    // ========================================================
    // 操作
    // ========================================================

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) { Warn("没有打开项目。"); return; }

        var rec = HarvestForm();

        if (rec.StatsDate.Length == 0)
        {
            Warn("「统计日」得填一个 —— 一份数据要知道是哪天统计的。\n\n（如果你是按「发布第 7 天」记的，统计日就填那一天。）");
            return;
        }

        int idx = _file.Records.FindIndex(x => x.Id == rec.Id);
        bool isUpdate = idx >= 0;

        if (isUpdate) _file.Records[idx] = rec;
        else _file.Records.Add(rec);

        if (!ReviewStore.Save(_project, _file))
        {
            Warn("写不进去（磁盘问题或文件被占用）。这次的数据先留在界面上，别关窗口。");
            return;
        }

        _editingId = rec.Id;
        BtnSave.Content = "保存修改（更新这一条）";

        RefreshList();
        LoadShell();
        Shell?.Status(isUpdate ? "已更新这一条复盘数据" : "已保存一条新的复盘数据");
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        ClearForm();
        ListRecords.SelectedItem = null;
        Shell?.Status("新的一条：填完点「保存为新快照」");
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        if (ListRecords.SelectedItem is not ReviewRecord rec)
        {
            Warn("先在列表里点中要删的那一条。");
            return;
        }

        var ok = MessageBox.Show(
            $"删掉这条复盘数据？\n\n{rec.Summary}",
            "删除快照", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        _file.Records.RemoveAll(x => x.Id == rec.Id);
        ReviewStore.Save(_project, _file);

        ClearForm();
        RefreshList();
        LoadShell();
        Shell?.Status("已删除一条复盘数据");
    }

    private void BtnReport_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) { Warn("没有打开项目。"); return; }

        // 顺手把表单里还没保存的东西也保住 —— 用户点"生成报告"时不想丢数据
        string? path = ReviewStore.WriteReport(_project, _file);

        if (path == null)
        {
            Warn("报告写不出来（检查一下项目目录还能不能写）。");
            return;
        }

        Shell?.Status("复盘报告已生成：" + path);
        MessageBox.Show(
            $"已生成：\n{path}\n\n这份报告会一直留在本地（归档之后也在），" +
            "给「归档后还要能翻复盘」兜底。",
            "复盘报告", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ListRecords_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListRecords.SelectedItem is ReviewRecord rec) LoadForm(rec);
    }

    // ========================================================
    // 归档项目专属：取回工程 / 打开归档文件夹
    // ========================================================

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _project.Model.Archive == null) return;

        var a = _project.Model.Archive;
        var ok = MessageBox.Show(
            $"把工程从移动硬盘取回来？\n\n" +
            $"· 从：{a.TargetDir}\n" +
            $"· 拷回：{_project.Dir}\n\n" +
            "本地保留的复盘数据不会被硬盘上的旧版覆盖。\n" +
            "硬盘上那份**保留不动**（它仍是你的归档备份）。",
            "取回工程", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        var proj = _project;
        Shell?.Status("正在取回工程…");

        var res = ProgressDialog.Run(
            Window.GetWindow(this),
            $"正在取回「{proj.Model.Name}」",
            "把工程从移动硬盘整包拷回本地。这期间工作台会被锁住。",
            "★ 取回是「只拷不删」，停下不会弄丢任何东西。\n" +
            "但本地可能会半新半旧，再点一次「取回工程」就能补全。",
            (report, ct) => ArchiveService.Restore(proj, report, ct));

        if (!res.Ok)
        {
            MessageBox.Show(res.Message,
                            res.Cancelled ? "取回已中止" : "取回没有完成",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            Shell?.Status(res.Cancelled ? "取回已中止" : "取回失败");
            return;
        }

        MessageBox.Show(res.Message, "取回工程", MessageBoxButton.OK, MessageBoxImage.Information);
        Shell?.AfterProjectRestored(proj);
    }

    private void BtnOpenArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_project?.Model.Archive == null) return;
        ArchiveService.OpenFolder(_project.Model.Archive.TargetDir);
    }

    private static void Warn(string msg) =>
        MessageBox.Show(msg, "复盘", MessageBoxButton.OK, MessageBoxImage.Warning);
}
