// ============================================================
// LabelDialog.cs - ★ V1.20.0 项目标签 / 交付登记窗口
// ============================================================
//
// 【这个窗口解决什么】
//   视频常常不是在工作台里做完的（交给外部 Agent、在外面剪、成片随手放），
//   工作台扫固定目录扫不到，就一直认为"还没出片"，归档按钮直接拒绝。
//   这里给用户一个**说了算**的地方：改标签、记发布日、登记外部成片路径。
//
// 【用户 2026-11-14 拍板的核心思路】
//   「标签是一等公民。软件按流程自动设，也允许手动调；不要过耦合，
//     非得指定文件夹里必须有个视频才认可就太死板了。」
//   → 所以这里：
//     ① 「跟随流程自动」是一个可选项，手动挑的标签永远优先；
//     ② 外部成片**只登记路径**，不复制/移动/删除，也不检查文件在不在；
//     ③ 这一步只写两个 json，**不碰任何视频文件**，归档要另外点。
//
// 【为什么用纯代码建窗口不用 XAML】
//   一次性的小对话框，跟 TextPrompt / AgentSettingsDialog 一个路子，
//   绕开 XAML 资源键、控件树建一半触发事件那类坑。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Theming;

namespace LiteCut.Workbench.Ui;

/// <summary>项目标签窗口。点「保存」才写盘；取消一个字都不动。</summary>
public sealed class LabelDialog : Window
{
    private readonly ProjectRef _r;

    private readonly RadioButton _radioAuto = new()
    {
        Content = "跟随流程自动（推荐）",
        GroupName = "LabelPick",
        FontSize = 12.5,
        Margin = new Thickness(0, 3, 14, 3),
        Foreground = Theme.Brush("TextMain"),
    };

    private readonly Dictionary<string, RadioButton> _radios = new();
    private readonly TextBox _boxDate = new() { FontSize = 13, Width = 150 };
    private readonly TextBox _boxFilms = new()
    {
        FontSize = 12.5,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 68,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBox _boxSource = new() { FontSize = 13 };
    private readonly TextBox _boxLink = new() { FontSize = 13 };
    private readonly TextBox _boxNote = new() { FontSize = 13 };

    /// <summary>保存过了才算真，调用方据此决定要不要刷新卡片墙。</summary>
    public bool Saved { get; private set; }

    public LabelDialog(ProjectRef r)
    {
        _r = r;

        Title = "项目标签 · " + r.Name;
        Width = 560;
        Height = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Brush("BgCard");
        Foreground = Theme.Brush("TextMain");
        FontFamily = new FontFamily("Microsoft YaHei");

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };

        root.Children.Add(new TextBlock
        {
            Text = "这支片做到哪一步了？",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush("TextBright"),
        });

        string current = ProjectLabels.Current(r);
        string auto = ProjectLabels.Auto(r);
        root.Children.Add(new TextBlock
        {
            Text = $"当前：{current}（{(ProjectLabels.IsManual(r) ? "手动设定" : "按流程自动")}）　·　" +
                   $"自动建议：{auto}\n依据：{ProjectLabels.AutoReason(r)}",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
            Foreground = Theme.Brush("TextDim"),
        });

        root.Children.Add(SectionLabel("标签"));
        root.Children.Add(BuildPicker(current));
        root.Children.Add(new TextBlock
        {
            Text = "手动挑一个就固定下来了，软件不会再自动改；想变回跟着流程走，选第一项。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = Theme.Brush("TextFaint"),
        });

        // ---- 发布日（只有「已发布」时才让改）----
        root.Children.Add(SectionLabel("发布日期（选「已发布」时填）"));
        string pub = ReviewStore.EffectivePublishDate(r);
        _boxDate.Text = string.IsNullOrWhiteSpace(pub) ? DateTime.Now.ToString("yyyy-MM-dd") : pub;
        StyleBox(_boxDate);
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal };
        dateRow.Children.Add(_boxDate);
        dateRow.Children.Add(new TextBlock
        {
            Text = "年-月-日　→ 会写进复盘，热力图靠它亮格",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = Theme.Brush("TextFaint"),
        });
        root.Children.Add(dateRow);
        root.Children.Add(new TextBlock
        {
            Text = "改回其它标签不会清掉已填的发布日（复盘数据是事实，不跟着标签删）。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 12),
            Foreground = Theme.Brush("TextFaint"),
        });

