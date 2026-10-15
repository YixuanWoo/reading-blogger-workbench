// ============================================================
// AccountReviewView.xaml.cs - ★ V1.15.0 复盘 · 账号页（第一层）
// ============================================================
//
// 【这一页只是"装配工"】
//   它自己**不算数据、不画图**，只做三件事：
//     ① 把各模块摆进布局（主图 / 账号卡 / 作品列表）；
//     ② 把数据从领域模块取出来，塞给模块；
//     ③ 把模块发出来的动作（点一行、改了日期）接给仓库。
//
//   数据从哪来：
//     发布日        → ReviewStore.EffectivePublishDate（每个项目）
//     作品列表数字  → WorksBoard.Build（读各项目 review.json 汇总）
//     账号那 4 个数 → AccountReviewStore（手填，存在 %APPDATA%）
//     更新节奏      → CadenceCard → CadenceStats
//
// 【★ 主图可换】
//   换主图 = 改下面 MainChartHost.Content 那一行。这一页的布局一个字都不用动。
//
// 【★ 界面调整这件事】
//   三块（主图 / 账号卡 / 作品列表）就是 XAML 里上下摞的三个 Border，
//   它们互不认识、各自独立：想换顺序就剪切粘贴，想改大小就改 Margin。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiteCut.Workbench.Controls;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Review;

namespace LiteCut.Workbench.Views;

public partial class AccountReviewView : UserControl, INavPage
{
    /// <summary>「平台 ▾」里代表"不筛"的那一项。</summary>
    private const string AllPlatforms = "全部";

    /// <summary>没读过盘时的兜底平台名（第一次用，哪儿都还没有平台）。</summary>
    private const string DefaultPlatform = "抖音";

    private readonly CadenceCard _chart = new();
    private readonly List<NumberField> _fields = new();

    private AccountReviewFile _file = new();

    /// <summary>正在程序化地铺下拉列表 —— 期间别把它当成"用户换了平台"。</summary>
    private bool _building;

    /// <summary>点某一行的「打开 ›」→ 交给宿主切到作品页。</summary>
    public event Action<ProjectRef>? OpenWorkRequested;

    public AccountReviewView()
    {
        InitializeComponent();

        MainChartHost.Content = _chart;          // ★ 换主图只改这一行
        BuildAccountFields();

        Loaded += (_, _) => Reload();
    }

    // ========================================================
    // 导航生命周期
    // ========================================================

    public void OnEnter() => Reload();

    /// <summary>没有"没保存的编辑"会丢 —— 保存动作都是显式的。</summary>
    public void OnLeave() { }

    private WorkbenchWindow? Shell => WorkbenchWindow.FindOwner(this);

    // ========================================================
    // ① 建那四个手填数（代码建，位置/大小随时可调）
    // ========================================================

    private void BuildAccountFields()
    {
        // 顺序就是界面上从左到右的顺序；想加/减/换位，改这个数组就行。
        // ★ 提醒：手填字段一直保持 **≤ 4 个** —— 能自动汇总的一律不让手填，
        //   否则"隔三差五回去抄一次"这件事迟早会坚持不下来。
        //   Key 是给调试遥控口用的稳定 Id（"acct:fans" 这样）——
        //   不设的话四个框的 Name 都叫 TxtValue，遥控口分不出哪个是哪个。
        var defs = new (string Title, string Foot, string Key)[]
        {
            ("粉丝总量",   "号上一共有多少人", "fans"),
            ("总播放",     "所有作品加起来",   "play"),
            ("总点赞",     "所有作品加起来",   "like"),
            ("近 30 天涨粉", "最近一个月的增量", "fans30d"),
        };

        foreach (var d in defs)
        {
            var f = new NumberField { Title = d.Title, Foot = d.Foot };
            f.Margin = new Thickness(0, 0, _fields.Count == defs.Length - 1 ? 0 : 10, 0);
            f.SetPlaceholder("—");
            System.Windows.Automation.AutomationProperties.SetAutomationId(f.ValueBox, "acct:" + d.Key);

            _fields.Add(f);
            StatGrid.Children.Add(f);
        }
    }

    // ========================================================
    // 读 → 铺
    // ========================================================

