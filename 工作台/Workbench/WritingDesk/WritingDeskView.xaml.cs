// ============================================================
// WritingDeskView.xaml.cs - 写作台（Markdown 版）
// ============================================================
//
// 【这一页在流水线里的位置：第 0 步，稿源】
//   稿子(script.md) → transcript.json → ①粗剪 → ②精剪 → ③分镜 → ④渲染 → ⑤导出
//   它产出的东西是① 的输入。
//
// 【★ 用户习惯：写一整篇 Markdown】
//   · 回车只是换行，不再切段 —— 结构由标题(#~######)表达。
//   · 左侧导航栏 = 按标题自动生成的目录(TOC)，点标题定位。
//   · 右上角「展开预览」才打开渲染面板（不默认，按需开）。
//   · 改字就是改文档，保存走顶栏「保存项目」/ Ctrl+S / 20 秒自动保存。
//
// 【★ 录音整体删除】
//   旧版"回车切块 + 段落录音脚手架"（永久ID/audio/待重录/script.json）
//   全部随块模型删除。录音是期二的事，届时按标题/行范围重新挂，与本文无关。
//
// 【★ 模块独立：本页只认"路径 + 宿主"，不认"项目"】
//   通过 <see cref="IWritingDeskHost"/> 拿 .md 路径、发状态提示、问未保存确认。
//   主窗口通过 <see cref="IWritingDeskPage"/> 调保存/未保存判断。
//   将来独立成写作软件：换个宿主实现，整个 WritingDesk 文件夹直接带走。
// ============================================================

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.WritingDesk.Markdown;

namespace LiteCut.Workbench.WritingDesk;

public partial class WritingDeskView : UserControl, INavPage, IWritingDeskPage
{
    private WritingDocument _doc = new();
    private IWritingDeskHost? _host;
    private string _loadedMd = "";

    /// <summary>打字区正在被程序自己改（回填/重载）时为true，用来挡掉 TextChanged 递归。</summary>
    private bool _suppress;

    public WritingDeskView()
    {
        InitializeComponent();
        // ★ force:true —— 控件首次 Loaded 是"这个页面刚建出来"，直接读一次。
        Loaded += (s, e) => Reload(force: true);
    }

    /// <summary>宿主注入：主窗口在导航到本页时设置（见 WorkbenchWindow.GoTo 的接线）。</summary>
    public void AttachHost(IWritingDeskHost host)
    {
        _host = host;
    }

    private IWritingDeskHost Host => _host ?? NullHost.Instance;

    // ========================================================
    // 生命周期（INavPage）
    // ========================================================

    public void OnEnter()
    {
        SetNavigationCollapsed(Host.NavigationCollapsed);   // 同步图标状态（宿主可能已被收起）
        Reload(force: false);
    }

    public void OnLeave()
    {
        RestoreNavigationOnLeave();   // ★ 沉浸式只在写作台里成立，切走就恢复导航
        if (_doc.Text.Length > 0 && HasUnsavedChanges)
            Host.Status("⚠ 稿子有改动还没保存 —— 回来时会问你要不要保存");
    }

    // ========================================================
    // IWritingDeskPage：主窗口驱动保存 / 未保存判断
    // ========================================================

    public bool HasUnsavedChanges => DirtyVsDisk();

