// ============================================================
// NavTree.cs - 菜单结构定义（★ 想改菜单，只改这一个文件 ★）
// ============================================================
//
// 【两个作用域】
//   Global   —— 没打开项目时看到的一级菜单
//   InProject—— 打开某个项目之后，一级菜单"收起"，换成工作流菜单
//
// 【现在的结构（2026-10-15 V1.14.0 复盘提到一级之后）】
//   Global   ：灵感 / 进行中 / 已归档 ＋ 复盘
//              · 没有"素材库"—— 素材库是每个项目自己的（见 InProject）
//              · 没有"模板"—— 用户明确不要
//              · ★ 复盘搬到这里：它是"发布之后"的回顾，属于账号/项目层面的
//                整体视角，不是剪辑流程里的一步（用户 2026-10-15 拍板）。
//   InProject：写作台（0）/ 录音台 / 精剪 / 分镜设计 / 渲染 / 组接导出
//              + 素材库（项目自己的）
//            · 原"发布"页已砍掉
//            · ★ V1.17.0 砍掉「音频粗剪」：现在稿子是一句一句录的，
//              卡壳当场重录，粗剪唯一的用处（砍气口、砍卡壳）已经在录音阶段
//              解决掉了。录音台的「应用到下游」直接把母带交给精剪。
//            · 分三组显示：文稿 / 成片 / 资源（Group 字段，只画分隔线）
//            · 复盘**不在这里**（见上）
//
// 【将来想改菜单，怎么做】
//   1. 加一项：往下面的列表里加一个 new NavNode { ... } 就行，
//      页面类写好之后在这一行挂上去，其它文件一行都不用改。
//   2. 换顺序：调整列表顺序。
//   3. 换分组：改那一项的 Group 文字（同名连续 = 同一组）。
//   4. 加一个全新的作用域（比如将来真有"跨项目的稿子库"）：再加一个列表，
//      然后在 WorkbenchWindow 里决定什么时候用它。
//   5. 想改成"菜单项支持子菜单"：NavNode 加一个 Children 字段、
//      NavHost 里多画一层。节点模型和渲染是分开的，改起来不牵连业务。
// ============================================================

using System.Collections.Generic;
using System.IO;
using LiteCut.Workbench.Views;
using LiteCut.Workbench.WritingDesk;

namespace LiteCut.Workbench.Navigation;

/// <summary>
/// 工作台的菜单结构。整个程序里只有这里定义"有哪些导航项"。
/// </summary>
public static class NavTree
{
    /// <summary>导航项 Id（供代码里跳转到指定页面用，别写字符串字面量）</summary>
    public static class Ids
    {
        public const string Inspiration = "inspiration";
        public const string ProjectsActive = "projects.active";
        public const string ProjectsArchived = "projects.archived";

        public const string StepFineCut = "step.finecut";
        public const string StepShots = "step.shots";
        public const string StepRender = "step.render";
        public const string StepExport = "step.export";
        public const string ProjectLibrary = "project.library";

        /// <summary>
        /// ★ V1.13.0 复盘（自媒体数据 + 结论）。
        ///   它是**发布之后**的闭环，不属于剪辑流程，所以单独一组；
        ///   而且刻意做成"不依赖归档状态"——进行中也随时能填。
        ///
        /// ★ V1.14.0 起挂在**一级菜单（Global）**里，不再放项目内导航。
        ///   理由（用户 2026-10-15）：复盘是"账号 / 项目整体"的视角，
        ///   未来还要分「项目复盘 / 账号复盘」两类 —— 跟具体某个项目的
        ///   五步流水线不是一个层面的东西，摆在项目里会让人误以为它是第 7 步。
        ///   ⚠ 代价（已跟用户说明）：项目开着的时候左下角不会看到它，
        ///     要回「← 全部项目」再点。等复盘页真正做大（带项目选择器）之后再收口。
        /// </summary>
        public const string Review = "project.review";

        /// <summary>⭐ 写作台（项目内流水线的第 0 步，在① 粗剪之前）。</summary>
        public const string Script = "project.script";

        /// <summary>
        /// ★ V1.16.0 录音台：稿子 → 声音的桥。
        ///
        /// 【为什么单独一页，而不是塞进写作台】
        ///   写作台是"改字"的地方，录音台是"念字"的地方：
        ///   一个要安静、要大段文字；一个要提词、要电平、要一遍遍重录。
        ///   塞在一起，改稿时会误碰录音键，录音时又会被光标和编辑框干扰。
        ///   分成两页之后，两边都只做一件事 —— 数据仍然只有一份 script.md。
        ///
        /// 【顺序】文稿组里排在写作台之后：先写，再念，然后才是① 粗剪。
        /// </summary>
        public const string RecordingDesk = "project.record";
    }