    private void Reload()
    {
        _file = AccountReviewStore.Load();

        BuildPlatformPickers();
        RefreshChart();
        RefreshAccountCard();
        RefreshWorks();
    }

    /// <summary>
    /// 两处平台下拉：
    ///   顶部那个 = **筛选**（看哪个平台的数），默认"全部"；
    ///   账号卡那个 = **这个号是哪个平台**（保存往哪儿写），必须具体。
    /// 不复用同一个：一个是"看"，一个是"写"，混在一起迟早会存错平台。
    /// </summary>
    private void BuildPlatformPickers()
    {
        var platforms = WorksBoard.Platforms();
        if (platforms.Count == 0) platforms = new List<string> { DefaultPlatform };

        _building = true;
        try
        {
            // ---- 顶部筛选 ----
            string? wantFilter = PickPlatform.SelectedItem as string;

            var filterItems = new List<string> { AllPlatforms };
            filterItems.AddRange(platforms);
            PickPlatform.ItemsSource = filterItems;
            PickPlatform.SelectedItem = filterItems.Contains(wantFilter ?? "")
                                        ? wantFilter : AllPlatforms;

            // ---- 账号卡的"这个号是" ----
            string? wantSave = PickSavePlatform.SelectedItem as string;

            PickSavePlatform.ItemsSource = new List<string>(platforms);
            PickSavePlatform.SelectedItem = platforms.Contains(wantSave ?? "")
                                            ? wantSave : platforms[0];
        }
        finally
        {
            _building = false;
        }

        if (string.IsNullOrWhiteSpace(InStatsDate.Text))
            InStatsDate.Text = AccountReviewStore.Today;
    }

    /// <summary>当前筛选的平台（"" = 全部）。</summary>
    private string FilterPlatform => PickPlatform.SelectedItem as string is { } s && s != AllPlatforms ? s : "";

    /// <summary>热力图：所有作品的发布日（跟平台无关 —— "有没有在稳定出片"不分平台）。</summary>
    private void RefreshChart() => _chart.SetDates(WorksBoard.PublishDates());

    private void RefreshAccountCard()
    {
        var snap = AccountReviewStore.Build(_file, FilterPlatform);

        long[] vals = { snap.Fans, snap.TotalPlay, snap.TotalLike, snap.Fans30d };
        for (int i = 0; i < _fields.Count && i < vals.Length; i++)
            _fields[i].SetValue(vals[i], snap.Has);

        string scope = string.IsNullOrEmpty(FilterPlatform) ? "全部平台" : FilterPlatform;

        TxtAccountSub.Text = snap.Has
            ? $"现在显示：{scope}　·　{snap.SourceNote}　·　统计日 {snap.StatsDate}"
            : $"现在显示：{scope}　·　还没填过账号数据 —— 选好「这个号是」和统计日，把四个数填上点保存。";

        BtnSaveAccount.IsEnabled = PickSavePlatform.SelectedItem is string;
    }

    private void RefreshWorks()
    {
        string plat = FilterPlatform;
        var rows = WorksBoard.Build(plat);

        ListWorks.ItemsSource = rows;

        TxtWorksSub.Text = string.IsNullOrEmpty(plat)
            ? "一行 = 一支片。数字读各项目自己的复盘数据自动加出来：每个平台各取最新的一份快照，再相加。" +
              "（完播率是比率、加不起来，所以它只出现在作品页里。）发布日可以直接在表里改。"
            : $"一行 = 一支片，只列在「{plat}」有记录的片子。数字取该平台最新的一份快照。" +
              "发布日可以直接在表里改。";

        int filled = rows.Count(r => r.HasData);
        int pending = rows.Count - filled;

        TxtWorksFoot.Text = rows.Count == 0
            ? "还没有作品 —— 先去「进行中」立个项目、做完发出去，这里就有内容了。"
            : $"共 {rows.Count} 支（已录数据 {filled} 支" +
              (pending > 0 ? $"，待填数据 {pending} 支" : "") + "）。" +
              "点右边的「打开 ›」进去填这支片的完整数据（播放/完播/点赞/涨粉 + 三段结论）。";
    }

    // ========================================================
    // 用户动作
    // ========================================================

    private void PickPlatform_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;

