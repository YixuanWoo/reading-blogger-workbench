// ============================================================
// DebugSession.cs - 调试模式的总装：把日志、监听、遥控口串起来
// ============================================================
//
// 【打开调试模式时，它一次性做四件事】
//   ① 打开日志（DebugLog）
//   ② 往窗口上挂"全局事件钩子" —— 按钮点击 / 输入框改动 / 列表选择
//      用窗口级 AddHandler(..., handledEventsToo: true)，所以**所有页面里的按钮**
//      都会冒泡到这里，不用挨个页面写日志。
//   ③ 盯住项目目录和配置目录（DebugFileWatch）→ "文件发生了什么"
//   ④ 起本地遥控口（UiRemoteServer）→ 让 Agent 能真的点界面
//
// 【为什么钩子挂在窗口上就够了】
//   WPF 的路由事件是**冒泡**的：页面里任何一个按钮的 Click 都会一路上浮到窗口。
//   而 ClickEvent 是 ButtonBase 的**路由事件**，能冒泡。
//   所以窗口这一层挂一次 = 全覆盖，且以后新加的按钮自动被覆盖。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LiteCut.Workbench.Diagnostics;

/// <summary>调试模式的一次"开启会话"。窗口持有它，开/关都走这里。</summary>
public sealed class DebugSession : IDisposable
{
    private readonly WorkbenchWindow _window;

    private UiRemoteServer? _server;
    private DebugFileWatch? _watch;
    private bool _hooked;

    /// <summary>最近一次"同一个控件、同一段文字"的输入 —— 用来去掉重复记录。</summary>
    private string _lastTextKey = "";

    public DebugSession(WorkbenchWindow window) => _window = window;

    /// <summary>现在开着没有。</summary>
    public bool Active { get; private set; }

    /// <summary>遥控口地址（没开时为空串）。</summary>
    public string Endpoint => _server?.Endpoint ?? "";

    /// <summary>遥控口起不来的原因（端口被占之类）。</summary>
    public string? LastError => _server?.LastError;

    /// <summary>调试日志文件路径。</summary>
    public string LogFile => DebugLog.FilePath;

    // ========================================================
    // 开 / 关
    // ========================================================

    /// <summary>打开调试模式。</summary>
    /// <param name="port">遥控口端口</param>
    /// <param name="logToFile">日志要不要落文件</param>
    public bool Start(int port, bool logToFile)
    {
        if (Active) return true;

        DebugLog.Enable(logToFile);
        HookEvents();
        StartWatch();

        _server = new UiRemoteServer(_window, port);
        bool ok = _server.Start();

        Active = true;
        DebugLog.Event("调试模式已就绪", new
        {
            endpoint = Endpoint,
            portOk = ok,
            error = ok ? null : _server.LastError,
        });

        return ok;
    }

    /// <summary>关掉调试模式（日志不关 —— 关掉之后就看不到"什么时候关的"了）。</summary>
    public void Stop()
    {
        if (!Active) return;

        _watch?.Dispose();
        _watch = null;

        _server?.Stop();
        _server = null;

        Active = false;
        DebugLog.Disable();
    }

    /// <summary>设置里改了端口之后，原地重启遥控口。</summary>
    public bool Restart(int port, bool logToFile)
    {
        Stop();
        return Start(port, logToFile);
    }

    // ========================================================
    // 全局事件钩子
    // ========================================================

