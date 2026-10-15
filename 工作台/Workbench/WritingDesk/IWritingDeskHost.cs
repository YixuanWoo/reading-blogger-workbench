// ============================================================
// IWritingDeskHost.cs - 写作台对"外壳"的抽象
// ============================================================
//
// 【这一层是什么】
//   写作台(WritingDesk)模块想保持独立、不依赖"项目/主窗口"，
//   但有些动作它自己做不了、必须拜托宿主：
//     · 拿当前文档路径（.md 在哪、本机视图状态文件在哪）
//     · 状态栏提示（宿主往底部状态栏写一行字）
//     · 未保存改动的确认（宿主弹窗问"存/弃/取消"）
//     · 外部链接打开（宿主决定用不用系统浏览器）
//   所以定义这个接口，宿主实现它，写作台只管调用。
//
// 【为什么用接口而不是直接调主窗口】
//   · 无头测试能塞一个"什么都不干"的实现，避免弹窗卡死测试。
//   · 将来独立成写作软件，实现方换成那个软件的宿主即可，写作台一行不改。
//
// 【默认行为】
//   宿主若不实现某些能力，接口提供"空实现"——写作台不因宿主缺能力而崩。
// ============================================================

using System;

namespace LiteCut.Workbench.WritingDesk;

/// <summary>
/// 写作台对宿主的抽象：宿主实现，写作台调用。路径属性与确认类方法
/// 都有默认实现，宿主没提供时写作台也不会崩。
/// </summary>
public interface IWritingDeskHost
{
    /// <summary>当前文档的 .md 路径。默认空串。</summary>
    string MdPath => "";

    /// <summary>本机视图状态文件（view-state.json）路径。默认空串。</summary>
    string StatePath => "";

    /// <summary>往状态栏写一行提示。默认什么都不做。</summary>
    void Status(string message) { }

    /// <summary>
    /// 有未保存改动时询问用户：返回 true = 可以继续重读/离开（必要时先存），
    /// false = 保持现状别动。默认直接放行（无头测试用）。
    /// </summary>
    bool ConfirmDiscardUnsaved() => true;

    /// <summary>用系统默认方式打开一个外部链接。默认什么都不做。</summary>
    void OpenExternal(Uri url) { }

    /// <summary>左侧工作台导航是否已收起（沉浸式创作）。默认 false（未收起）。</summary>
    bool NavigationCollapsed => false;

    /// <summary>
    /// 请求宿主收起/展开左侧工作台导航（沉浸式创作）。
    /// 默认什么都不做（无头测试 / 未接线宿主）。
    /// </summary>
    void SetNavigationCollapsed(bool collapsed) { }
}
