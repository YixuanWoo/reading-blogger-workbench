// ============================================================
// InspirationView.xaml.cs - 灵感池
// ============================================================
//
// 【为什么「灵感」不做成一级导航里一个空壳】
//   空壳菜单项是会骗人的：点进去什么都没有，就再也不点了。
//   所以这里让它真能干活：记一条 → 想做了 → 一键立项 → 进项目开工。
//   立项之后这条灵感会被标记成"已立项"，不会重复立。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Ui;

namespace LiteCut.Workbench.Views;

public partial class InspirationView : UserControl, INavPage
{
    private readonly List<InspirationItem> _items = new();
    private readonly List<InspirationRow> _rows = new();

    public InspirationView()
    {
        InitializeComponent();
        ListItems.ItemsSource = _rows;
        Loaded += (s, e) => Refresh();
    }

    public void OnEnter() => Refresh();

    public void OnLeave() { }

    // ========================================================
    // 刷新
    // ========================================================

    public void Refresh()
    {
        _items.Clear();
        _items.AddRange(InspirationStore.Load());
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Clear();
        foreach (var it in _items) _rows.Add(BuildRow(it));

        ListItems.ItemsSource = null;
        ListItems.ItemsSource = _rows;

        bool empty = _rows.Count == 0;
        TxtEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ScrollList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private static InspirationRow BuildRow(InspirationItem it)
    {
        bool created = !string.IsNullOrWhiteSpace(it.ProjectDir);

        return new InspirationRow
        {
            Id = it.Id,
            Title = it.Title,
            Note = string.IsNullOrWhiteSpace(it.Note) ? "" : it.Note,
            NoteVisibility = string.IsNullOrWhiteSpace(it.Note)
                ? Visibility.Collapsed : Visibility.Visible,
            Meta = created
                ? $"已立项 · {it.CreatedAt:yyyy-MM-dd}"
                : $"记于 {it.CreatedAt:yyyy-MM-dd HH:mm}",
        };
    }

    // ========================================================
    // 操作
    // ========================================================

    private WorkbenchWindow? Shell => WorkbenchWindow.FindOwner(this);

    /// <summary>
    /// 写回灵感池，失败时**说清它没成功**。返回是否真的写进了磁盘。
    ///
    /// 【为什么要有这一个私有方法】
    ///   以前三处（记一条 / 立项 / 删除）都是 <c>InspirationStore.Save(_items);</c> 裸调：
    ///   写失败（磁盘满、文件被网盘/杀毒占着）界面照样说"已记下这条灵感"，
    ///   下次打开却是空的 —— 用户以为存住了。现在统一在这里判结果、统一文案。
    ///
    /// 【文案要说清三件事】
    ///   ① 没保存成功（不是"稍后再试"这种含糊话）；
    ///   ② 常见原因（磁盘空间 / 文件被占用）；
    ///   ③ 清单还在界面上没丢 —— 这是用户最关心的，也确实是事实（内存里的 _items 没动）。
    /// </summary>
    /// <param name="action">这一次想干什么（"这条灵感" / "立项记录" / "这次删除"），用于拼话。</param>
    private bool SaveAll(string action)
    {
        if (InspirationStore.Save(_items)) return true;

        const string detail =
            "灵感池文件（%APPDATA%\\翊瑄的工作台\\inspiration.json）没能写进磁盘。\n\n" +
            "常见原因有两个：\n" +
            "  ① 磁盘空间不够（尤其是系统盘 / C 盘）；\n" +
            "  ② 文件被别的程序占着：网盘同步（OneDrive / 坚果云）、杀毒软件、正在编辑它的 Obsidian。\n\n" +
            "清单还在界面上，没有丢 —— 但现在它只在内存里。\n" +
            "处理完上面任意一条之后再操作一次，就能存进去了；在这之前别关程序。";

        MessageBox.Show(
            $"{action}没保存成功。\n\n{detail}",
            "灵感池没保存", MessageBoxButton.OK, MessageBoxImage.Warning);

        Shell?.Status($"⚠ 灵感池保存失败 —— {action}只在内存里，清单还在界面上没丢，别关程序");

        return false;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        string? title = TextPrompt.Show(Shell, "记一条灵感", "想法是什么：");
        if (title == null) return;

        string? note = TextPrompt.Show(Shell, "补充说明", "想细说点什么（可以留空直接确定）：", "");

        _items.Insert(0, new InspirationItem
        {
            Title = title,
            Note = note ?? "",
            CreatedAt = DateTime.Now,
        });

        // 先刷界面再判结果：写失败时这条也得留在列表上（"清单没丢"才不是空话）
        bool saved = SaveAll("这条灵感");
        Rebuild();

        // 写失败时不许说"已记下" —— 那是在骗人
        if (saved) Shell?.Status("已记下这条灵感");
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        var it = ItemOf(sender);
        if (it == null) return;

        if (!string.IsNullOrWhiteSpace(it.ProjectDir) && System.IO.Directory.Exists(it.ProjectDir))
        {
            var open = MessageBox.Show(
                "这条灵感已经立过项了。要打开它吗？",
                "已立项", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (open != MessageBoxResult.OK) return;

            var existing = ProjectStore.Load(it.ProjectDir);
            if (existing != null) Shell?.OpenProject(existing);
            return;
        }

        try
        {
            var r = ProjectStore.Create(it.Title, it.Channel, it.Note);
            it.ProjectDir = r.Dir;

            // 项目文件夹是真的建好了 —— 先把用户带进去，别让他以为"没立成"再点一次。
            Shell?.OpenProject(r);

            // 再判账本：写失败时"已立项"这句不能说，红字会盖掉上面那句"已打开项目"
            if (SaveAll("这条灵感的立项记录"))
                Shell?.Status($"已立项：{r.Name}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("立项失败：" + ex.Message, "立项",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var it = ItemOf(sender);
        if (it == null) return;

        _items.Remove(it);
        SaveAll("这次删除");
        Rebuild();
    }

    private InspirationItem? ItemOf(object sender)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not InspirationRow row) return null;
        return _items.FirstOrDefault(i => i.Id == row.Id);
    }

    // ========================================================
    // 列表行（只用于绑定显示）
    // ========================================================

    public sealed class InspirationRow
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Note { get; set; } = "";
        public Visibility NoteVisibility { get; set; } = Visibility.Collapsed;
        public string Meta { get; set; } = "";
    }
}