    /// <summary>存稿子。由主窗口在「保存项目」/ Ctrl+S / 自动保存时调用。</summary>
    public bool Save()
    {
        string md = Host.MdPath;
        if (string.IsNullOrEmpty(md)) return false;
        if (_doc.IsEmpty) return false;
        if (!DirtyVsDisk()) return false;

        try
        {
            WritingDocumentStore.Save(md, _doc);
            SavePreviewState();
            RebuildStat();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("稿子保存失败：" + ex.Message, "保存稿子",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>内存里的正文跟磁盘上的是否不一样。</summary>
    private bool DirtyVsDisk()
    {
        string md = Host.MdPath;
        if (string.IsNullOrEmpty(md) || _doc.Text.Length == 0) return false;
        try
        {
            return !string.Equals(_doc.Text.Trim(), WritingDocumentStore.Load(md).Text.Trim(),
                                  StringComparison.Ordinal);
        }
        catch
        {
            return true;   // 读不出来 → 保守当有改动
        }
    }

    // ========================================================
    // 读 / 写
    // ========================================================

    /// <summary>
    /// 从磁盘重新读一遍稿子。
    /// 未保存改动时绝不默默覆盖内存（老规矩：写了一半不能无声丢掉）。
    /// </summary>
    private void Reload(bool force = false)
    {
        string md = Host.MdPath;
        if (string.IsNullOrEmpty(md))
        {
            RebuildAll();
            return;
        }

        bool projectChanged = !string.Equals(_loadedMd, md, StringComparison.OrdinalIgnoreCase);

        // 同一个文档、有未保存改动 → 先问，绝不默默覆盖
        if (!force && !projectChanged && HasUnsavedChanges)
        {
            bool proceed = Host.ConfirmDiscardUnsaved();
            if (!proceed) { RebuildAll(); return; }   // 用户选取消：内存原封不动
        }

        _doc = WritingDocumentStore.Load(md);
        _loadedMd = md;

        // 恢复本机视图状态（预览开关）
        RestorePreviewState();

        FillEditor();
        RebuildAll();
    }

    private void FillEditor()
    {
        _suppress = true;
        TbxEditor.Text = _doc.Text;
        _suppress = false;
    }

    private void RebuildAll()
    {
        RebuildToc();
        RebuildPreview();
        RebuildStat();
    }

    // ========================================================
    // ★ 打字区：Markdown 原文（回车只是换行）
    // ========================================================

    private void TbxEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _doc.Text = TbxEditor.Text ?? "";
        RebuildAll();
    }

    // ========================================================
    // 目录（TOC）—— 按标题自动生成
    // ========================================================

    private sealed class TocRow
    {
        public string Indent { get; set; } = "";
        public string LevelMark { get; set; } = "";
        public string Text { get; set; } = "";
        public int LineIndex { get; set; }
    }

    private void RebuildToc()
    {
        // ★ 必须展平：Build 返回的是树（根节点列表），二级以下标题在 Children 里；
        //   只拿根节点会让 TOC 只剩一级标题（V1.12.2 教训）。
        var rows = MarkdownOutline.Flatten(_doc.Outline).Select(n => new TocRow
        {
            Indent = new string('　', n.Level - 1),
            LevelMark = $"H{n.Level}",
            Text = n.Text,
            LineIndex = n.LineIndex,
        }).ToList();

        ListToc.ItemsSource = rows;
        ListToc.SelectedIndex = -1;
    }

    private void OnTocPick(object sender, SelectionChangedEventArgs e)
    {
        if (ListToc.SelectedItem is not TocRow row) return;

        // 定位到该标题所在行：把光标设到该行开头，并滚动过去
        int offset = OffsetOfLine(row.LineIndex);
        if (offset >= 0 && offset <= TbxEditor.Text.Length)
        {
            TbxEditor.CaretIndex = offset;
            TbxEditor.ScrollToLine(row.LineIndex);
            TbxEditor.Focus();
        }
    }

    /// <summary>把"第几行(0起)"换算成文本偏移（含换行符）。</summary>
    private int OffsetOfLine(int lineIndex)
    {
        int i = 0, offset = 0;
        foreach (var ch in TbxEditor.Text)
        {
            if (i >= lineIndex) return offset;
            offset++;
            if (ch == '\n') i++;
        }
        return offset;
    }

    // ========================================================
    // 渲染预览（按需开）
    // ========================================================

    private void RebuildPreview()
    {
        if (!GridPreview.IsVisible) return;   // 没展开就不渲染（省资源）
        PreviewViewer.Document = MarkdownRenderer.Render(_doc.Text);
    }

    private void BtnPreview_Click(object sender, RoutedEventArgs e)
    {
        bool open = GridPreview.Visibility == Visibility.Collapsed;
        SetPreviewOpen(open);
    }

    private void SetPreviewOpen(bool open)
    {
        if (open)
        {
            GridPreview.Visibility = Visibility.Visible;
            SplitterPreview.Visibility = Visibility.Visible;
            ColPreview.Width = new GridLength(380);
            BtnPreview.ToolTip = "收起预览";
            PreviewClose.Visibility = Visibility.Visible;   // 双栏加斜杠 = 再点就收起
            RebuildPreview();
        }
        else
        {
            GridPreview.Visibility = Visibility.Collapsed;
            SplitterPreview.Visibility = Visibility.Collapsed;
            ColPreview.Width = new GridLength(0);
            BtnPreview.ToolTip = "展开预览";
            PreviewClose.Visibility = Visibility.Collapsed;
        }
    }

    private void RestorePreviewState()
    {
        string statePath = Host.StatePath;
        bool open = string.IsNullOrEmpty(statePath)
            ? false
            : WritingDocumentStore.LoadPreviewState(statePath);
        // 首次展开前不渲染（SetPreviewOpen 里 RebuildPreview 只在 open=true 时走）
        SetPreviewOpen(open);
    }

    private void SavePreviewState()
    {
        bool open = GridPreview.Visibility == Visibility.Visible;
        WritingDocumentStore.SavePreviewState(Host.StatePath, open);
    }

    // ========================================================
    // ★ 沉浸式创作：收起/展开左侧工作台导航
    // ========================================================

    private void BtnImmersive_Click(object sender, RoutedEventArgs e)
        => SetNavigationCollapsed(!Host.NavigationCollapsed);

    /// <summary>把沉浸式状态同步给宿主；按钮图标随状态切换语义。</summary>
    private void SetNavigationCollapsed(bool collapsed)
    {
        Host.SetNavigationCollapsed(collapsed);

        // 图标语义：平时双栏框 + 竖线偏左（"左侧有栏"）；
        // 收起后给框加一条斜杠 = 再点就展开
        if (collapsed)
        {
            BtnImmersive.ToolTip = "展开左侧导航（退出沉浸式创作）";
            ImmersiveFrame.Stroke = BrushOf("#FFEA580C");       // 暗橙：提示当前处于沉浸式
            ImmersiveDivider.Stroke = BrushOf("#FFEA580C");
        }
        else
        {
            BtnImmersive.ToolTip = "沉浸式创作：收起左侧导航";
            ImmersiveFrame.Stroke = (SolidColorBrush)FindResource("TextDim");
            ImmersiveDivider.Stroke = (SolidColorBrush)FindResource("TextDim");
        }
    }

    /// <summary>离开本页时把左侧导航展开回来 —— 沉浸式只在写作台里成立，切走就恢复。</summary>
    private void RestoreNavigationOnLeave()
    {
        if (Host.NavigationCollapsed) Host.SetNavigationCollapsed(false);
    }

    // ========================================================
    // 底部统计
    // ========================================================

    private void RebuildStat()
    {
        if (_doc.IsEmpty)
        {
            TxtStat.Text = "还没有稿子";
            TxtSub.Text = "一整篇 Markdown · 回车只是换行 · 标题结构自动成目录";
            TxtStatL.Text = "空稿";
            TxtStatR.Text = "";
            return;
        }

        int titles = _doc.Outline.Count;
        TxtStat.Text = $"{_doc.CharCount} 字 · {_doc.SentenceCount} 句 · {_doc.LineCount} 行"
                     + $"　·　{titles} 个标题";
        TxtStatL.Text = HasUnsavedChanges ? "● 有改动" : $"上次保存 {_doc.SavedAt}";
        TxtStatR.Text = titles > 0 ? "目录按标题自动生成，点击可定位" : "还没有标题 —— 用 # 开头写标题即可生成目录";

        TxtSub.Text = $"{_doc.CharCount} 字 · {_doc.SentenceCount} 句 · {_doc.LineCount} 行 · "
                    + (HasUnsavedChanges ? "● 有改动（20 秒内自动保存，或按 Ctrl+S）"
                                         : $"上次保存 {_doc.SavedAt}");
    }

    // ========================================================
    // 打开 .md
    // ========================================================

    private void BtnOpenMd_Click(object sender, RoutedEventArgs e)
    {
        string md = Host.MdPath;
        if (string.IsNullOrEmpty(md) || !File.Exists(md))
        {
            MessageBox.Show("还没有 script.md —— 先写点东西再保存。", "打开稿源",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(md)
        { UseShellExecute = true });
    }

    // ========================================================
    // 辅助
    // ========================================================

    private static SolidColorBrush BrushOf(string argb)
        => new((Color)ColorConverter.ConvertFromString(argb));

    /// <summary>宿主没注入时的空实现（无头测试 / 未接线时用，不弹窗）。</summary>
    private sealed class NullHost : IWritingDeskHost
    {
        public static readonly NullHost Instance = new();
    }
}