    // ========================================================
    // 一级菜单（没打开项目时）
    // ========================================================

    /// <summary>未打开项目时显示的菜单。</summary>
    public static IReadOnlyList<NavNode> Global { get; } = new List<NavNode>
    {
        new NavNode
        {
            Id = Ids.Inspiration,
            Title = "灵感",
            Icon = "✦",
            CreateView = () => new InspirationView(),
        },
        new NavNode
        {
            Id = Ids.ProjectsActive,
            Title = "进行中",
            Icon = "▤",
            CreateView = () => new ProjectListView(ProjectListView.Filter.Active),
        },
        new NavNode
        {
            Id = Ids.ProjectsArchived,
            Title = "已归档",
            Icon = "▥",
            CreateView = () => new ProjectListView(ProjectListView.Filter.Archived),
        },

        // ── 复盘（★ V1.14.0 从项目内提到一级；V1.15.0 长成两层）──
        //
        //  ★ 指向的是**容器**（ReviewHostView），不是某一页：
        //    容器里第一层是账号页（更新节奏 + 账号数据 + 作品列表），
        //    点作品列表里的一行才钻进第二层（作品页）。
        //    第二层故意**不进菜单** —— 它是"从某支片点进去的"，不是独立的一站。
        new NavNode
        {
            Id = Ids.Review,
            Title = "复盘",
            Icon = "6",
            Group = "复盘分析",
            IsNew = true,
            CreateView = () => new ReviewHostView(),
        },
    };

    // ========================================================
    // 项目内的工作流菜单（打开项目后替换掉一级菜单）
    // ========================================================

    /// <summary>
    /// 打开某个项目之后显示的菜单 —— 就是"干活的那条流水线"。
    /// 顺序即工作流顺序，和原来顶部的步骤条完全一致。
    /// </summary>
    public static IReadOnlyList<NavNode> InProject { get; } = new List<NavNode>
    {
        // ── 文稿（这一组在① 之前：稿子是转写的上游）──
        new NavNode
        {
            Id = Ids.Script,
            Title = "写作台",
            Icon = "0",
            Group = "文稿",
            IsNew = true,
            CreateView = () => new WritingDeskView(),
            // ★ 必须缓存：主区是 Markdown 原文编辑区 + 光标位置，重建就丢
            KeepAlive = true,
        },
        new NavNode
        {
            Id = Ids.RecordingDesk,
            Title = "录音台",
            Icon = "◉",
            Group = "文稿",
            IsNew = true,
            CreateView = () => new RecordingDeskView(),
            // ★ 必须缓存：光标、选区、刚录完还没确认的那条都在内存里，重建就丢
            KeepAlive = true,
        },

        // ── 成片（V1.17.0：粗剪砍掉，精剪成为第 1 站）──
        //
        //  【为什么砍粗剪】
        //    它存在的唯一理由就是"砍气口、砍卡壳"。现在稿子一句一句录，
        //    卡壳当场就重录了 —— 粗剪面对一条已经是干净人声的母带，无事可做。
        //    （用户 2026-10-18 原话："粗剪是我完全没价值的东西"。）
        //
        //  【精剪为什么还在】
        //    气口留多长、两句之间接多紧，是**判断**不是算法：
        //    快节奏和慢节奏需要的呼吸长度不一样，只能人拖。
        new NavNode
        {
            Id = Ids.StepFineCut,
            Title = "精剪",
            Icon = "1",
            Group = "成片",
            CreateView = () => new StepFineCutView(),
            // ★ 必须缓存：编辑器压着撤销历史 / 播放状态，重建就全丢
            KeepAlive = true,
        },
        new NavNode
        {
            Id = Ids.StepShots,
            Title = "分镜设计",
            Icon = "2",
            Group = "成片",
            CreateView = () => new Step2ShotView(),
        },
        new NavNode
        {
            Id = Ids.StepRender,
            Title = "渲染",
            Icon = "3",
            Group = "成片",
            CreateView = () => new Step3RenderView(),
        },
        new NavNode
        {
            Id = Ids.StepExport,
            Title = "组接导出",
            Icon = "4",
            Group = "成片",
            CreateView = () => new Step4ExportView(),
        },

        // ── 资源（不属于任何一步，是项目内的公共资源）──
        new NavNode
        {
            Id = Ids.ProjectLibrary,
            Title = "素材库",
            Icon = "▤",
            Group = "资源",
            CreateView = () => new ProjectLibraryView(),
        },
    };
    // ★ 注意：这里**没有**「复盘」—— 它在 Global 一级菜单里（V1.14.0 拍板）。
    //   如果你又看到它在项目内导航里冒出来，那一定是别处手写了一份菜单，
    //   不是这个文件的问题（全程序只有这里定义菜单）。

