// ============================================================
// AgentSettingsDialog.cs - Agent 直连设置 + 一键连通性检测
// ============================================================
//
// 【为什么要有这个窗口】
//   2026-09-29 用户反馈：点"交给 Agent"第一次弹错、第二次进度条跑
//   4 分多钟没结果。排查发现 AppConfig 里 AgentBaseUrl/Key/Model 三个
//   字段全空 —— 因为整个界面根本没有地方填！结果永远走"任务包文件
//   通道"干等外部 Agent 领取，独立运行的 LiteCut 没人领 → 干等 15 分钟。
//
//   所以这个窗口解决三件事：
//     1. 让配置有地方填（BaseUrl / Key / Model / 流式开关）
//     2. 填完立刻能"检测连接"—— 真发一次最小请求，通了显示
//        "Agent 已就绪 · 模型 · 延迟"，不通给人话原因（401=密钥错、
//        404=地址少了 /v1……），别让用户第一次正式调用才踩坑
//     3. 保存到全局配置，以后每次打开都是这个配置
//
// 【为什么用纯代码建窗口不用 XAML】
//   一次性的小对话框，代码建完即走，绕开"控件树建一半触发事件"那类坑。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LiteCut.Workbench.Agent;

public class AgentSettingsDialog : Window
{
    private readonly AppConfig _cfg;

    private readonly TextBox _txtUrl = new() { FontSize = 13 };
    private readonly PasswordBox _txtKey = new() { FontSize = 13 };
    private readonly TextBox _txtModel = new() { FontSize = 13 };
    private readonly CheckBox _chkStream = new() { Content = "流式（能看到逐字进度，推荐）", FontSize = 12.5 };
    private readonly TextBlock _txtResult = new()
    {
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };
    private readonly Button _btnTest;
    private bool _saved;

    /// <summary>保存过了才算真，调用方据此决定要不要刷新界面。</summary>
    public bool Saved => _saved;

    public AgentSettingsDialog(AppConfig cfg)
    {
        _cfg = cfg;

        Title = "Agent 连接设置";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x24)); // BgCard
        Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF)); // TextMain

        _txtUrl.Text = cfg.AgentBaseUrl ?? "";
        _txtKey.Password = cfg.AgentApiKey ?? "";
        _txtModel.Text = cfg.AgentModel ?? "";
        _chkStream.IsChecked = cfg.AgentStream;

        _btnTest = new Button
        {
            Content = "检测连接",
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 12.5,
        };
        _btnTest.Click += async (_, _) => await TestAsync();

        var btnSave = new Button
        {
            Content = "保存",
            Padding = new Thickness(18, 6, 18, 6),
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
        };
        btnSave.Click += (_, _) => Save();

        var btnCancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 12.5,
        };
        btnCancel.Click += (_, _) => Close();

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(Label("接口地址（OpenAI 兼容，一般填到 /v1）"));
        root.Children.Add(_txtUrl);
        root.Children.Add(Label("API Key"));
        root.Children.Add(_txtKey);
        root.Children.Add(Label("模型名（如 deepseek-chat / gpt-4o-mini / 本地模型名）"));
        root.Children.Add(_txtModel);
        root.Children.Add(_chkStream);

        root.Children.Add(new TextBlock
        {
            Text = "留空则走「任务包通道」：任务写到项目的 stages 目录，等外部 Agent 领取回填 —— 独立运行工作台时没人领取，会一直等。",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        testRow.Children.Add(_btnTest);
        var testHint = new TextBlock
        {
            Text = "← 填完先测一下，别等正式调用才发现配置错了",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)),
        };
        testRow.Children.Add(testHint);
        root.Children.Add(testRow);
        root.Children.Add(_txtResult);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        btnRow.Children.Add(btnSave);
        btnRow.Children.Add(btnCancel);
        root.Children.Add(btnRow);

        Content = root;

        // 输入框带点样式，不然默认白底在暗色窗口里很扎眼
        ApplyInputStyle(_txtUrl);
        ApplyInputStyle(_txtKey);
        ApplyInputStyle(_txtModel);
    }

    private static void ApplyInputStyle(Control c)
    {
        c.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x30)); // BgInput
        c.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF));
        c.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38));
        c.Padding = new Thickness(6, 4, 6, 4);
        c.Margin = new Thickness(0, 3, 0, 0);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(0, 10, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)),
    };

    private async System.Threading.Tasks.Task TestAsync()
    {
        // 用界面上还没保存的值测 —— 用户填完就能测，不用先保存
        var probe = new AppConfig
        {
            AgentBaseUrl = _txtUrl.Text.Trim(),
            AgentApiKey = _txtKey.Password.Trim(),
            AgentModel = _txtModel.Text.Trim(),
            AgentStream = false, // 检测走非流式，最简单直接
        };

        _btnTest.IsEnabled = false;
        _txtResult.Text = "检测中…（真的发了一次最小请求）";
        _txtResult.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1));

        var r = await AgentBridge.TestAsync(probe);

        if (r.Ok)
        {
            _txtResult.Text = "✔ Agent 已就绪 —— " + r.Detail + "（模型回音：" + Trim(r.Content, 20) + "）";
            _txtResult.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // Ok
        }
        else
        {
            _txtResult.Text = "✘ 没通：" + r.Error;
            _txtResult.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Warn
        }

        _btnTest.IsEnabled = true;
    }

    private void Save()
    {
        _cfg.AgentBaseUrl = _txtUrl.Text.Trim();
        _cfg.AgentApiKey = _txtKey.Password.Trim();
        _cfg.AgentModel = _txtModel.Text.Trim();
        _cfg.AgentStream = _chkStream.IsChecked == true;
        _cfg.Save();
        _saved = true;
        Close();
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