    private void HookEvents()
    {
        if (_hooked) return;
        _hooked = true;

        UiInspector.HookMenus();

        // ① 所有按钮（含 ToggleButton / RadioButton / CheckBox）的点击
        //    最后一个参数 handledEventsToo: true —— 就算某个页面把事件标记成"已处理"，
        //    我们仍然要看得到它（否则"点了没反应"的按钮反而记不下来）。
        _window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyClick), true);

        // ② 输入框改动
        _window.AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnAnyText), true);

        // ③ 下拉 / 列表的选择
        _window.AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(OnAnySelect), true);

        DebugLog.Event("已挂上界面事件钩子（按钮 / 输入 / 选择）");
    }

    private void OnAnyClick(object sender, RoutedEventArgs e)
    {
        if (Source(e, sender) is not FrameworkElement fe) return;

        DebugLog.Write("click", Describe(fe), new
        {
            id = IdOf(fe),
            type = fe.GetType().Name,
            page = _window.CurrentNavId,
        });
    }

    private void OnAnyText(object sender, RoutedEventArgs e)
    {
        if (Source(e, sender) is not TextBoxBase tb) return;

        string text = tb is TextBox t ? t.Text : "";
        string key = IdOf(tb) + "\u0001" + text;
        if (key == _lastTextKey) return;   // 连续两次一样（比如先设 Text 再挪光标）不重复记
        _lastTextKey = key;

        DebugLog.Write("text", Describe(tb), new
        {
            id = IdOf(tb),
            length = text.Length,
            value = text.Length > 120 ? text[..120] + "…" : text,
        });
    }

    private void OnAnySelect(object sender, RoutedEventArgs e)
    {
        if (Source(e, sender) is not Selector sel) return;

        DebugLog.Write("select", Describe(sel), new
        {
            id = IdOf(sel),
            type = sel.GetType().Name,
            index = sel.SelectedIndex,
            value = sel.SelectedItem?.ToString(),
        });
    }

    /// <summary>
    /// 找出"到底是谁"触发了这次事件。
    ///
    /// 【为什么不能直接用 sender】
    ///   我们把钩子挂在**窗口**上，所以 WPF 传给我们的 sender 永远是窗口本身 ——
    ///   照它记出来的日志全是"WorkbenchWindow 被点了"，一点用都没有。
    ///   真正按下的是 <c>e.OriginalSource</c>；它有时是控件内部的零件
    ///   （比如按钮里的那个 TextBlock），所以再沿着可视树往上找到第一个"像控件的东西"。
    /// </summary>
    private static FrameworkElement? Source(RoutedEventArgs e, object sender)
    {
        object? o = e.OriginalSource ?? sender;

        for (var d = o as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement fe &&
                (fe is ButtonBase || fe is TextBoxBase || fe is Selector ||
                 fe is MenuItem || fe is Slider || fe is TabItem))
                return fe;

            // 走到窗口就停（再往上没有意义了）
            if (d is Window) break;
        }

        return sender as FrameworkElement;
    }

    // ========================================================
    // 文件监听
    // ========================================================

    private void StartWatch()
    {
        _watch = new DebugFileWatch();

        _watch.Watch(
            Project.ProjectStore.RootDir,        // 全部项目（含打开中的那个）
            AppConfig.DataDir);                  // config.json / inspiration.json
    }

    // ========================================================
    // 小工具
    // ========================================================

    private static string IdOf(FrameworkElement fe)
    {
        try
        {
            string aid = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrWhiteSpace(aid)) return aid;
        }
        catch { }
        return fe.Name ?? "";
    }

    private static string Describe(FrameworkElement fe)
    {
        string id = IdOf(fe);
        string label = fe switch
        {
            ButtonBase b => b.Content?.ToString() ?? "",
            TextBox t => t.Text is { Length: > 40 } s ? s[..40] + "…" : t.Text ?? "",
            ComboBox c => c.SelectedItem?.ToString() ?? "",
            ListBox l => l.SelectedItem?.ToString() ?? "",
            _ => "",
        };

        label = label.Replace("\r", " ").Replace("\n", " ").Trim();

        string type = fe.GetType().Name;
        if (id.Length > 0 && label.Length > 0) return $"{type}「{label}」#{id}";
        if (label.Length > 0) return $"{type}「{label}」";
        if (id.Length > 0) return $"{type}#{id}";
        return type;
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _server?.Stop();
    }
}
