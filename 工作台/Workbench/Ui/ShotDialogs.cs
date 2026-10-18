// ============================================================
// ShotDialogs.cs - 分镜页的两个确认框（V1.1.0 / T-16）
// ============================================================
//
// 【为什么是两个完全不同的框，而不是一个】
//   按"摩擦分两处"的设计（T-16 §3.2.1）：
//
//     拆 / 合        → 只问「设计怎么处理」—— 这是草稿内的事，跟下游无关，
//                       所以框里**只字不提产物**（提了就是吓唬人）。
//     应用到下游     → 才摊代价（改多少文件、多少镜待重渲），按水位加权。
//
//   合成一个框的后果：拆的时候就把"44 个产物要改名"糊人脸上，
//   而那一刻其实下游一个字都没动 —— 这不叫提醒，这叫添乱。
//
// 【为什么用代码建窗口，不写 XAML】
//   一次性对话框写成 XAML 会踩"控件树建到一半触发事件"的老坑
//   （Step2ShotView 5.6 节记过：XamlReader 期间触发 handler → NullReference →
//    整页起不来）。代码建窗口没有这个问题，也省一个文件配对。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteCut.Workbench.Theming;

namespace LiteCut.Workbench.Ui;

/// <summary>拆分时「原来的设计怎么办」的选择。</summary>
public enum DesignChoice
{
    /// <summary>用户点了取消 / 关窗口。</summary>
    Cancel,

    /// <summary>保留原镜设计（默认选择）—— 原镜打「待复核」，新镜置空挂「待补设计」。</summary>
    Keep,

    /// <summary>彻底清空重新设计 —— 两镜都清空，都挂「待补设计」。</summary>
    Clear,
}

/// <summary>分镜页的确认框。</summary>
public static class ShotDialogs
{
    private static Brush B(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    // ★ V1.18.0：配色改走主题。用属性而不是 static readonly —— 字段只初始化一次，
    //   切过主题之后再弹的对话框会一直用第一次那套颜色。
    private static Brush CText => Theme.Brush("TextMain");
    private static Brush CDim => Theme.Brush("TextDim");
    private static Brush CBg => Theme.Brush("BgCard");
    private static Brush CBgIn => Theme.Brush("BgInput");
    private static Brush CBorder => Theme.Brush("Border");
    private static Brush CWarn => Theme.Brush("Warn");
    private static Brush CDanger => Theme.Brush("Danger");

    // ========================================================
    // 框 A：拆 / 合 —— 只问设计
    // ========================================================

    /// <summary>
    /// 拆 / 合之前的确认框。设计全空时**不要调它**（那一档本来就不该打扰用户）。
    /// </summary>
    /// <param name="preview">后果预览，每行一句（"#7 前半 3.2s 设计保留，标「待复核」"）</param>
    public static DesignChoice AskDesignChange(Window? owner, string title,
                                              IReadOnlyList<string> preview, bool isSplit)
    {
        var win = Shell(title, owner, 520, 0);
        var root = (StackPanel)win.Content;

        root.Children.Add(Label("拆开之后会变成这样：", CDim, 11.5, new Thickness(0, 0, 0, 6)));
        root.Children.Add(Box(preview, CText));

        root.Children.Add(Label("原来的设计怎么办？", CText, 12.5, new Thickness(0, 12, 0, 6)));

        var rKeep = Radio(true,
            isSplit ? "保留原镜设计，我回头自己看着改"
                    : "两镜的设计拼接保留（不丢信息，不合适你直接删后半段）",
            isSplit ? "原镜（前半）的设计全文保留，标「待复核」；新镜（后半）留空挂「待补设计」"
                    : "合并后这一镜的内容确实就是原来两段连续的，所以两段设计拼起来刚好覆盖新范围",
            CBg, CText, CDim);
        var rClear = Radio(false,
            isSplit ? "彻底清空，两镜都重新设计" : "清空，整镜重新设计",
            "两边的设计字段都清空，挂「待补设计」，交给 Agent 重做",
            CBg, CText, CDim);

        root.Children.Add(rKeep);
        root.Children.Add(rClear);

        var result = DesignChoice.Cancel;
        if (Buttons(win, root, "取消", "确定", () => { }, () =>
            {
                result = rClear.IsChecked == true ? DesignChoice.Clear : DesignChoice.Keep;
            }) != true) return DesignChoice.Cancel;

        return result;
    }

    // ========================================================
    // 框 B：应用到下游 —— 这里才摊代价
    // ========================================================

    /// <summary>
    /// 交接确认框。
    /// </summary>
    /// <param name="lines">代价逐条（danger=true 的用红字 —— L2 以上才给红字）</param>
    /// <param name="confirmText">勾选框文字；空串 = 不用勾</param>
    public static bool ConfirmCommit(Window? owner, string title,
                                     IReadOnlyList<(string text, bool danger)> lines,
                                     string confirmText)
    {
        var win = Shell(title, owner, 560, 0);
        var root = (StackPanel)win.Content;

        root.Children.Add(Label("这次交接会：", CText, 12.5, new Thickness(0, 0, 0, 6)));

        var listed = new List<string>();
        foreach (var (text, danger) in lines) listed.Add((danger ? "⚠ " : "· ") + text);
        var box = Box(listed, CText);
        foreach (var tb in TextBlocksOf(box))
            if (tb.Text.StartsWith("⚠", StringComparison.Ordinal)) tb.Foreground = CDanger;
        root.Children.Add(box);

        CheckBox? chk = null;
        if (!string.IsNullOrEmpty(confirmText))
        {
            chk = new CheckBox
            {
                Content = confirmText,
                Foreground = CWarn,
                FontSize = 12.5,
                Margin = new Thickness(0, 12, 0, 0),
            };
            root.Children.Add(chk);
        }

        bool ok = false;
        var done = Buttons(win, root, "取消", "交接", () => { }, () => { ok = true; });
        if (done != true) return false;
        if (chk != null && chk.IsChecked != true) return false;
        return ok;
    }

    // ========================================================
    // 搭窗口的小零件
    // ========================================================

    private static Window Shell(string title, Window? owner, double width, double height)
    {
        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };

        var win = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 640,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = CBg,
            FontFamily = new FontFamily("Microsoft YaHei"),
            Content = root,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        if (owner != null) win.Owner = owner;
        return win;
    }

