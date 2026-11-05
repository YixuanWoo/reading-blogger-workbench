// ============================================================
// App.xaml.cs - 应用程序启动代码
// ============================================================
// WPF 程序的启动顺序：Main()（编译器自动生成）→ App → 主窗口。
//
// 这里除了启动，还挂了三件额外的事：
//   ① 单实例锁     —— 已经有工作台在跑，就把它叫到前面来，自己退出
//   ② 全局异常捕获 —— 界面上没弹出来的错误，一律记到日志文件
//   ③ 启动兜底     —— 主窗口万一构造失败，弹个能看懂的提示而不是静默退出
//
// 【为什么①很重要】
//   工作台的配置(config.json)和项目存档(project.json)都是"整份读、整份写"。
//   两个实例同时开着同一个项目，各自每 20 秒自动保存一次 ——
//   后写的会把先写的整个盖掉，等于悄悄丢活。
//   而且每个实例都占一份音频引擎和上百 MB 内存，用户一旦以为卡住、
//   反复双击图标，就会叠出一堆实例互相抢磁盘，越等越慢。
//   桌面工具本来就该单开，这里直接锁死。
//
// 【为什么②很重要】
//   WPF 的绑定错误、样式查找失败这类问题，很多时候界面还能继续显示，
//   但某个按钮已经不能用了 —— 用户只会觉得"点了没反应"。
//   没有日志就只能靠猜。你要实测，日志是第一手证据。
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiteCut.Workbench.Agent;
using LiteCut.Workbench.Theming;

namespace LiteCut;

/// <summary>
/// 应用程序入口类。
/// </summary>
public partial class App : Application
{
    // ============================================================
    // 启动计时锚点
    // ============================================================

    /// <summary>
    /// OnStartup 被调用的那一刻（毫秒，取自系统开机计时）。
    /// 任何页面都能用它算出"从进程启动到现在过了多久"，用来给启动速度打点。
    /// </summary>
    public static long StartupTick { get; private set; } = Environment.TickCount64;

    // ============================================================
    // 本次运行的渲染模式（V1.19.0）
    // ============================================================

    /// <summary>
    /// 本次运行是不是"兼容渲染"（软件渲染、不占显卡）。
    /// ★ 为什么要留这个只读标记：设置页要显示"当前实际生效的是哪种" ——
    ///   配置里写了 true、但这次没重启，实际跑的还是硬件加速，
    ///   用户看不到这个区别就会以为"开了没用"。所以只认启动时真的设上的结果。
    /// </summary>
    public static bool SoftwareRenderActive { get; private set; }

    // ============================================================
    // 单实例锁
    // ============================================================

    /// <summary>
    /// 单实例锁。静态字段持有 —— 被 GC 回收的话锁就没了。
    /// 进程退出时系统会自动释放，所以崩溃也不会留下"幽灵锁"。
    /// </summary>
    private static Mutex? _instanceLock;

    /// <summary>日志文件路径。出问题让它来背锅。</summary>
    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "翊瑄的工作台", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTick = Environment.TickCount64;

