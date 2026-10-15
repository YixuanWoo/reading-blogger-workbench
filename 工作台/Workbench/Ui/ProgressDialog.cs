// ============================================================
// ProgressDialog.cs - ★ V1.13.1「搬运中」的锁窗进度条
// ============================================================
//
// 【为什么要有这个东西】
//   归档是"真搬文件"：几百 MB 的复制 + 逐个校验，要跑几秒到几十秒。
//   这期间如果界面还能点，手快两下就可能撞上正在搬的工程 —— 那已经不是
//   程序自己能兜住的破坏了。所以要求就两条：
//     ① 进度看得见（卡住了也能看出来）
//     ② 别的什么都点不动（防手滑）
//
// 【"点不动别的"是怎么做到的】
//   用 ShowDialog()。WPF 里 ShowDialog 是**模态**：它把属主窗口（工作台
//   主窗）的输入整个吃掉 —— 主窗上所有按钮、左侧导航、连标题栏的关闭
//   按钮统统失效，用户想点也点不到。
//   这比"逐个按钮去 IsEnabled=false"彻底得多：后者总会漏，哪天新加一个
//   按钮忘了禁就出事了。
//
// 【模态会阻塞，为什么反而正好】
//   ShowDialog 会**阻塞调用它的那一行**（它在当前线程跑一个嵌套消息循环）。
//   这恰好能干活：
//     · 后台 Task 的进度回调里 Dispatcher.BeginInvoke → 投递到 UI 线程
//     · ShowDialog 的嵌套消息循环会照常处理这些投递 → 进度真的在动
//     · 后台干完 → BeginInvoke 里调 Finish() → Close() → ShowDialog 返回
//   所以调用点的顺序必须是「先把后台任务起起来，再 ShowDialog」（见 Run()），
//   反过来的话所有进度都会砸在窗口显示之前，用户看不到过程。
//
// 【为什么进度条自己画】
//   WPF 自带的 ProgressBar 走系统配色（浅色主题下是亮绿），和这套深色界面
//   放一起很突兀；而且我要顺手加"卡住预警"，用自己画的更顺手。
//   自绘用两列 Grid：左列宽度 = 百分比（star 权重），右列 = 剩下的。
//   star 权重天然按像素比例分配，不用去算控件实际宽度，也不怕窗口缩放。
//
// 【为什么是"安全中止"而不是"取消"】
//   归档的顺序是死的：先复制 → 校验 → 才删本地。取消点只在复制/校验阶段，
//   那时**本地一个文件都没删**，停下最多是硬盘上留半份拷贝（下次覆盖，无害）。
//   一旦进到"清理本地"，ArchiveService 就不再响应取消了 —— 删到一半停下
//   才是最糟的结果。按钮上的字和二次确认都把这层意思说清楚。
// ============================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench.Ui;

/// <summary>
/// 模态进度窗：显示"第几步 / 这一步多少 / 总百分比 / 已用多久"，
/// 并提供「安全中止」。要点是它**锁死主窗口**（模态）且**关不掉**。
/// </summary>
public sealed class ProgressDialog : Window
{
    // ---- 配色：跟 App.xaml 的深色主题保持一致。这里写死 hex 而不是走
    //      {StaticResource}，是因为纯代码建窗时资源查找要靠 FindResource，
    //      万一资源 key 改了这里会直接崩；写死则最多是颜色不跟主题变。 ----
    private static readonly Brush BdDeep = New("#FF0E1013");   // 窗体最深底
    private static readonly Brush BdCard = New("#FF1A1E24");   // 面板底
    private static readonly Brush BdInput = New("#FF232830");  // 输入/轨道底
    private static readonly Brush BrBorder = New("#FF2A3038"); // 描边
    private static readonly Brush BrMain = New("#FFE6E9EF");   // 主文字
    private static readonly Brush BrDim = New("#FF8B93A1");    // 次要文字
    private static readonly Brush BrFaint = New("#FF78828F");  // 更淡的文字
    private static readonly Brush BrAccent = New("#FF3B82F6");
    private static readonly Brush BrWarn = New("#FFF59E0B");
    private static readonly Brush BrOk = New("#FF22C55E");

    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;