        // ---- 外部成片路径（★ 只登记）----
        root.Children.Add(SectionLabel("外部成片路径（选填 · 一行一个）"));
        StyleBox(_boxFilms);
        _boxFilms.Text = string.Join(Environment.NewLine, r.Model.Label?.ExternalFilms ?? new List<string>());
        root.Children.Add(_boxFilms);

        var filmRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var btnPick = new Button
        {
            Content = "添加文件…",
            FontSize = 12,
            Padding = new Thickness(12, 5, 12, 5),
            Background = Theme.Brush("BgInput"),
            Foreground = Theme.Brush("TextMain"),
            BorderBrush = Theme.Brush("Border"),
            BorderThickness = new Thickness(1),
        };
        btnPick.Click += (_, _) => PickFilms();
        filmRow.Children.Add(btnPick);
        filmRow.Children.Add(new TextBlock
        {
            Text = "只记路径，不复制 / 不移动 / 不删除，也不检查文件在不在",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = Theme.Brush("TextFaint"),
        });
        root.Children.Add(filmRow);
        root.Children.Add(new TextBlock
        {
            Text = "视频在工作台外面做完的，在这里登记一下就够了；归档时这些文件不会被搬走。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = Theme.Brush("TextFaint"),
        });

        // ---- 三个选填备注 ----
        root.Children.Add(SectionLabel("这支片是怎么做完的（选填）"));
        StyleBox(_boxSource);
        _boxSource.Text = r.Model.Label?.Source ?? "";
        root.Children.Add(_boxSource);

        root.Children.Add(SectionLabel("发布链接（选填）", 10));
        StyleBox(_boxLink);
        _boxLink.Text = r.Model.Label?.Link ?? "";
        root.Children.Add(_boxLink);

        root.Children.Add(SectionLabel("备注（选填）", 10));
        StyleBox(_boxNote);
        _boxNote.Text = r.Model.Label?.Note ?? "";
        root.Children.Add(_boxNote);

        root.Children.Add(new TextBlock
        {
            Text = "★ 保存只写 project.json 和复盘的发布日，不动任何视频文件。" +
                   "标签 ≠ 归档 —— 文件想先留着就先留着，想归档再点卡片上的「归档」。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = Theme.Brush("TextWarm"),
        });

