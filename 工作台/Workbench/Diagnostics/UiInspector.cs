// ============================================================
// UiInspector.cs - 看界面 / 点界面（给遥控口用）
// ============================================================
//
// 【为什么需要它】
//   以前想"看看某个按钮长什么样、点了会怎样"，只能人肉截图或者写一个
//   专门的验证工程把页面 new 出来。两个办法都慢，而且看不见"现在屏幕上是什么"。
//
//   这一层把"界面上有哪些能点的东西"变成一份可读的清单，并且能真的去点它：
//     ① Collect()  —— 遍历当前所有窗口的可视树，列出所有可交互控件
//     ② Invoke()   —— 像真人一样点一个按钮（走 WPF 的自动化对等体，
//                      等价于鼠标按下去，会触发 Click / Checked 那一整套）
//     ③ SetText()  —— 往输入框里打字
//     ④ SetToggle()—— 勾选 / 取消勾选
//     ⑤ SelectIndex() —— 选下拉 / 列表的第几项
//
// 【控件 ID 是怎么来的】
//   优先用 WPF 自己的 AutomationId（x:Name 或代码里设的），没有就现造一个
//   （父级名 + "~" + 在父级里的序号）。所以：
//     · 有名字的按钮   → "BtnApply"
//     · DataTemplate 里的卡片按钮 → 名字会重复，靠 label 内容 + index 定位
//   实际用的时候最省事的还是**按文字找**：{"label":"应用到下游"}。
//
// 【为什么要遍历"所有窗口"】
//   设置窗口是一个独立的 Window，菜单是独立的 Popup。
//   只遍历主窗口的话，Agent 就点不到设置里的东西。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LiteCut.Workbench.Diagnostics;

/// <summary>界面上一个可交互控件的快照。</summary>
public sealed class UiControl
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Visible { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string Window { get; set; } = "";
}

/// <summary>控件快照 + 它对应的真实元素（内部用）。</summary>
public sealed class UiHit
{
    public FrameworkElement Element { get; init; } = null!;
    public UiControl Info { get; init; } = null!;
}

public static class UiInspector
{
    /// <summary>当前处于打开状态的右键/下拉菜单（它们在独立的视觉树里，得自己记）。</summary>
    private static readonly List<ContextMenu> OpenMenus = new();

    private static bool _menusHooked;

