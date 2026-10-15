// ============================================================
// SettingsWindow.xaml.cs - ★ V1.14.0 设置窗口
// ============================================================
//
// 【一条硬规矩：点了「保存设置」才写盘】
//   窗口里所有输入框都是"先读出来、改了再写回"。点取消 = 一个字段都不动。
//   设置项一旦即时生效，用户改错了想反悔就没机会了 —— 设置不该是这种手感。
//
// 【为什么直接改 AppConfig 实例就行】
//   传进来的 _cfg 就是运行中的那一份（WorkbenchWindow.State.Config），
//   所以改完之后外壳马上就能看到新值；Save() 只是把它落到 config.json。
// ============================================================

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiteCut.Core.Media;
using LiteCut.Workbench.Agent;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _cfg;

    /// <summary>控件树建完了没有 —— 建到一半时 @Checked 会提前触发。</summary>
    private bool _uiReady;

    /// <summary>点过「保存设置」没有（调用方据此决定要不要刷新界面）。</summary>
    public bool Saved { get; private set; }

    public SettingsWindow(AppConfig cfg)
    {
        _cfg = cfg;

        InitializeComponent();
        LoadFromConfig();

        _uiReady = true;
        RefreshDebugStatus();

        TxtUrl.Focus();
    }

    // ========================================================
    // 读配置 → 铺界面
    // ========================================================

    private void LoadFromConfig()
    {
        // ---- 大模型 ----
        TxtUrl.Text = _cfg.AgentBaseUrl ?? "";
        TxtKey.Password = _cfg.AgentApiKey ?? "";
        TxtModel.Text = _cfg.AgentModel ?? "";
        ChkStream.IsChecked = _cfg.AgentStream;

        // ---- 路径 ----
        TxtProjectRoot.Text = _cfg.ProjectRoot ?? "";
        TxtSeed.Text = _cfg.SeedDir ?? "";
        TxtFfmpeg.Text = _cfg.FFmpegPath ?? "";
        TxtRenderRoot.Text = _cfg.RenderAssetRoot ?? "";
        TxtTemplateDir.Text = _cfg.HyperFramesTemplateDir ?? "";
        TxtArchiveRoot.Text = _cfg.ArchiveRoot ?? "";
        TxtTranscript.Text = _cfg.TranscriptDir ?? "";

        RefreshPathHints();

        // ---- 调试 ----
        ChkDebug.IsChecked = _cfg.DebugEnabled;
        ChkDebugFile.IsChecked = _cfg.DebugLogToFile;
        TxtDebugPort.Text = _cfg.DebugPort.ToString();
    }

    /// <summary>把"这一项实际会用哪"标在输入框旁边（留空时尤其重要）。</summary>
    private void RefreshPathHints()
    {
        try
        {
            TxtProjectRootNow.Text = "实际：" + ProjectStore.RootDir;
        }
        catch { TxtProjectRootNow.Text = ""; }

        try
        {
            string ff = FFmpegLocator.FFmpegPath ?? "";
            TxtFfmpegNow.Text = ff.Length > 0 ? "已找到：" + ff : "还没找到 ffmpeg";
        }
        catch { TxtFfmpegNow.Text = ""; }
    }

    // ========================================================
    // 分类切换
    // ========================================================

    private void Cat_Checked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;    // 控件树还没建完 → 别动 Visibility

        string tag = (sender as FrameworkElement)?.Tag as string ?? "llm";

        PageLlm.Visibility = tag == "llm" ? Visibility.Visible : Visibility.Collapsed;
        PagePaths.Visibility = tag == "paths" ? Visibility.Visible : Visibility.Collapsed;
        PageDebug.Visibility = tag == "debug" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "debug") RefreshDebugStatus();
    }

    // ========================================================
    // 大模型：检测连接
    // ========================================================

    private async void BtnTestLlm_Click(object sender, RoutedEventArgs e)
    {
        // 用界面上还没保存的值测 —— 填完就能测，不用先保存
        var probe = new AppConfig
        {
            AgentBaseUrl = TxtUrl.Text.Trim(),
            AgentApiKey = TxtKey.Password.Trim(),
            AgentModel = TxtModel.Text.Trim(),
            AgentStream = false,        // 检测走非流式，最简单直接
        };

        BtnTestLlm.IsEnabled = false;
        TxtLlmResult.Text = "检测中…（真的发了一次最小请求）";
        TxtLlmResult.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");

        try
        {
            var r = await AgentBridge.TestAsync(probe);

            if (r.Ok)
            {
                TxtLlmResult.Text = "✔ 已就绪 —— " + r.Detail + "（模型回音：" + Shorten(r.Content, 20) + "）";
                TxtLlmResult.Foreground = (System.Windows.Media.Brush)FindResource("Ok");
            }
            else
            {
                TxtLlmResult.Text = "✘ 没通：" + r.Error;
                TxtLlmResult.Foreground = (System.Windows.Media.Brush)FindResource("Warn");
            }
        }
        finally
        {
            BtnTestLlm.IsEnabled = true;
        }
    }

    /// <summary>
    /// 截断一段文字用于显示。
    /// ★ 名字别叫 Clip —— UIElement 上已经有一个 Clip 属性，同名会撞（CS0108）。
    /// </summary>
    private static string Shorten(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    // ========================================================
    // 路径：浏览 / 复位 / 打开
    // ========================================================

    private static string? PickFolder(string title, string start)
    {
        try
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) d.InitialDirectory = start;
            return d.ShowDialog() == true ? d.FolderName : null;
        }
        catch
        {
            MessageBox.Show("这台机器上打不开文件夹选择框。可以直接把路径粘进输入框。",
                            "选择文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private void BrowseProjectRoot_Click(object sender, RoutedEventArgs e)
    {
        string? p = PickFolder("选择项目根目录", TxtProjectRoot.Text.Trim());
        if (p != null) { TxtProjectRoot.Text = p; RefreshPathHints(); }
    }

    private void ResetProjectRoot_Click(object sender, RoutedEventArgs e)
    {
        TxtProjectRoot.Text = "";
        RefreshPathHints();
    }

    private void OpenProjectRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = TxtProjectRoot.Text.Trim();
            if (dir.Length == 0) dir = ProjectStore.RootDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开：" + ex.Message, "打开目录",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseSeed_Click(object sender, RoutedEventArgs e)
    {
        string? p = PickFolder("选择立项素材种子目录", TxtSeed.Text.Trim());
        if (p != null) TxtSeed.Text = p;
    }

    private void ResetSeed_Click(object sender, RoutedEventArgs e) => TxtSeed.Text = "";

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 ffmpeg.exe",
                Filter = "可执行程序|ffmpeg.exe|所有文件|*.*",
                FileName = "ffmpeg.exe",
            };
            string start = Path.GetDirectoryName(TxtFfmpeg.Text.Trim()) ?? "";
            if (start.Length > 0 && Directory.Exists(start)) d.InitialDirectory = start;

            if (d.ShowDialog() == true)
            {
                TxtFfmpeg.Text = d.FileName;
                RefreshPathHints();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "选择 ffmpeg",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        FFmpegLocator.TryFind();
        string? found = FFmpegLocator.FFmpegPath;

        if (string.IsNullOrWhiteSpace(found))
        {
            MessageBox.Show("自动没找到。请点「浏览…」手动指定 ffmpeg.exe 的位置。",
                            "自动检测", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshPathHints();
            return;
        }

        TxtFfmpeg.Text = found;
        RefreshPathHints();
    }

    private void BrowseRenderRoot_Click(object sender, RoutedEventArgs e)
    {
        string? p = PickFolder("选择 HyperFrames 引擎根目录", TxtRenderRoot.Text.Trim());
        if (p != null) TxtRenderRoot.Text = p;
    }

    private void BrowseTemplateDir_Click(object sender, RoutedEventArgs e)
    {
        string? p = PickFolder("选择动效模板库目录", TxtTemplateDir.Text.Trim());
        if (p != null) TxtTemplateDir.Text = p;
    }

    private void BrowseArchiveRoot_Click(object sender, RoutedEventArgs e)
    {
        string? p = PickFolder("选择归档落点根目录", TxtArchiveRoot.Text.Trim());
        if (p != null) TxtArchiveRoot.Text = p;
    }

    private void BrowseTranscript_Click(object sender, RoutedEventArgs e)
    {
        string? p = PickFolder("选择转写稿兜底目录", TxtTranscript.Text.Trim());
        if (p != null) TxtTranscript.Text = p;
    }

    // ========================================================
    // 调试
    // ========================================================

    private void ChkDebug_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        RefreshDebugStatus();
    }

    private void RefreshDebugStatus_Click(object sender, RoutedEventArgs e) => RefreshDebugStatus();

    private void RefreshDebugStatus()
    {
        if (TxtDebugStatus == null) return;

        var shell = Owner as WorkbenchWindow;
        var dbg = shell?.Debug;

        bool live = dbg?.Active == true;
        bool wantOn = ChkDebug.IsChecked == true;

        if (live)
        {
            string ep = dbg!.Endpoint;
            TxtDebugStatus.Text = $"● 正在运行 —— 遥控口 {ep}";
            TxtDebugStatus.Foreground = (System.Windows.Media.Brush)FindResource("Ok");
            TxtDebugLogPath.Text = "日志文件：" + (dbg.LogFile.Length > 0 ? dbg.LogFile : "（未开启落文件）");
        }
        else if (wantOn)
        {
            TxtDebugStatus.Text = "○ 已勾选，但还没生效 —— 点右下角「保存设置」就会打开。";
            TxtDebugStatus.Foreground = (System.Windows.Media.Brush)FindResource("Warn");
            TxtDebugLogPath.Text = "";
        }
        else
        {
            TxtDebugStatus.Text = "○ 未打开。界面上发生的事不会被记录，遥控口也不监听。";
            TxtDebugStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
            TxtDebugLogPath.Text = "";
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string dir = Path.Combine(exeDir, "logs");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开日志文件夹：" + ex.Message, "打开目录",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ========================================================
    // 保存 / 取消
    // ========================================================

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // 端口先校验 —— 写进去一个非法值，下次启动遥控口会起不来，很难查
        if (!int.TryParse(TxtDebugPort.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            MessageBox.Show(
                "遥控口端口要填 1024 ~ 65535 之间的整数。\n\n" +
                "（默认 18766。挑一个没被别的东西占用的号就行。）",
                "端口不对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Save(_cfg, this, port);
        Saved = true;
        Close();
    }

    /// <summary>
    /// 把界面上的值写回配置并落盘。
    /// ★ 抽成静态是为了**能被无头验证直接调用** —— 不用真的弹一个窗口。
    /// </summary>
    public static void Save(AppConfig cfg, SettingsWindow ui, int port)
    {
        cfg.AgentBaseUrl = ui.TxtUrl.Text.Trim();
        cfg.AgentApiKey = ui.TxtKey.Password.Trim();
        cfg.AgentModel = ui.TxtModel.Text.Trim();
        cfg.AgentStream = ui.ChkStream.IsChecked == true;

        cfg.ProjectRoot = ui.TxtProjectRoot.Text.Trim();
        cfg.SeedDir = ui.TxtSeed.Text.Trim();
        cfg.FFmpegPath = ui.TxtFfmpeg.Text.Trim();
        cfg.RenderAssetRoot = ui.TxtRenderRoot.Text.Trim();
        cfg.HyperFramesTemplateDir = ui.TxtTemplateDir.Text.Trim();
        cfg.ArchiveRoot = ui.TxtArchiveRoot.Text.Trim();
        cfg.TranscriptDir = ui.TxtTranscript.Text.Trim();

        cfg.DebugEnabled = ui.ChkDebug.IsChecked == true;
        cfg.DebugLogToFile = ui.ChkDebugFile.IsChecked == true;
        cfg.DebugPort = port;

        cfg.Save();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