        // 筛到具体平台时，把"这个号是"跟着拨过去 —— 免得"看着抖音、存到视频号"
        if (PickSavePlatform.ItemsSource is IEnumerable<string> list && !string.IsNullOrEmpty(FilterPlatform))
        {
            string want = FilterPlatform;
            if (list.Contains(want)) PickSavePlatform.SelectedItem = want;
        }

        RefreshAccountCard();
        RefreshWorks();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        Reload();
        Shell?.Status("已重新汇总（作品列表 + 账号数据 + 更新节奏）");
    }

    private void BtnSaveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (PickSavePlatform.SelectedItem is not string plat)
        {
            Warn("先选「这个号是」哪个平台。");
            return;
        }

        string date = InStatsDate.Text.Trim();
        if (date.Length == 0) date = AccountReviewStore.Today;

        if (ReviewStore.TryParseDate(date) == null)
        {
            Warn($"「统计日」认不出来：{date}\n\n请写成 年-月-日，例如 2026-10-15。");
            return;
        }

        if (!AccountReviewStore.SaveSnapshot(_file, plat, date,
                                             _fields[0].Value, _fields[1].Value,
                                             _fields[2].Value, _fields[3].Value, ""))
        {
            Warn("写不进去（磁盘问题或文件被占用）。这次填的数还留在界面上，别关窗口。");
            return;
        }

        _file = AccountReviewStore.Load();
        RefreshAccountCard();

        Shell?.Status($"已保存 {plat} 这一组账号数据（统计日 {date}）");
    }

    private void BtnOpenWork_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is WorkRow row)
            OpenWorkRequested?.Invoke(row.Ref);
    }

    private void ListWorks_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ListWorks.SelectedItem is WorkRow row) OpenWorkRequested?.Invoke(row.Ref);
    }

    /// <summary>
    /// 行内改发布日：焦点离开那一格就落盘。
    ///
    /// 【为什么不要求"点保存"】
    ///   热力图的唯一数据源就是它。多一步确认，人就会"待会儿一起填"——
    ///   然后就没有然后了。改完即存，行内改日期才有意义。
    ///
    /// 【为什么那一格是 OneWay 绑定（★ 踩过一次）】
    ///   一开始写成 TwoWay + LostFocus：结果是"输入框自己要往对象里写一份，
    ///   我这边再写一份"，两边抢 —— 绑定先进来把值写进 row.PublishDate，
    ///   这里一看"值跟对象里一样"就直接 return 了，**磁盘上一个字都没落**，
    ///   界面上却看着像改成功了（最坏的一种 bug：静默失效）。
    ///   现在口径统一：**绑定只负责显示，落盘只有这一个地方**。
    /// </summary>
    private void InPublishDate_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not WorkRow row) return;

        string typed = (box.Text ?? "").Trim();

        // 清空 = 撤销发布日（比如填错了片子）
        if (typed.Length == 0)
        {
            if (row.PublishDate.Length == 0) return;
            row.PublishDate = "";
            ReviewStore.SetPublishDate(row.Ref, "");
            RefreshChart();
            Shell?.Status($"已清掉「{row.Name}」的发布日");
            return;
        }

        var d = ReviewStore.TryParseDate(typed);
        if (d == null)
        {
            // 认不出来的日期**不写盘**：一个坏日期会让热力图整块歪掉
            box.Text = row.PublishDate;
            Warn($"发布日认不出来：{typed}\n\n请写成 年-月-日，例如 2026-10-02。");
            return;
        }

        string norm = d.Value.ToString("yyyy-MM-dd");
        if (norm == row.PublishDate) return;

        row.PublishDate = norm;                      // 框里的字跟着变成规范写法
        ReviewStore.SetPublishDate(row.Ref, norm);
        RefreshChart();
        Shell?.Status($"「{row.Name}」的发布日记成 {norm}（更新节奏已跟着变）");
    }

    /// <summary>一键「今天」：片子上线了点一下，记今天发布。</summary>
    private void BtnPublishedToday_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not WorkRow row) return;

        string today = AccountReviewStore.Today;
        row.PublishDate = today;
        ReviewStore.SetPublishDate(row.Ref, today);
        RefreshChart();
        Shell?.Status($"「{row.Name}」记成今天（{today}）发布");
    }

    private static void Warn(string msg) =>
        MessageBox.Show(msg, "复盘", MessageBoxButton.OK, MessageBoxImage.Warning);
}