    private readonly ColumnDefinition _colFill = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _colRest = new() { Width = new GridLength(100, GridUnitType.Star) };

    private readonly TextBlock _txtStage = new();
    private readonly TextBlock _txtPct = new();
    private readonly TextBlock _txtDetail = new();
    private readonly TextBlock _txtTime = new();
    private readonly TextBlock _txtWarn = new();
    private readonly Button _btnCancel;

    private readonly string _cancelHint;

    /// <summary>最近一次收到进度报告的时刻（Environment.TickCount64）。跨线程读写。</summary>
    private long _lastReportTick;

    /// <summary>最近一次真正刷新界面的时刻 —— 用来给高频回调节流。</summary>
    private long _lastPaintTick;

    /// <summary>用户已经按了「安全中止」，正在等它停。</summary>
    private volatile bool _stopping;

    /// <summary>还在跑（false = 已进入收尾，别再把画面刷回"进行中"）。</summary>
    private volatile bool _running = true;

    /// <summary>真正的关闭闸门：只有 Finish() 走完才允许 Close()。</summary>
    private bool _allowClose;

    /// <summary>交给后台任务的取消令牌。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>界面上显示多少秒没进展就提示"可能卡住了"。</summary>
    private const int StallWarnSeconds = 15;

    private ProgressDialog(string title, string subtitle, string cancelHint, string cancelText)
    {
        _cancelHint = cancelHint;

        Title = title;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei, Segoe UI");
        UseLayoutRounding = true;

        // ------------------ 版面 ------------------
        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrMain,
        });

        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = BrDim,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = BrBorder,
            Margin = new Thickness(0, 16, 0, 0),
        });

        // --- 阶段 + 百分比 ---
        var headRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _txtStage.Text = "准备中…";
        _txtStage.FontSize = 13.5;
        _txtStage.Foreground = BrMain;
        _txtStage.VerticalAlignment = VerticalAlignment.Center;
        _txtStage.TextTrimming = TextTrimming.CharacterEllipsis;

        _txtPct.Text = "0%";
        _txtPct.FontSize = 15;
        _txtPct.FontWeight = FontWeights.SemiBold;
        _txtPct.Foreground = BrAccent;
        _txtPct.VerticalAlignment = VerticalAlignment.Center;
        _txtPct.Margin = new Thickness(12, 0, 0, 0);
        _txtPct.FontFamily = new FontFamily("Consolas, Microsoft YaHei");

        Grid.SetColumn(_txtStage, 0);
        Grid.SetColumn(_txtPct, 1);
        headRow.Children.Add(_txtStage);
        headRow.Children.Add(_txtPct);
        stack.Children.Add(headRow);

        // --- 进度条（两列 star 权重；左=已走，右=剩下） ---
        var track = new Grid { Height = 12, Margin = new Thickness(0, 10, 0, 0) };
        track.ColumnDefinitions.Add(_colFill);
        track.ColumnDefinitions.Add(_colRest);

        var fill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#FF2563EB")!,
                (Color)ColorConverter.ConvertFromString("#FF3B82F6")!,
                new Point(0, 0), new Point(1, 0)),
        };
        Grid.SetColumn(fill, 0);
        track.Children.Add(fill);

        // 外框单独套一层（圆角 + 深底 + 描边），内层填充铺在它上面
        var trackBorder = new Border
        {
            Background = BdInput,
            BorderBrush = BrBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = track,
            Padding = new Thickness(1),
            Height = 14,
        };
        stack.Children.Add(trackBorder);

        // --- 明细（正在处理哪个文件） ---
        _txtDetail.Text = "";
        _txtDetail.FontSize = 12;
        _txtDetail.Foreground = BrDim;
        _txtDetail.Margin = new Thickness(0, 9, 0, 0);
        _txtDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(_txtDetail);

        // --- 计时 / 卡住预警 ---
        _txtTime.Text = "";
        _txtTime.FontSize = 11.5;
        _txtTime.Foreground = BrFaint;
        _txtTime.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(_txtTime);

        _txtWarn.Text = "";
        _txtWarn.FontSize = 12;
        _txtWarn.Foreground = BrWarn;
        _txtWarn.Margin = new Thickness(0, 7, 0, 0);
        _txtWarn.TextWrapping = TextWrapping.Wrap;
        _txtWarn.Visibility = Visibility.Collapsed;
        stack.Children.Add(_txtWarn);

        // --- 底部按钮 ---
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        _btnCancel = new Button
        {
            Content = cancelText,
            MinWidth = 104,
            Padding = new Thickness(14, 7, 14, 7),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = BrDim,
            Background = BdInput,
            BorderBrush = BrBorder,
            BorderThickness = new Thickness(1),
        };
        _btnCancel.Click += BtnCancel_Click;
        btnRow.Children.Add(_btnCancel);
        stack.Children.Add(btnRow);

        // ------------------ 外壳（圆角 + 描边 + 投影，做出"浮在主窗上面"的感觉） ------------------
        var shell = new Border
        {
            Background = BdCard,
            BorderBrush = BrBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(16),   // 给投影留出画的地方
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 3,
                Opacity = 0.55,
                Color = Colors.Black,
            },
        };

        Content = shell;

        // ------------------ 计时器：400ms 刷一次"已用 / 多久没动" ------------------
        // 存在的意义：万一真的卡住了（硬盘拔了、盘坏了），用户能从
        // "最后更新 32 秒前"这行字看出端倪，而不是面对一个死掉的窗口。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (s, e) => TickClock();
        _timer.Start();

        Loaded += (s, e) => Activate();
    }

    // ========================================================
    // 给后台任务调的两个入口
    // ========================================================

    /// <summary>
    /// 收一条进度（**从后台线程调用**）。内部自己做节流 + 切回 UI 线程。
    /// 节流是因为归档几百个文件会调几百次，全投过去会把消息队列堵住、反而卡。
    /// </summary>
    public void Report(ArchiveProgress p)
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref _lastReportTick, now);

        // 60ms 内只刷一次：进度条看着够顺，又不会把 UI 线程压垮。
        // 最后一个报告被节流掉也没关系 —— Finish() 会把进度强行置到 100%。
        if (now - Interlocked.Read(ref _lastPaintTick) < 60) return;
        Interlocked.Exchange(ref _lastPaintTick, now);

        Dispatcher.BeginInvoke(new Action(() => Paint(p)), DispatcherPriority.Background);
    }

    /// <summary>后台任务结束（成功/失败/中止都会走到这）→ 收尾并关闭窗口。</summary>
    public void Finish()
    {
        _running = false;
        _timer.Stop();

        SetPercent(100, BrOk);
        _txtDetail.Text = "";
        _txtWarn.Visibility = Visibility.Collapsed;
        _txtTime.Text = $"用时 {_sw.Elapsed.TotalSeconds:F1} 秒";
        _btnCancel.IsEnabled = false;
        _btnCancel.Visibility = Visibility.Collapsed;

        // 太快的话窗口多停一下再关 —— 否则一闪而过，用户根本来不及看
        // （小项目归档实测只要 5 秒，其中复制可能就 1 秒多）
        double ms = _sw.Elapsed.TotalMilliseconds;
        int wait = ms < 1200 ? 900 : 350;

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
        t.Tick += (s, e) =>
        {
            t.Stop();
            _allowClose = true;
            Close();
        };
        t.Start();
    }

    // ========================================================
    // 画面刷新
    // ========================================================

    private void Paint(ArchiveProgress p)
    {
        if (!_running) return;   // 已收尾，别刷回"进行中"

        if (!_stopping)
        {
            _txtStage.Text = p.Headline;
            _txtStage.Foreground = BrMain;
            SetPercent(p.Percent, BrAccent);
        }

        _txtDetail.Text = p.Sub;
    }

    private void SetPercent(double percent, Brush color)
    {
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        // 两列 star 权重按比例分像素：100 和剩下的部分
        _colFill.Width = new GridLength(percent, GridUnitType.Star);
        _colRest.Width = new GridLength(100 - percent, GridUnitType.Star);

        _txtPct.Text = $"{percent:F0}%";
        _txtPct.Foreground = color;
    }

    /// <summary>每 400ms 走一次：刷"已用 / 最后更新多久前"，太久没动就报警。</summary>
    private void TickClock()
    {
        if (!_running) return;

        double used = _sw.Elapsed.TotalSeconds;
        long idleMs = Environment.TickCount64 - Volatile.Read(ref _lastReportTick);
        double idle = idleMs / 1000.0;

        _txtTime.Text = $"已用 {used:F0} 秒　·　最后一条进展在 {idle:F0} 秒前";

        if (_stopping)
        {
            _txtWarn.Text = "正在安全停下 —— 正在等当前这个文件处理完，本地一个文件都不会删。";
            _txtWarn.Foreground = BrWarn;
            _txtWarn.Visibility = Visibility.Visible;
            return;
        }

        if (idle >= StallWarnSeconds)
        {
            _txtWarn.Text = $"⚠ 已经 {idle:F0} 秒没有新进展了。可能是硬盘在忙、或者卡住了。\n" +
                            $"　再等等；要放弃可以点下面的「{_btnCancel.Content}」。";
            _txtWarn.Foreground = BrWarn;
            _txtWarn.Visibility = Visibility.Visible;
        }
        else
        {
            _txtWarn.Visibility = Visibility.Collapsed;
        }
    }

    // ========================================================
    // 安全中止 / 关闭闸门
    // ========================================================

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;

        var ok = MessageBox.Show(this,
            "确定要中止吗？\n\n" +
            _cancelHint + "\n\n" +
            "（正在处理的那个文件会先做完，不会留下半个文件。）",
            "安全中止", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (ok != MessageBoxResult.OK) return;

        _stopping = true;
        _cts.Cancel();

        _btnCancel.IsEnabled = false;
        _btnCancel.Content = "正在停下…";
        _txtStage.Text = "正在安全停下…";
        _txtStage.Foreground = BrWarn;
        TickClock();
    }

    /// <summary>
    /// ★ 关闭闸门：干活期间任何人（包括 Alt+F4、任务栏右键关闭）都关不掉它。
    ///   只有 Finish() 走完把 _allowClose 打开，才放行。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    // ========================================================
    // 给调用点用的"一键跑"
    // ========================================================

    /// <summary>
    /// 弹进度窗跑一件后台活，窗口是**模态**的（主窗全锁）。
    /// <para>
    /// work 会拿到两个东西：报告进度的回调（从后台线程调，直接丢给它就行）、
    /// 取消令牌（用户点了「安全中止」就变红）。
    /// </para>
    /// </summary>
    public static ArchiveResult Run(
        Window? owner,
        string title,
        string subtitle,
        string cancelHint,
        Func<Action<ArchiveProgress>, CancellationToken, ArchiveResult> work,
        string cancelText = "安全中止")
    {
        var dlg = new ProgressDialog(title, subtitle, cancelHint, cancelText);

        if (owner != null)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Task<ArchiveResult>? task = null;

        dlg.Loaded += (s, e) =>
        {
            // ★ 顺序：先把后台任务起起来，ShowDialog 再进消息循环。
            //   这样第一条进度不会早于窗口显示。
            //   注意 Loaded 是在 ShowDialog 内部、UI 线程上跑的，
            //   所以这里赋值 task、以及下面读 task，都在同一个线程，不存在可见性问题。
            task = Task.Run<ArchiveResult>(() =>
            {
                try
                {
                    return work(dlg.Report, dlg.Token);
                }
                catch (Exception ex)
                {
                    return new ArchiveResult { Ok = false, Message = "出错了：\n\n" + ex.Message };
                }
            });

            // 干完（成功/失败/中止都算）→ 通知窗口收尾。用 ContinueWith 而不是在
            // 任务体里收尾，是为了不占用后台线程去发 UI 消息。
            _ = task.ContinueWith(
                t => dlg.Dispatcher.BeginInvoke(new Action(dlg.Finish)),
                TaskScheduler.Default);
        };

        // 模态：主窗（含所有按钮、导航、标题栏）全部失效，用户点不到。
        // 它会阻塞在这里，直到 Finish() 里 Close()。
        dlg.ShowDialog();

        // 走到这里说明窗口已经关了、任务也一定完成了，所以这句不会真的等
        return task != null
            ? task.GetAwaiter().GetResult()
            : new ArchiveResult { Ok = false, Message = "操作没有完成。" };
    }

    private static Brush New(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
}
