// ============================================================
// NavHost.cs - 把 NavNode 列表画成左侧导航按钮
// ============================================================
//
// 【职责边界】
//   NavNode  = 菜单"是什么"（数据）
//   NavTree  = 有哪些菜单项（结构）
//   NavHost  = 把数据画成按钮、维护"当前选中"（渲染）
//   三者分开，所以换显示风格（横排/图标栏/折叠树）不用动业务代码。
//
// 【为什么用代码建控件而不是 XAML 绑定】
//   NavNode 带一个"创建页面"的委托，纯 XAML 绑不了委托；
//   菜单项数量本来就少（个位数），代码建按钮更直白，也更好读懂。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteCut.Workbench.Theming;

namespace LiteCut.Workbench.Navigation;

/// <summary>
/// 导航栏渲染器：往一个 Panel 里铺按钮，并把"点了哪一项"回调出去。
/// </summary>
public sealed class NavHost
{
    private sealed class Entry
    {
        public NavNode Node = new();
        public RadioButton Button = new();
    }

    private readonly Panel _panel;
    private readonly Action<NavNode> _onPick;
    private readonly List<Entry> _entries = new();

    /// <summary>正在程序化地改选中状态，期间忽略按钮的 Checked 回调</summary>
    private bool _suppress;

    /// <summary>当前高亮的导航项 Id</summary>
    public string? ActiveId { get; private set; }

    public NavHost(Panel panel, Action<NavNode> onPick)
    {
        _panel = panel;
        _onPick = onPick;
    }

    /// <summary>
    /// 按给定菜单重新铺一遍导航栏。
    /// </summary>
    /// <param name="nodes">要显示的菜单项</param>
    /// <param name="activeId">哪一项高亮（null = 都不高亮）</param>
    public void Render(IReadOnlyList<NavNode> nodes, string? activeId)
    {
        _panel.Children.Clear();
        _entries.Clear();
        ActiveId = activeId;

        Style? navStyle = TryFindNavStyle();

        _suppress = true;
        try
        {
            // ★ 分组：同名连续节点的Group 非空且与上一项不同 → 插一条分隔线。
            //   只画视觉分段，不做二级菜单（NavNode.Group 的注释里说明了为什么）。
            string lastGroup = "";

            foreach (var node in nodes)
            {
                if (!string.IsNullOrEmpty(node.Group) && node.Group != lastGroup)
                {
                    _panel.Children.Add(BuildGroupHeader(node.Group));
                    lastGroup = node.Group;
                }

                var btn = new RadioButton
                {
                    GroupName = "NavHost",
                    Tag = node.Icon,
                    IsEnabled = node.Enabled,
                    IsChecked = node.Id == activeId,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };

                if (navStyle != null) btn.Style = navStyle;
                btn.Content = BuildContent(node);

                // ★ V1.14.0：给导航按钮一个**稳定的对外身份**。
                //   调试模式的遥控口靠它把"跳页"翻译成"点这个按钮" ——
                //   否则只能靠按钮上的文字找（文字会改、也会重名）。
                //   用 AutomationId 而不是 Name：Name 会进 WPF 的命名域，
                //   带点号的字符串在那儿是非法标识符。
                System.Windows.Automation.AutomationProperties.SetAutomationId(btn, "nav:" + node.Id);
                System.Windows.Automation.AutomationProperties.SetName(btn, node.Title);

                // 闭包捕获：每个按钮记住自己对应哪个节点
                var captured = node;
                btn.Checked += (s, e) =>
                {
                    if (_suppress) return;
                    ActiveId = captured.Id;
                    _onPick(captured);
                };

                _entries.Add(new Entry { Node = node, Button = btn });
                _panel.Children.Add(btn);
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>只改高亮，不重建控件（切页面时用）。</summary>
    public void SetActive(string? id)
    {
        ActiveId = id;
        _suppress = true;
        try
        {
            foreach (var e in _entries)
                e.Button.IsChecked = e.Node.Id == id;
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>拿到某个导航项对应的按钮（暂时没用到，留作将来加角标/计数）</summary>
    public RadioButton? ButtonOf(string id)
    {
        foreach (var e in _entries)
            if (e.Node.Id == id) return e.Button;
        return null;
    }

    // ========================================================
    // 内部
    // ========================================================

    /// <summary>
    /// 按钮内容。没有角标就是纯文字；有角标时右边挂一个小胶囊。
    /// </summary>
    private static object BuildContent(NavNode node)
    {
        if (string.IsNullOrEmpty(node.Badge)) return node.Title;

        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        var chip = new Border
        {
            Background = Theme.Brush("Border"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = node.Badge,
                FontSize = 9,
                Foreground = Theme.Brush("TextMuted"),
            },
        };

        sp.Children.Add(new TextBlock { Text = node.Title, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(chip);
        return sp;
    }

    /// <summary>
    /// ★ 分组标题：一条细分隔线 + 组名小字。
    ///
    /// 【为什么用文字而不是纯线条】
    ///   纯线条只能让人感觉到"这里断了一下"，说不出断的是哪一段。
    ///   写上「文稿 / 成片 / 资源」，扫一眼就知道这一组是干什么的。
    ///
    /// 【为什么不用 TreeView / Expander】
    ///   见 NavNode.Group 的注释：流水线只有七步，做成能展开的树
    ///   会让人误以为每一步底下还有子任务 —— 那是另一套心智，不要引进来。
    /// </summary>
    private static Border BuildGroupHeader(string group)
    {
        var tb = new TextBlock
        {
            Text = group,
            FontSize = 11,
            // 字间距拉开一点，视觉上退到背景里，不跟菜单项抢注意力
            Foreground = Theme.Brush("TextMuted"),
        };
        tb.SetValue(TextBlock.MarginProperty, new Thickness(14, 12, 0, 4));

        return new Border
        {
            Margin = new Thickness(6, 4, 6, 2),
            Padding = new Thickness(8, 0, 0, 0),
            BorderBrush = Theme.Brush("DividerFaint"),
            BorderThickness = new Thickness(0, 1, 0, 0),   // 上边一条细线 = 分组线
            Child = tb,
        };
    }

    /// <summary>取 App 级导航按钮样式；取不到（比如无界面环境）就用默认样式。</summary>
    private static Style? TryFindNavStyle()
    {
        try
        {
            return Application.Current?.TryFindResource("NavBtn") as Style;
        }
        catch
        {
            return null;
        }
    }
}