        // ---- 外框：上面滚动，按钮钉在底部 ----
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 8, 20, 14),
        };
        var cancel = MakeButton("取消", primary: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = MakeButton("保存标签", primary: true);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(scroll);
        Grid.SetRow(scroll, 0);
        grid.Children.Add(buttons);
        Grid.SetRow(buttons, 1);
        Content = grid;

        SyncEnabled();
    }

    private UIElement BuildPicker(string current)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

        _radioAuto.Checked += (_, _) => SyncEnabled();
        wrap.Children.Add(_radioAuto);

        foreach (string name in ProjectLabels.Selectable)
        {
            var rb = new RadioButton
            {
                Content = name,
                GroupName = "LabelPick",
                FontSize = 12.5,
                Margin = new Thickness(0, 3, 14, 3),
                Foreground = Theme.Brush("TextMain"),
            };
            rb.Checked += (_, _) => SyncEnabled();
            _radios[name] = rb;
            wrap.Children.Add(rb);
        }

        if (ProjectLabels.IsManual(_r) && _radios.TryGetValue(current, out var pick))
            pick.IsChecked = true;
        else
            _radioAuto.IsChecked = true;

        return wrap;
    }

    private void SyncEnabled()
    {
        bool published = _radios.TryGetValue(ProjectLabels.Published, out var rb)
                         && rb.IsChecked == true;
        _boxDate.IsEnabled = published;
    }

    private void PickFilms()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选中成片（只登记路径，不会动这些文件）",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|所有文件|*.*",
        };

        if (dlg.ShowDialog(this) != true) return;

        var paths = ParseFilms();
        foreach (string f in dlg.FileNames)
            if (!paths.Contains(f, StringComparer.OrdinalIgnoreCase)) paths.Add(f);
        _boxFilms.Text = string.Join(Environment.NewLine, paths);

        // 顺手把「跟随自动」切到「已完成」——登记了成片通常就是做完了。
        // 用户已经手动挑了别的标签就不抢他的选择。
        if (_radioAuto.IsChecked == true && _radios.TryGetValue(ProjectLabels.Done, out var done))
            done.IsChecked = true;
    }

    private List<string> ParseFilms() =>
        (_boxFilms.Text ?? "")
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void Save()
    {
        string chosen = "";
        foreach (var kv in _radios)
            if (kv.Value.IsChecked == true) { chosen = kv.Key; break; }

        // 日期先校验再写盘，免得标签存了、发布日没存，两边对不上
        string pubDate = "";
        if (chosen == ProjectLabels.Published)
        {
            string typed = (_boxDate.Text ?? "").Trim();
            if (typed.Length == 0) typed = DateTime.Now.ToString("yyyy-MM-dd");
            var parsed = ReviewStore.TryParseDate(typed);
            if (parsed == null)
            {
                MessageBox.Show("发布日认不出来：\n\n请写成 年-月-日，例如 2026-11-14。",
                                "项目标签", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            pubDate = parsed.Value.ToString("yyyy-MM-dd");
        }

        try
        {
            var lp = _r.Model.Label ??= new LabelPart();
            lp.Manual = chosen;                 // 空 = 跟随流程自动
            lp.ManualSetAt = chosen.Length > 0 ? DateTime.Now.ToString("yyyy-MM-dd HH:mm") : "";
            lp.Source = (_boxSource.Text ?? "").Trim();
            lp.Link = (_boxLink.Text ?? "").Trim();
            lp.Note = (_boxNote.Text ?? "").Trim();
            lp.ExternalFilms = ParseFilms();

            ProjectStore.Save(_r);

            if (chosen == ProjectLabels.Published && !ReviewStore.SetPublishDate(_r, pubDate))
                MessageBox.Show("标签存好了，但复盘发布日没写进去（文件可能被占用）。\n" +
                                "去复盘页的「作品列表」里补一下发布日就行。",
                                "项目标签", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "项目标签",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Saved = true;
        Close();
    }

    // ---- 小零件 ----

    private static TextBlock SectionLabel(string text, double top = 0) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(0, top, 0, 4),
        Foreground = Theme.Brush("TextDim"),
    };

    private static void StyleBox(TextBox box)
    {
        box.Padding = new Thickness(8, 6, 8, 6);
        box.Background = Theme.Brush("BgInput");
        box.Foreground = Theme.Brush("TextMain");
        box.BorderBrush = Theme.Brush("Border");
        box.BorderThickness = new Thickness(1);
        box.CaretBrush = Theme.Brush("TextMain");
    }

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text,
        MinWidth = 90,
        Padding = new Thickness(14, 6, 14, 6),
        Margin = new Thickness(8, 0, 0, 0),
        FontSize = 13,
        Cursor = System.Windows.Input.Cursors.Hand,
        Foreground = Theme.Brush("TextOnAccent"),
        Background = primary ? Theme.Brush("AccentHot") : Theme.Brush("BgInput"),
        BorderBrush = Theme.Brush("Border"),
        BorderThickness = new Thickness(1),
    };
}