        // ---- 兼容渲染：把工作台从显卡那条链上摘下来（V1.19.0）----
        // ★ 位置极其要紧：必须在**任何窗口 / 视觉对象创建之前**设。
        //   WPF 一旦开始出画面，渲染模式就锁死了，再设只会在调试输出里
        //   留一句"ProcessRenderMode 已被忽略"，界面纹丝不动 —— 白折腾。
        //   所以放在 OnStartup 的第一件事，比单实例检查还早（那个分支只是退出，
        //   设了也不影响）。
        // ★ 读配置失败就当没开：这是排障开关，绝不能因为一个 json 读不出来
        //   把工作台挡在门外。
        try
        {
            if (Workbench.AppConfig.Load().SoftwareRender)
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode =
                    System.Windows.Interop.RenderMode.SoftwareOnly;
                SoftwareRenderActive = true;
            }
        }
        catch (Exception ex)
        {
            WriteLog("兼容渲染开关读取失败（已按硬件加速启动）", ex);
        }

        // ---- 单实例：已经有工作台在跑 → 把那个叫到最前面，自己安静退出 ----
        // 例外：设了 LITECUT_ALLOW_MULTI_INSTANCE=1 时放行 —— 给无头验证工程用，
        // 不然一边开着工作台一边跑端到端验证，验证进程会被单实例锁掐死。
        if (Environment.GetEnvironmentVariable("LITECUT_ALLOW_MULTI_INSTANCE") != "1"
            && !ClaimSingleInstance())
        {
            BringRunningInstanceToFront();
            Shutdown();
            return;
        }

        // ---- 挂全局异常处理 ----
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteLog("致命异常", args.ExceptionObject as Exception);

        // DispatcherUnhandledException：UI 线程上抛出来却没人接的异常
        DispatcherUnhandledException += (_, args) =>
        {
            WriteLog("界面异常", args.Exception);
            args.Handled = true;   // 先记下来，不让程序直接崩掉
        };

        try
        {
            base.OnStartup(e);

            // ---- 先把主题铺上，再建窗口 ----
            // ★ 顺序很要紧：WorkbenchWindow 构造时 XAML 就在解析 {StaticResource}，
            //   那时拿到的笔已经是主题色了，不会先亮一下深色再刷成白色。
            //   读的是同一份 config.json（跟 WorkbenchState.Config 同一个文件，
            //   只是这里自己 Load 一次：State 是跟着窗口一起建的，等不了）。
            try
            {
                var cfg = Workbench.AppConfig.Load();
                // SavedId = 用户存过的偏好（设置里"取消"时用它还原）；
                // 真正把颜色铺上去的是 Apply，它同时把 CurrentId 记下来。
                Theme.SavedId = cfg.ThemeId ?? Theme.NightId;
                Theme.Apply(cfg.ThemeId);
            }
            catch (Exception ex)
            {
                // 主题读不出来最多是"配色不对"，绝不能挡住启动
                WriteLog("主题应用失败（已退回夜空黑）", ex);
            }

            // 主窗口在这里手动创建（App.xaml 里故意没写 StartupUri）：
            // 走 StartupUri 的话，窗口由框架在 OnStartup 返回后才建，
            // 上面那个"已经有实例在跑"的分支就拦不住 —— 会闪一个空窗口再消失。
            MainWindow = new Workbench.WorkbenchWindow();

            // 标题栏图标。ApplicationIcon（csproj 里那条）只管 exe 文件的图标
            // —— 资源管理器、任务栏、Alt+Tab 看的是它；但 WPF 窗口的标题栏不吃那套，
            // 必须在这里显式赋值（否则左上角是空的）。
            // 读的是打包进程序集的那份（csproj 里的 Resource 项），
            // 而不是到磁盘上去找文件 —— 所以单文件 exe 运行时也不怕路径变了。
            //
            // 【为什么套一层 try】
            //   这行跑在启动路径上，而 pack URI 取不到资源是会抛异常的。
            //   图标终究是锦上添花，一枚图没找到就把整个工作台挡在门外，
            //   赔不起 —— 找不到就光着标题栏继续跑，别拦着人干活。
            try
            {
                MainWindow.Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico"));
            }
            catch (Exception ex)
            {
                WriteLog("图标加载失败（不影响使用）", ex);
            }

            MainWindow.Show();

            // 标题栏染成和界面一致的底色 —— 系统默认那条亮色标题栏骑在上面非常突兀
            // （深色主题那会儿用户就点名要改）。V1.18.0 起跟着主题走：
            // 白天主题给浅色标题栏，否则一块深色边框扣在白界面上更难看。
            // 必须在 Show() 之后：那时窗口句柄才存在。
            ApplyTitleBarTheme(MainWindow);

            // 任务一：拉起 Agent 桥接（若没在跑），**并开看护心跳**。
            //
            // ★ 2026-10-03：原来只调一次 EnsureBridgeLaunched 就完事 ——
            //   桥接进程会随 WorkBuddy 宿主一起没（切会话 / 宿主崩 / 电脑休眠），
            //   而工作台自己一直开着就**再也不会拉第二次**。
            //   表现 = 隔一夜 / 重开过电脑后，点「检测 Agent」永远报连接被拒。
            //   现在：启动时先拉一次，之后由看护每 10 秒探一次，掉了就重拉。
            //   → 达成"无论何时都能连通"（前提是 WorkBuddy 本身开着）。
            _ = Task.Run(() =>
            {
                AgentBridge.EnsureBridgeLaunched();
                AgentBridge.StartBridgeWatchdog();
            });
        }
        catch (Exception ex)
        {
            WriteLog("启动失败", ex);
            MessageBox.Show(
                "工作台启动时出问题了：" + Environment.NewLine + Environment.NewLine +
                ex.Message + Environment.NewLine + Environment.NewLine +
                "详细记录在：" + LogPath,
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// 退出时收尾。
    /// ★ 桥接看护是个**静态 Timer** —— 不显式停掉的话它会让进程吊着不退出
    ///   （Timer 有非空回调就不参与 CLR 的线程池终止判定）。
    ///   这属于「加了后台任务就必须配退出钩子」的通用规矩。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { AgentBridge.StopBridgeWatchdog(); } catch { }
        base.OnExit(e);
    }

    // ============================================================
    // 单实例的实现
    // ============================================================

    /// <summary>
    /// 抢单实例锁。抢到返回 true；已经有别人拿着返回 false。
    /// </summary>
    private static bool ClaimSingleInstance()
    {
        try
        {
            // Local\ = 只对当前登录会话生效（同一台电脑换用户登录互不影响）
            _instanceLock = new Mutex(initiallyOwned: true,
                                      @"Local\LiteCut_Workbench_SingleInstance_A7F3",
                                      out bool isFirst);
            return isFirst;
        }
        catch
        {
            // 连锁都建不起来（极罕见）→ 放行，宁可多开也不要打不开
            return true;
        }
    }

    /// <summary>
    /// 把已经在跑的那个工作台窗口叫到前台。
    /// 找不到窗口就算了 —— 不弹框骚扰（用户再点一次图标通常就命中了）。
    /// </summary>
    private static void BringRunningInstanceToFront()
    {
        int me = Environment.ProcessId;

        // 头一个实例可能还在启动中（窗口还没建好），给它几秒
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("LiteCut"))
                {
                    using (p)
                    {
                        if (p.Id == me) continue;

                        IntPtr h = p.MainWindowHandle;
                        if (h == IntPtr.Zero) continue;

                        ShowWindowAsync(h, SW_RESTORE);   // 最小化了就还原
                        SetForegroundWindow(h);
                        return;
                    }
                }
            }
            catch
            {
                return;
            }

            Thread.Sleep(300);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    // ============================================================
    // 标题栏深色化（V1.9.10）
    // ============================================================
    // 界面整体是深色，但 Windows 的标题栏默认是系统配色 —— 用户看到的是
    // 一条突兀的亮条骑在深色窗口头上。DWM 提供了两个属性可以改它：
    //   DWMWA_USE_IMMERSIVE_DARK_MODE —— 深色标题栏（Win10 1809+，最稳的兜底）
    //   DWMWA_CAPTION_COLOR           —— 直接指定标题栏颜色（Win11 22000+ 才有）
    // 先试直接指定颜色（跟窗口底色完全一致），系统不支持再退到"深色模式"；
    // 都不支持就算了 —— 亮条难看，但不值得为它崩掉启动。
    // ============================================================

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>标题栏用深色模式（Win10 1809+）</summary>
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    /// <summary>标题栏直接指定颜色（Win11 22000+）</summary>
    private const int DWMWA_CAPTION_COLOR = 35;
    /// <summary>窗口边框颜色（Win11）</summary>
    private const int DWMWA_BORDER_COLOR = 34;
    /// <summary>标题栏文字颜色（Win11 22000+）</summary>
    private const int DWMWA_TEXT_COLOR = 36;

    /// <summary>
    /// 把窗口标题栏染成当前主题的底色（深色主题 = 深色标题栏，白天主题 = 浅色）。
    /// 在窗口句柄就绪之后调用（Show() 之后即安全）。
    /// ★ V1.18.0 起改为 public：设置里换主题之后要**立刻**把标题栏也换掉，
    ///   不然窗口内容已经白了、头上还扣着一条黑的。
    /// </summary>
    public static void ApplyTitleBarTheme(System.Windows.Window window)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            // 底色取当前主题的「窗口最底」（BgDeep）
            Color c = Theme.ColorOf("BgDeep");

            // COLORREF 的字节序是 0x00BBGGRR —— 和网页的 #RRGGBB 正好相反，别写顺了
            int caption = (c.R) | (c.G << 8) | (c.B << 16);

            // 亮底要用亮标题栏（immersive dark mode = 0），暗底用 1
            double luma = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            int on = luma < 0.5 ? 1 : 0;

            // 先按 Win11 的路子直接染色；失败（Win10）就退回"深色/浅色标题栏"开关
            // 两句都调也无害：系统不认某个属性时它只返回错误码，不会崩。
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, 4);

            // 边框一并染掉，否则 Win11 会给窗口描一圈亮边，还是扎眼
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref caption, 4);

            // 标题文字颜色（Win11 22000+）。不设的话系统按"深色/浅色"自己猜，
            // 在浅色标题栏上偶尔会给出浅色字 —— 那就看不见了。
            if (handle != IntPtr.Zero)
            {
                int text = luma < 0.5 ? 0x00EFE9E6 : 0x00201A16;  // 注意 BGR 序
                DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref text, 4);
            }
        }
        catch
        {
            // 标题栏染色是纯观感优化，任何环境下失败都不值得打断启动
        }
    }

    // ============================================================
    // 异常日志
    // ============================================================

    /// <summary>
    /// 把异常追加写进日志。保留历史，方便对照时间线。
    /// </summary>
    private static void WriteLog(string title, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}{Environment.NewLine}" +
                $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                $"{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 连日志都写不了就真没办法了
        }
    }
}