    private static TextBlock Label(string text, Brush fg, double size, Thickness margin) =>
        new() { Text = text, Foreground = fg, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin };

    private static Border Box(IReadOnlyList<string> lines, Brush fg)
    {
        var sp = new StackPanel();
        foreach (var l in lines)
            sp.Children.Add(new TextBlock
            {
                Text = l, Foreground = fg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            });

        return new Border
        {
            Background = CBgIn, BorderBrush = CBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10),
            Child = sp,
        };
    }

    private static IEnumerable<TextBlock> TextBlocksOf(DependencyObject d)
    {
        if (d is TextBlock tb) yield return tb;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
            foreach (var t in TextBlocksOf(VisualTreeHelper.GetChild(d, i))) yield return t;
    }

    private static RadioButton Radio(bool check, string head, string tip,
                                     Brush bg, Brush fg, Brush dim)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = head, Foreground = fg, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock
        {
            Text = tip, Foreground = dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new RadioButton
        {
            GroupName = "designChoice",
            IsChecked = check,
            Content = sp,
            Foreground = fg,
            Background = bg,
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 0, 0, 6),
        };
    }

    /// <summary>底部两个按钮。返回 true = 点了右边那个。返回 null = 直接关窗口。</summary>
    private static bool? Buttons(Window win, StackPanel root, string left, string right,
                                 Action onLeft, Action onRight)
    {
        bool? outcome = null;

        var l = new Button
        {
            Content = left, MinWidth = 88, Padding = new Thickness(14, 6, 14, 6),
            Background = CBgIn, Foreground = CText, BorderBrush = CBorder,
            BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
        };
        var r = new Button
        {
            Content = right, MinWidth = 88, Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(10, 0, 0, 0),
            Background = B("AccentHot", Brushes.SteelBlue), Foreground = Theme.Brush("TextOnAccent"),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        l.Click += (_, _) => { outcome = false; onLeft(); win.Close(); };
        r.Click += (_, _) => { outcome = true; onRight(); win.Close(); };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        bar.Children.Add(l);
        bar.Children.Add(r);
        root.Children.Add(bar);

        win.ShowDialog();
        return outcome;
    }
}