    /// <summary>
    /// 挂上"菜单开了 / 关了"的全局监听。
    /// ★ 用 RegisterClassHandler 是**类级**的 —— 一次性覆盖以后出现的所有 ContextMenu，
    ///   不用在每个菜单上单独挂。
    /// </summary>
    public static void HookMenus()
    {
        if (_menusHooked) return;
        _menusHooked = true;

        try
        {
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is ContextMenu cm && !OpenMenus.Contains(cm)) OpenMenus.Add(cm);
                }));

            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is ContextMenu cm) OpenMenus.Remove(cm);
                }));
        }
        catch
        {
            // 挂不上也不影响主流程（只是开着的菜单扫不到）
        }
    }

    // ========================================================
    // 看
    // ========================================================

    /// <summary>把当前所有窗口（含打开中的菜单）里可交互的控件列一遍。</summary>
    public static List<UiHit> Collect()
    {
        var list = new List<UiHit>();
        var app = Application.Current;
        if (app == null) return list;

        try
        {
            foreach (Window w in app.Windows)
            {
                if (w == null || !w.IsVisible) continue;
                string tag = string.IsNullOrEmpty(w.Name) ? w.GetType().Name : w.Name;
                Walk(w, tag, w, list, 0);
            }
        }
        catch
        {
            // 窗口枚举期间有窗口被关掉是常事，忽略
        }

        // 打开着的右键菜单：它们在独立的 Popup 里，不在窗口的可视树中
        foreach (var cm in OpenMenus.ToArray())
        {
            try
            {
                if (!cm.IsVisible) continue;
                Walk(cm, "Menu", Window.GetWindow(cm), list, 0);
            }
            catch { }
        }

        return list;
    }

    private static void Walk(DependencyObject node, string scope, Window? owner,
                             List<UiHit> list, int depth)
    {
        if (depth > 80) return;

        // Popup 的内容在另一棵可视树里 —— VisualTreeHelper 拿不到，得显式下去
        if (node is Popup pop)
        {
            if (pop.Child is DependencyObject pc) Walk(pc, scope, owner, list, depth + 1);
            return;
        }

        int count = ChildCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject? child;
            try { child = VisualTreeHelper.GetChild(node, i); }
            catch { continue; }
            if (child == null) continue;

            string nextScope = scope;

            if (child is FrameworkElement fe)
            {
                string? id = IdentityOf(fe);
                if (id == null && IsInteractive(fe)) id = scope + "~" + i;

                if (id != null) nextScope = id;
                if (IsInteractive(fe)) list.Add(new UiHit { Element = fe, Info = InfoOf(fe, id ?? "", owner) });
            }

            Walk(child, nextScope, owner, list, depth + 1);
        }
    }

    private static int ChildCount(DependencyObject node)
    {
        if (node is not Visual && node is not Visual3D) return 0;
        try { return VisualTreeHelper.GetChildrenCount(node); }
        catch { return 0; }
    }

    /// <summary>
    /// 界面上的"能操作"的控件种类。内容展示型的（TextBlock/Border）不算。
    ///
    /// ★ 刻意排除的两类（它们也是 ButtonBase，但不是给人点的按钮，列出来纯是噪音）：
    ///   RepeatButton —— 滚动条的上下箭头、ComboBox 的下拉箭头、Slider 的轨道
    ///   ScrollBar    —— 滚动条本体
    /// </summary>
    private static bool IsInteractive(DependencyObject d)
    {
        if (d is RepeatButton) return false;

        return d is ButtonBase || d is TextBox || d is RichTextBox || d is PasswordBox ||
               d is ComboBox || d is ListBox || d is Slider || d is TabItem || d is MenuItem;
    }

    /// <summary>一个控件对外报的 Id：AutomationId &gt; 元素名 &gt; 无。</summary>
    private static string? IdentityOf(FrameworkElement fe)
    {
        try
        {
            string aid = AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrWhiteSpace(aid)) return aid;
        }
        catch { }

        return string.IsNullOrWhiteSpace(fe.Name) ? null : fe.Name;
    }

    private static UiControl InfoOf(FrameworkElement fe, string id, Window? owner)
    {
        string type;
        string label;

        switch (fe)
        {
            case MenuItem mi:
                type = "MenuItem";
                label = mi.Header?.ToString() ?? "";
                break;
            case ButtonBase b:
                type = b.GetType().Name;
                label = TextOf(b);
                break;
            case TextBox t:
                type = t.GetType().Name;
                label = t.Text ?? "";
                break;
            case RichTextBox rtb:
                type = "RichTextBox";
                label = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.Trim();
                break;
            case PasswordBox pw:
                type = "PasswordBox";
                label = pw.Password.Length > 0 ? "••••••" : "";
                break;
            case ComboBox cb:
                type = "ComboBox";
                label = ItemTextOf(cb.SelectedItem);
                break;
            case ListBox lb:
                type = "ListBox";
                label = ItemTextOf(lb.SelectedItem);
                break;
            case Slider sl:
                type = "Slider";
                label = sl.Value.ToString("0.##");
                break;
            case TabItem ti:
                type = "TabItem";
                label = ti.Header?.ToString() ?? "";
                break;
            default:
                type = fe.GetType().Name;
                label = "";
                break;
        }

        // 用户/代码显式给的"可读名字"优先（导航项这类没 Content 的就靠它）
        try
        {
            string an = AutomationProperties.GetName(fe);
            if (!string.IsNullOrWhiteSpace(an)) label = an;
        }
        catch { }

        label = (label ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (label.Length > 120) label = label[..120] + "…";

        var info = new UiControl
        {
            Id = id,
            Type = type,
            Label = label,
            Enabled = fe.IsEnabled,
            Visible = fe.IsVisible && fe.ActualWidth > 0.5 && fe.ActualHeight > 0.5,
            W = Math.Round(fe.ActualWidth, 1),
            H = Math.Round(fe.ActualHeight, 1),
            Window = owner?.Title ?? "",
        };

        // 屏幕坐标 → 换算成"相对窗口左上角"，方便 Agent 判断在界面的哪个位置
        try
        {
            Point p = fe.PointToScreen(new Point(0, 0));
            Point o = owner != null ? owner.PointToScreen(new Point(0, 0)) : new Point(0, 0);
            info.X = Math.Round(p.X - o.X, 1);
            info.Y = Math.Round(p.Y - o.Y, 1);
        }
        catch
        {
            // Popup 里的元素偶尔算不出来，留 0 就行
        }

        return info;
    }

    private static string TextOf(ButtonBase b)
    {
        if (b.Content is string s) return s;

        try
        {
            if (b.Content is TextBlock tb) return tb.Text;
            if (b.Content is Panel panel) return FlattenPanel(panel);
            if (b.ToolTip is string tts && !string.IsNullOrWhiteSpace(tts)) return tts;
        }
        catch { }

        return b.Content?.ToString() ?? "";
    }

    private static string FlattenPanel(Panel p)
    {
        var parts = new List<string>();
        foreach (var c in p.Children)
        {
            switch (c)
            {
                case TextBlock t: parts.Add(t.Text); break;
                case string s: parts.Add(s); break;
                case Panel inner: parts.Add(FlattenPanel(inner)); break;
                default: parts.Add(c?.ToString() ?? ""); break;
            }
        }
        return string.Join(" ", parts).Trim();
    }

    private static string ItemTextOf(object? item) => item switch
    {
        null => "",
        ComboBoxItem ci => ci.Content?.ToString() ?? "",
        ListBoxItem li => li.Content?.ToString() ?? "",
        _ => item.ToString() ?? "",
    };

    // ========================================================
    // 读屏（把界面上"写着的字"按阅读顺序列出来）
    // ========================================================

    /// <summary>
    /// 把当前界面上**看得见的文字**按阅读顺序列出来（就是"读一屏"）。
    ///
    /// 【为什么还要这个】
    ///   Collect() 只报"能点的东西"，但界面上一半的信息是**写着的字**：
    ///   状态栏、提示语、统计数字、"还剩 3 天"…… 以前只能截图看。
    ///   这一层把字抽出来，Agent 就能像人一样"读"这一屏，不必截图。
    ///
    /// 【去重规则】
    ///   Label 的内容和它内部 TextBlock 的文字是同一句，会连着出现两次 ——
    ///   所以"和上一句一样就丢掉"。按钮里包着的 TextBlock 由按钮自己报，不重复。
    /// </summary>
    public static List<string> TextLines(string? filter)
    {
        var raw = new List<string>();
        var app = Application.Current;
        if (app == null) return raw;

        try
        {
            foreach (Window w in app.Windows)
            {
                if (w == null || !w.IsVisible) continue;
                WalkText(w, raw, 0);
            }
        }
        catch { }

        foreach (var cm in OpenMenus.ToArray())
        {
            try { if (cm.IsVisible) WalkText(cm, raw, 0); } catch { }
        }

        var outp = new List<string>();
        string prev = "\u0000";
        foreach (var s0 in raw)
        {
            string s = (s0 ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0 || s == prev) continue;
            prev = s;
            if (!string.IsNullOrWhiteSpace(filter) &&
                s.IndexOf(filter!, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (s.Length > 200) s = s[..200] + "…";
            outp.Add(s);
        }
        return outp;
    }

    private static void WalkText(DependencyObject node, List<string> lines, int depth)
    {
        if (depth > 80) return;

        if (node is Popup pop)
        {
            if (pop.Child is DependencyObject pc) WalkText(pc, lines, depth + 1);
            return;
        }

        int count = ChildCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject? child;
            try { child = VisualTreeHelper.GetChild(node, i); }
            catch { continue; }
            if (child == null) continue;

            if (child is FrameworkElement fe && fe.IsVisible &&
                fe.ActualWidth > 0.5 && fe.ActualHeight > 0.5)
            {
                if (child is TextBlock tb) lines.Add(tb.Text);
                else if (child is Label lb) lines.Add(lb.Content?.ToString() ?? "");

                // ★ V1.15.0：输入框里的字也算"屏上的字"。
                //   漏掉它们的话，"读一屏"读不到**填了什么** ——
                //   而"我刚才填的那个数到底进去了没有"恰恰是最需要看的。
                //   只读文本框，绝不读别的东西（密码框只报位数，不报内容）。
                else if (child is TextBox tbx) lines.Add(tbx.Text);
                else if (child is PasswordBox pbx) lines.Add($"••••（{pbx.Password.Length} 位）");
                else if (child is RichTextBox rtb)
                {
                    var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
                    lines.Add(range.Text);
                }
            }

            WalkText(child, lines, depth + 1);
        }
    }

    // ========================================================
    // 找
    // ========================================================

    /// <summary>
    /// 按条件找控件。三个条件都是可选的，一起给就是"同时满足"。
    /// </summary>
    /// <param name="id">控件 Id（精确，或忽略大小写的后缀匹配 —— "BtnApply" 能命中 "Step2ShotView.BtnApply"）</param>
    /// <param name="label">控件上显示的文字（包含匹配，忽略大小写）</param>
    /// <param name="type">类型名，如 Button / ComboBox</param>
    /// <param name="index">命中多个时取第几个（从 0 开始；负数 = 取最后一个）</param>
    public static (UiHit? Hit, string Detail) Find(string? id, string? label, string? type, int index)
    {
        var all = Collect();
        var matched = new List<UiHit>();

        foreach (var h in all)
        {
            if (!string.IsNullOrWhiteSpace(id) && !IdMatches(h.Info.Id, id!)) continue;

            if (!string.IsNullOrWhiteSpace(label) &&
                h.Info.Label.IndexOf(label!, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (!string.IsNullOrWhiteSpace(type) &&
                !string.Equals(h.Info.Type, type, StringComparison.OrdinalIgnoreCase)) continue;

            matched.Add(h);
        }

        if (matched.Count == 0)
        {
            string cond = DescribeCond(id, label, type);
            return (null, $"没找到符合条件的控件（{cond}）。当前界面共 {all.Count} 个可操作控件，" +
                          "可以先 GET /ui/tree 看一眼。");
        }

        int pick = index < 0 ? matched.Count - 1 : index;
        if (pick >= matched.Count) pick = matched.Count - 1;

        // ★ 命中多个时把"都有谁"列出来 —— 不同页面的按钮很容易重名（比如
        //   复盘页的「保存为新快照」和外壳的「保存项目」都叫 BtnSave），
        //   只报"取第 1 个"会让人点错还不知道错在哪。
        string detail =
            $"命中 {matched.Count} 个，取第 {pick + 1} 个：" +
            $"{matched[pick].Info.Type}「{matched[pick].Info.Label}」(Id={matched[pick].Info.Id})";

        if (matched.Count > 1)
        {
            var brief = new List<string>();
            for (int i = 0; i < matched.Count; i++)
                brief.Add($"[{i}] {matched[i].Info.Type}「{matched[i].Info.Label}」#{matched[i].Info.Id}");
            detail += "　—— 全部命中：" + string.Join(" ｜ ", brief);
        }

        return (matched[pick], detail);
    }

    private static bool IdMatches(string actual, string want)
    {
        if (string.Equals(actual, want, StringComparison.OrdinalIgnoreCase)) return true;
        // 允许省略父级前缀："BtnApply" 命中 "Step2ShotView~3.BtnApply"
        return actual.EndsWith("." + want, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeCond(string? id, string? label, string? type)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(id)) parts.Add("id=" + id);
        if (!string.IsNullOrWhiteSpace(label)) parts.Add("label 含 " + label);
        if (!string.IsNullOrWhiteSpace(type)) parts.Add("type=" + type);
        return parts.Count == 0 ? "没给条件" : string.Join(" 且 ", parts);
    }

    // ========================================================
    // 动
    // ========================================================

    /// <summary>
    /// 点一个控件 —— 像真人一样。
    /// 走 WPF 的自动化对等体（AutomationPeer），和鼠标点下去是同一条路：
    /// 该触发的 Click / Checked / Unchecked 一个都不会少。
    /// </summary>
    public static string Invoke(FrameworkElement fe)
    {
        switch (fe)
        {
            case MenuItem mi:
                string how;
                var mpeer = UIElementAutomationPeer.CreatePeerForElement(mi) ?? new MenuItemAutomationPeer(mi);
                if (mpeer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider minv)
                {
                    minv.Invoke();
                    how = "已点击菜单项";
                }
                else
                {
                    mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, mi));
                    how = "已点击菜单项（RaiseEvent 兜底）";
                }

                // 点完把菜单收起来 —— 真人点完菜单也会关
                if (ItemsControl.ItemsControlFromItemContainer(mi) is ContextMenu parentMenu)
                    parentMenu.IsOpen = false;
                return how;

            case RadioButton rb:
                // 单选按钮：Checked 才是它的事件（本工程的单选全用 Checked）。
                // 已经选中的再点一次没有意义，直接返回，避免把它"切掉"。
                if (rb.IsChecked == true) return "已经是选中状态（没有重复触发）";
                rb.IsChecked = true;
                return "已选中（触发 Checked）";

            case CheckBox ck:
                ck.IsChecked = !(ck.IsChecked == true);
                return ck.IsChecked == true ? "已勾选" : "已取消勾选";

            case Button b:
                var peer = UIElementAutomationPeer.CreatePeerForElement(b) ?? new ButtonAutomationPeer(b);
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv)
                {
                    inv.Invoke();
                    return "已点击（Invoke）";
                }
                b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
                return "已点击（RaiseEvent 兜底）";

            case ToggleButton tb:
                tb.IsChecked = !(tb.IsChecked == true);
                return tb.IsChecked == true ? "已打开" : "已关闭";

            case TabItem ti:
                if (ti.Parent is TabControl tc) { tc.SelectedItem = ti; return "已切到该标签页"; }
                return "这个标签页没有归属的 TabControl";

            case ListBox lb:
                if (lb.SelectedIndex < 0 && lb.Items.Count > 0) { lb.SelectedIndex = 0; return "已选中第 1 项"; }
                return "列表控件本身不用点 —— 用 /ui/select 指定选第几项";

            default:
                return "这种控件不支持点击（" + fe.GetType().Name + "）";
        }
    }

    /// <summary>往输入框里打字（整段替换）。</summary>
    public static string SetText(FrameworkElement fe, string text)
    {
        switch (fe)
        {
            case TextBox t:
                t.Text = text;
                t.CaretIndex = t.Text.Length;

                // ★ 真人打完字会点到别处 —— 那一瞬间会触发 LostFocus，
                //   而"行内改发布日"这类框是**靠失焦落盘**的。
                //   只设 Text 不补这一下，就会出现"遥控改了但没存"的假象，
                //   测出来的结论是错的（这比测不出来更糟）。
                try { t.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, t)); }
                catch { }

                return $"已写入 {t.Text.Length} 个字符（触发 TextChanged + LostFocus）";

            case RichTextBox rtb:
                rtb.Document.Blocks.Clear();
                rtb.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text)));
                return "已写入富文本框";

            case PasswordBox pw:
                pw.Password = text;
                return "已写入密码框";

            default:
                return "这不是输入框（是 " + fe.GetType().Name + "）";
        }
    }

    /// <summary>勾选 / 取消勾选。</summary>
    public static string SetToggle(FrameworkElement fe, bool value)
    {
        if (fe is ToggleButton tb)
        {
            if (tb.IsChecked == value) return "本来就是" + (value ? "勾选" : "未勾选") + "状态";
            tb.IsChecked = value;
            return value ? "已勾选（触发 Checked）" : "已取消勾选（触发 Unchecked）";
        }
        return "这不是能勾选的控件（是 " + fe.GetType().Name + "）";
    }

    /// <summary>选下拉 / 列表的第几项，或者选内容等于某段文字的那一项。</summary>
    public static string SelectIndex(FrameworkElement fe, int index, string? byText)
    {
        ItemsControl? ic = fe switch
        {
            ComboBox cb => cb,
            ListBox lb => lb,
            _ => null,
        };

        if (ic == null) return "这不是下拉/列表（是 " + fe.GetType().Name + "）";
        if (ic.Items.Count == 0) return "这个列表是空的";

        int pick = index;

        if (!string.IsNullOrWhiteSpace(byText))
        {
            pick = -1;
            for (int i = 0; i < ic.Items.Count; i++)
            {
                string txt = ItemTextOf(ic.Items[i]);
                if (txt.IndexOf(byText!, StringComparison.OrdinalIgnoreCase) >= 0) { pick = i; break; }
            }
            if (pick < 0) return $"列表里没有内容含「{byText}」的项（共 {ic.Items.Count} 项）";
        }

        if (pick < 0) pick = 0;
        if (pick >= ic.Items.Count) return $"只有 {ic.Items.Count} 项，没有第 {pick + 1} 项";
        if (pick == ic.Items.Count - 1 && index >= ic.Items.Count) pick = ic.Items.Count - 1;

        if (ic is ComboBox c) c.SelectedIndex = pick;
        else if (ic is ListBox l) l.SelectedIndex = pick;

        return $"已选中第 {pick + 1} 项：{ItemTextOf(ic.Items[pick])}";
    }
}
