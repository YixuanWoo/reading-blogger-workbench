// ============================================================
// NavNode.cs - 导航项的数据模型
// ============================================================
//
// 【为什么要"用数据描述菜单"，而不是在 XAML 里写死一堆按钮】
//   用户明确要求：菜单结构将来可能随时改，代码不能焊死。
//   写死在 XAML 里改一次要动 XAML + 事件处理 + 页面映射三处；
//   写成数据之后，改菜单只需要动 NavTree.cs 一个文件。
//
//   NavNode 只描述"菜单项长什么样、点它打开哪个页面"，
//   不关心怎么画、画在哪儿 —— 那是 NavHost 的事。
// ============================================================

using System;
using System.Windows.Controls;

namespace LiteCut.Workbench.Navigation;

/// <summary>
/// 导航页面的生命周期钩子。页面想在被切换进来/切走时做点什么，就实现它。
///
/// 可选实现：绝大多数页面不需要，只有"要主动铺数据"（精剪）或者
/// "每次进来重新扫目录"（项目列表）这种页面才需要。
/// </summary>
public interface INavPage
{
    /// <summary>页面被导航到（每次显示都调用，实现方要保证幂等）</summary>
    void OnEnter();

    /// <summary>页面被导航走（离开前把编辑结果交回共享状态）</summary>
    void OnLeave();
}

/// <summary>
/// "保存前同步"钩子。有些页面上的编辑还留在控件里没交回共享状态
/// （典型：精剪编辑器里刚拖完还没松手的那一下）。实现这个接口，
/// 点保存时就会先被叫一次。
/// </summary>
public interface ISaveSync
{
    /// <summary>把页面上尚未落地的编辑同步回共享状态</summary>
    void SyncBeforeSave();
}

/// <summary>
/// 一个导航项。
/// </summary>
public sealed class NavNode
{
    /// <summary>唯一标识。用来记住"当前选中的是谁"，改了会让记住的位置失效。</summary>
    public string Id { get; init; } = "";

    /// <summary>显示文字</summary>
    public string Title { get; init; } = "";

    /// <summary>左侧小图标（一个字符就够，比如 "✂"）</summary>
    public string Icon { get; init; } = "";

    /// <summary>灰显但不隐藏（用于"规划中"这种还没做的东西）</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>右侧小角标文字（比如 "规划中"）。留空就不显示。</summary>
    public string Badge { get; init; } = "";

    /// <summary>
    /// 分组名（"" = 不分组）。同名的连续节点会画一条分隔线 + 组标题。
    ///
    /// 【为什么只做视觉分段，不做二级菜单】
    ///   加一项就把列表面拉平会很快撞墙（写作台 → 7 项，录音台 → 8，看板 → 9）。
    ///   真正能撑住的是"分段"，不是"能展开的树"—— 流水线只有七步，
    ///   树状菜单会让人以为每一步下面还有子任务。
    ///
    /// 【怎么用】
    ///   照顺序排节点，给同一组的几项填同一个 Group 名；下一项换了名字就自动断开。
    ///   ★ 分组只影响"画一条线"，不影响跳转逻辑（那个走 Ids）。
    /// </summary>
    public string Group { get; init; } = "";

    /// <summary>这一项是不是新加的（画个"新"角标提醒自己，也提醒用户）。</summary>
    public bool IsNew { get; init; }

    /// <summary>
    /// 点开它创建哪个页面。
    /// 用"工厂函数"而不是"页面实例"，是为了懒创建 —— 没点开的页面不用占内存。
    /// </summary>
    public Func<UserControl>? CreateView { get; init; }

    /// <summary>
    /// true = 页面实例缓存起来，切走再切回还是同一个实例。
    /// 精剪页必须这样（编辑器里压着撤销历史和播放状态，重建就全丢了）。
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>给调试/日志用</summary>
    public override string ToString() => $"{Id}({Title})";
}
