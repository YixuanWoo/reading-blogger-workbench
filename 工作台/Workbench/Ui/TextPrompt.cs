// ============================================================
// TextPrompt.cs - 极简"输入一行字"对话框
// ============================================================
//
// 【为什么要自己写一个】
//   WPF 里没有内置的 InputBox（WinForms 有，但为了一个输入框去引 WinForms
//   不划算）。而"新建项目/改项目名"这类操作只需要一个文本框 + 确定取消，
//   用代码建出来就行 —— 不占一个 XAML 文件，也不会跑偏。
//
// 【为什么不用 Microsoft.VisualBasic.Interaction.InputBox】
//   它长成 Windows 2000 的样子，和这套深色界面放一起很突兀。
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteCut.Workbench.Ui;

/// <summary>输入一行文字的模态对话框。</summary>
public static class TextPrompt
{
    /// <summary>
    /// 弹出输入框。用户点确定返回输入内容（已 Trim），取消返回 null。
    /// </summary>
    public static string? Show(Window? owner, string title, string label,
                               string initial = "", string okText = "确定")
    {
        var win = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF)),
            FontFamily = new FontFamily("Microsoft YaHei"),
            Owner = owner,
            ShowInTaskbar = false,
        };

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

        root.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)),
        });

        var box = new TextBox
        {
            Text = initial,
            FontSize = 14,
            Padding = new Thickness(9, 7, 9, 7),
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x30)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
        };
        box.SelectAll();
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        string? result = null;

        Button MakeButton(string text, bool primary, bool isDefault)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 84,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 13,
                IsDefault = isDefault,
                Cursor = Cursors.Hand,
                Foreground = Brushes.White,
                Background = primary
                    ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))
                    : new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38)),
                BorderThickness = new Thickness(1),
            };
            return b;
        }

        var ok = MakeButton(okText, primary: true, isDefault: true);
        ok.Click += (s, e) =>
        {
            result = box.Text.Trim();
            win.DialogResult = true;
        };

        var cancel = MakeButton("取消", primary: false, isDefault: false);
        cancel.IsCancel = true;
        cancel.Click += (s, e) => { win.DialogResult = false; };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        win.Content = root;
        win.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };

        bool? ok2 = win.ShowDialog();
        if (ok2 != true) return null;
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
