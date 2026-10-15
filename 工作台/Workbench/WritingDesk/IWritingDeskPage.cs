// ============================================================
// IWritingDeskPage.cs - 主窗口对写作台页面的最小接口
// ============================================================
//
// 【这一层是什么】
//   主窗口(WorkbenchWindow)在"保存项目 / Ctrl+S / 自动保存 / 关窗拦截"
//   时要把写作台的稿子一并处理。它不该硬编码 <see cref="WritingDeskView"/>
//   这个具体类 —— 那样写作台一旦改名/重构，主窗口就得跟着改。
//   所以抽出这个最小接口，主窗口只认接口，写作台自己实现。
//
// 【与 IWritingDeskHost 的分工】
//   IWritingDeskHost = 写作台 → 宿主（写作台调用宿主的能力）
//   IWritingDeskPage = 宿主 → 写作台（宿主调用写作台的能力）
//   一进一出，写作台与宿主互不依赖具体类。
// ============================================================

namespace LiteCut.Workbench.WritingDesk;

/// <summary>主窗口通过这个接口驱动写作台页面的保存 / 未保存判断。</summary>
public interface IWritingDeskPage
{
    /// <summary>把当前稿子存盘（写回 .md）。返回是否真的写了。</summary>
    bool Save();

    /// <summary>有没有未保存的改动（主窗口用它拦退出/拦切换）。</summary>
    bool HasUnsavedChanges { get; }
}
