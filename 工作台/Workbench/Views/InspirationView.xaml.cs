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
        InspirationStore.Save(_items);
        Rebuild();
        Shell?.Status("已记下这条灵感");
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
            InspirationStore.Save(_items);

            Shell?.Status($"已立项：{r.Name}");
            Shell?.OpenProject(r);
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
        InspirationStore.Save(_items);
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