    /// <summary>
    /// 按作用域取菜单。以后要加作用域，在这里加分支即可。
    /// </summary>
    public static IReadOnlyList<NavNode> ForScope(bool insideProject)
        => insideProject ? InProject : Global;

    /// <summary>
    /// 每个作用域的"默认落点"（启动时 / 记不住上次停在哪时用）。
    /// 刻意写出来而不是"取列表第一项"：菜单顺序将来会按手感调，
    /// 但"打开程序该落在哪"是产品决策，不该被顺序改动带跑。
    /// </summary>
    public static string DefaultGlobalId => Ids.ProjectsActive;

    /// <summary>
    /// ★ 打开项目后的默认落点（2026-10-02 起按"有没有稿子"分流）。
    ///
    /// 【为什么不能写死】
    ///   原来是常量 <c>StepCut</c>。加了写作台之后这会变成真bug：
    ///   打开《思辨与立场》这种**已经做完**的老项目，它没有稿子，
    ///   落地却弹到写作台（一片空白）→ 用户以为稿子丢了。
    ///
    /// 【分流规则（很朴素，只看一件事实：这一支片有没有稿源）】
    ///   有稿源（script.md 存在且非空）→ 落写作台
    ///       理由：这个项目已经在走"文稿驱动"，从稿子开始接着干才对。
    ///   没稿源                              → 落精剪
    ///       理由：老项目（2026 年以前直接导音频剪的）没有稿子，写作台/录音台
    ///             都跟它没关系；它要接着干的下一步就是精剪。
    ///             （V1.17.0 之前这里是落粗剪 —— 粗剪已经砍掉了。）
    ///
    /// ★ 这样处理的好处：**不需要给project.json 加字段**，
    ///   也就不存在"老档案缺 DriveMode 字段"的迁移问题 —— 判据直接看磁盘事实。
    ///
    /// ★ V1.14.0：归档项目**不再走这里** —— 归档项目打开时压根不进项目作用域，
    ///   直接停在全局作用域的「复盘」页（见 WorkbenchWindow.OpenProject）。
    ///   复盘已经搬到一级菜单，它不属于任何项目的工作流。
    /// </summary>
    public static string DefaultProjectId => HasScript ? Ids.Script : Ids.StepFineCut;

    /// <summary>当前打开的项目是不是"已归档"。</summary>
    public static bool IsArchivedProject =>
        WorkbenchWindow.Session?.Current?.IsArchived == true;
    /// <summary>
    /// 当前项目有没有稿源。判据 = <c>script.md</c> 存在且非空。
    ///
    /// 【为什么用"看文件"而不是"看配置字段"】
    ///   读一个字段要先给它默认值、再处理老档案没有该字段的情况；
    ///   看文件则天然覆盖全部历史 —— 没有 script.md 的项目就是老路，一句都不用多写。
    ///   （这跟"永久 ID 只给空的补"是同一个思路：让老数据自己落到正确的分支上。）
    /// </summary>
    public static bool HasScript
    {
        get
        {
            var r = WorkbenchWindow.Session?.Current;
            if (r == null) return false;
            try
            {
                string p = Path.Combine(r.Dir, "script.md");
                return File.Exists(p) && new FileInfo(p).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 在菜单里挑一个要落过去的 Id：想落的那项不在这个作用域里就用默认落点。
    /// （比如上次停在项目里的「精剪」，这次没打开项目 → 退到「进行中」）
    /// </summary>
    public static string PickLanding(bool insideProject, string? want)
    {
        var scope = ForScope(insideProject);

        if (!string.IsNullOrEmpty(want))
        {
            foreach (var n in scope)
                if (n.Id == want) return n.Id;
        }

        string def = insideProject ? DefaultProjectId : DefaultGlobalId;
        foreach (var n in scope)
            if (n.Id == def) return n.Id;

        // 默认落点被删掉了（改菜单时可能发生）→ 退回第一项
        return scope.Count > 0 ? scope[0].Id : "";
    }

    /// <summary>在所有作用域里按 Id 找一个节点（找不到返回 null）。</summary>
    public static NavNode? Find(string id)
    {
        foreach (var n in Global)
            if (n.Id == id) return n;
        foreach (var n in InProject)
            if (n.Id == id) return n;
        return null;
    }
}
