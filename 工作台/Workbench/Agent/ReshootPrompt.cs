// ============================================================
// ReshootPrompt.cs - 第④步渲染页：**单镜重渲**给 Agent 的提示词
// ============================================================
//
// 【这一步跟"整批渲染"（RenderPrompt）差在哪】
//   整批渲染：从零把 219 个镜头渲出来，产物是"一批文件"。
//   单镜重渲：某一镜用户看着不满意，**在原实现的基础上改**，重渲成新的一版。
//   所以提示词的重心完全不同 —— 整批那份讲"怎么做"，
//   这份讲"**只改哪里、别动哪里**"。
//
// 【用户拍板的两条硬要求（2026-09-30）】
//   ① 重渲按钮的本质 = 拉 Agent 来干活。所以任务里必须带上**用户写的修改意见**，
//      否则"重渲"就只是把同一镜再渲一遍，毫无意义。
//   ② ★ 提示词要让 Agent 参考「这一镜原本的分镜设计」+「原本渲染前写好的代码」，
//      **在原代码基础上修改，而不是整篇重写** —— 重写既丢掉用户已经认可的设计，
//      又白白多花几倍 token。
//
// 【为什么正文里故意不贴 HTML 源码】
//   原实现一个文件 ~8KB。把它整段复制进提示词，等于每次重渲都白付这 8KB 的输入成本，
//   而且源码一改，提示词里那份立刻变成过期副本（Agent 会照着旧代码改）。
//   正确做法：**只给绝对路径，让 Agent 自己去读那个文件**。
//   这不是任务不完整 —— 这是刻意的，提示词里会写明这一点。
//
// 【任务包文件名】
//   单镜重渲落到 stages/04-render/agent-reshoot.json（**不是** agent-request.json）。
//   整批渲染和单镜重渲可能同时存在（一边补渲、一边调镜），
//   同一个文件名会让两份任务互相覆盖 —— 见 AgentRequest.RequestFileName。
// ============================================================

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LiteCut.Workbench.Agent;

public static class ReshootPrompt
{
    /// <summary>单镜重渲的请求文件名（跟整批渲染的 agent-request.json 分开）。</summary>
    public const string RequestFile = "agent-reshoot.json";

    /// <summary>单镜重渲的回包文件名。</summary>
    public const string ResponseFile = "agent-reshoot-response.json";

    /// <summary>任务包里那份"人能读"的提示词。</summary>
    public const string PromptFile = "agent-reshoot-prompt.md";

    /// <summary>任务标题（也是任务包里 title 字段）。</summary>
    public static string BuildTitle(string projectName, int index) =>
        $"单镜重渲 · {projectName} · #{index:D3}";

    // ========================================================
    // 原实现在哪 —— 按优先级给候选，让 Agent 挑第一个存在的
    // ========================================================
    //
    // 【为什么不写死一个路径】
    //   工作台自己生成的"卡片"落在 outputDir，
    //   而整批渲染时执行者往往把真正的实现放在引擎工程目录的 shots/ 子目录下
    //   （这次就是 D:\HyperFrames\思辨与立场\shots\）—— 两个地方都可能有。
    //   给一串候选、让 Agent 自己判断哪个是真的，比工作台猜错一个路径强。
    public static List<string> SourceCandidates(RenderSpec spec, string outputDir, int index)
    {
        string name = $"shot_{index:D3}.html";
        var list = new List<string>();

        void Add(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string p = Path.Combine(dir, name);
            if (!list.Contains(p)) list.Add(p);
        }

        Add(Path.Combine(spec.EngineProjectDir, "shots-blocks")); // ★ 块级渲染的真实实现（2026-09-30 起的主力）
        Add(Path.Combine(spec.EngineProjectDir, "shots"));   // 旧的整批渲染实现
        Add(outputDir);                                      // 工作台生成的基础卡片
        Add(spec.EngineProjectDir);

        return list;
    }

    /// <summary>
    /// 相对资源（底图 / GSAP / 配图）所在目录 —— 跟着**实际找到的那个实现文件**走。
    /// </summary>
    // 【为什么不能写死 shots/assets】
    //   块级成片把实现放在 shots-blocks/ 下，它的 assets/ 也在 shots-blocks/assets/。
    //   写死 shots/assets 会让重渲的 Agent 拿着一份对不上的资源目录去改代码。
    public static string AssetsDirOf(RenderSpec spec, string outputDir, int index)
    {
        foreach (var c in SourceCandidates(spec, outputDir, index))
        {
            if (File.Exists(c)) return Path.Combine(Path.GetDirectoryName(c)!, "assets");
        }
        return Path.Combine(spec.EngineProjectDir, "shots-blocks", "assets");
    }

    /// <summary>
    /// 这一镜缩略图该落哪（审阅台读它做左栏小图 / 网格视图）。
    /// 重渲完顺手更新这个文件，否则审阅台里看到的还是旧画面。
    /// </summary>
    public static string ThumbPath(string renderStageDir, int index) =>
        Path.Combine(renderStageDir, "thumbs", $"t_{index:D3}.jpg");

    // ========================================================
    // 系统提示词
    // ========================================================

    public static string SystemPrompt()
    {
        var sb = new StringBuilder();

        // ★ T-26①（V1.11.3）：《视觉规范.md》注入 —— 重渲也必须守同一份画面标准
        //   （改镜头时最容易"顺手破规"：只顾意见、把规范忘了）。文件缺失 → 空串跳过。
        sb.Append(VisualStandard.BuildSection());

        sb.AppendLine("你是「工作台」的**单镜重渲**执行者。");
        sb.AppendLine("用户对某一个镜头不满意，写了修改意见；你要在**这一镜原有的实现上改**，");
        sb.AppendLine("然后只把这一镜重渲成新的 MP4。");
        sb.AppendLine();

        sb.AppendLine("# 第一条铁律：改，不要重写");
        sb.AppendLine("- 这一镜的 HTML 原来已经写好、也渲过一遍了。任务正文给了它的**绝对路径**。");
        sb.AppendLine("  你的第一件事是 Read 那个文件，看清它现在的结构：底图怎么铺、有哪些元素、");
        sb.AppendLine("  动效怎么编排的、时间是怎么分布的。");
        sb.AppendLine("- ★ 任务正文**故意不贴 HTML 源码**，只给路径。这不是任务缺信息 ——");
        sb.AppendLine("  是刻意的省 token：你只需要读那一个 ~8KB 的文件，而不是让源码被复制进提示词");
        sb.AppendLine("  （复制进来的还有过期风险：源码一改，提示词里那份就是旧的）。");
        sb.AppendLine("- ★ **只改用户点到的部分，其余一律原样保留**（版式、配色、字体、背景、层级、时长）。");
        sb.AppendLine("  用户说\"字太小\"就只动字号，不要顺手把配色、动效、结构一起重做 ——");
        sb.AppendLine("  他已经认可了现在这一版，重做等于把他认可的东西也扔了。");
        sb.AppendLine("- 改完把新 HTML **写回同一个路径**；原文件先另存一份 `<同名>.prev.html`。");
        sb.AppendLine("- 原实现找不到 / 读不出来：按任务正文里的分镜字段重新实现一版，");
        sb.AppendLine("  并在回包里**说清\"没找到原实现，这是重写的\"** —— 别默默重写。");
        sb.AppendLine();

        sb.AppendLine("# 画面硬规格（所有视频统一，不许破）");
        sb.AppendLine("- 底图、画幅、帧率按任务正文；元素一律**叠在底图之上**，四周留白让网格透出来。");
        sb.AppendLine("- 不要白底/纯黑底大色块，不要把任何图片铺满全屏（会把统一底图盖掉）。");
        sb.AppendLine("- 文字用浅色：米白 #f3ecdc / 亮金 #f0d68a / 浅蓝 —— 深蓝底上深色字看不清。");
        sb.AppendLine("- 动效只能用网页技术做（HTML/CSS/GSAP）。航拍、实拍、三维建模渲不出来，");
        sb.AppendLine("  用户的意见如果要这类效果，用网页能做的等效手法实现，并在回包里说明降级。");
        sb.AppendLine("- 原实现里引用的相对资源（底图、GSAP、配图）就在 HTML 隔壁的 assets/ 目录，");
        sb.AppendLine("  路径照旧，别改成绝对路径。要换配图就把新图放进那个 assets/（或 <outputDir>/images/）。");
        sb.AppendLine();
        sb.AppendLine("# ★ 字幕 ≠ 文字（2026-09-30 实际踩过的坑，别再犯）");
        sb.AppendLine("- 用户说「不要字幕」，禁的只是底部随口播同步的白色字幕条 —— 不是画面里的文字。");
        sb.AppendLine("- **画面中必须有关键词/关键语/数字/标题**作为视觉元素。一个文字都没有的镜头 = 废镜头；");
        sb.AppendLine("  如果原实现正好缺文字（老版本踩过这个坑），这次顺手把本镜关键词补进去（改，不重写）。");
        sb.AppendLine();

        sb.AppendLine("# 时长：默认不许动");
        sb.AppendLine("- 这一镜的时长（任务正文 durationSec）是成片拼接的依据 —— 改了就整条片子错位。");
        sb.AppendLine("- **默认必须保持原时长**（误差 ±0.1 秒）。用户的意见就算会让画面变长变短，也要压在原时长里。");
        sb.AppendLine("- 唯一的例外：用户在修改意见里**明确要求**改变这一镜的长短（\"这一镜太赶，多给 2 秒\"）。");
        sb.AppendLine("  这种情况下照办，但**必须在回包里单独写一行**\"时长已由 X 改为 Y\"——");
        sb.AppendLine("  用户要知道拼片会因此错位，得回去调。");
        sb.AppendLine();

        sb.AppendLine("# 产物");
        sb.AppendLine("- 新 MP4 覆盖 <outputDir>/shot_NNN.mp4；**覆盖前先把旧版另存为 shot_NNN.prev.mp4**");
        sb.AppendLine("  （用户要靠它做 A/B 对比，判断这一改到底改好了还是改坏了）。");
        sb.AppendLine("- 顺手更新缩略图 <renderStageDir>/thumbs/t_NNN.jpg（从新 MP4 抽一帧，约 320×180）。");
        sb.AppendLine("  工具用渲染引擎自带的 ffmpeg（<引擎工程根>/_tools/bin/ffmpeg.exe）。");
        sb.AppendLine("- 只重渲这一镜，**不要去动其它镜头**（哪怕你觉得它们也有问题）。");
        sb.AppendLine();
        sb.AppendLine("# ★ 渲完必须自检（不许渲完就交）");
        sb.AppendLine("- 从新 MP4 抽 2~3 帧（约 0.42 / 0.85 时长处）**真的看一眼图**，确认用户提的那处确实改好了。");
        sb.AppendLine("- 四条判据：① 产物写入时间晚于本次开始（时间没变 = 没渲上，先挪走旧文件再渲）；");
        sb.AppendLine("  ② 时长 ≈ 原时长（偏差 < 0.12 秒）；③ 体积正常（真画面几十 KB 起步，< 20KB 基本是空画面）；");
        sb.AppendLine("  ④ 抽帧肉眼核对该镜要求的元素都在、没出界/重叠/被裁。");
        sb.AppendLine("- 自己看着不对就改到对为止再交，别把\"渲出来了\"当成\"改好了\"。");
        sb.AppendLine("  ★ 若抽帧看到画面里出现了**别的镜头的内容**，那不是设计串台，是旧产物没被覆盖（渲染失败），");
        sb.AppendLine("    比对一下体积和写入时间就知道了 —— 先挪走旧文件再重渲。");
        sb.AppendLine();

        sb.AppendLine("# 进度与回包（工作台在实时看，别让它干等）");
        sb.AppendLine("- 任务包目录（任务正文 workDir）里有 agent-reshoot.json，其中的 id 是本次任务编号。");
        sb.AppendLine("- 干活过程中往 workDir/agent-progress.json 写进度（跟整批渲染共用这一个文件）：");
        sb.AppendLine("    {\"id\":\"<id>\",\"text\":\"#NNN 正在读原实现…\"}");
        sb.AppendLine("    {\"id\":\"<id>\",\"text\":\"#NNN 正在改 HTML（改了字号与入场）…\"}");
        sb.AppendLine("    {\"id\":\"<id>\",\"text\":\"#NNN 渲染中（约 30 秒）…\"}");
        sb.AppendLine("- 全部完成后写 workDir/agent-reshoot-response.json：");
        sb.AppendLine("    {\"id\":\"<id>\",\"status\":\"done\",\"content\":\"<人话：改了哪几处 / 新时长 / 产物路径 / 有无降级>\"}");
        sb.AppendLine("- 做不下去要放弃时写 {\"id\":\"<id>\",\"status\":\"error\",\"error\":\"原因\"}。");
        sb.AppendLine("- ★ 回包与进度文件的 id 必须与请求一致 —— 工作台只认配对的 id。");
        sb.AppendLine();

        sb.AppendLine("# 回包里务必写清的几件事");
        sb.AppendLine("1. 你读了哪个原实现文件（完整路径）。");
        sb.AppendLine("2. 具体改了哪几处（说出改动落在哪个类名/选择器/片段上），以及为什么这样改能解决用户的问题。");
        sb.AppendLine("3. 新 MP4 的时长（跟原来比有没有变）。");
        sb.AppendLine("4. 有没有降级或没做到的地方 —— 如实说，别粉饰。");

        return sb.ToString();
    }

    // ========================================================
    // 任务正文
    // ========================================================

    /// <summary>
    /// 单镜重渲的任务正文。
    /// </summary>
    /// <param name="shot">要重渲的那一镜（分镜表里的原始设计）。</param>
    /// <param name="spec">成片规格（画幅/底图/引擎/模板库/工程目录）。</param>
    /// <param name="workDir">任务包目录。</param>
    /// <param name="outputDir">渲染产物目录。</param>
    /// <param name="renderStageDir">项目里的 stages/04-render（缩略图落这儿）。</param>
    /// <param name="userNote">★ 用户写的修改意见 —— 最高优先级，原样照办。</param>
    /// <param name="edits">用户在界面里直接改过的字段（文案/画面/动效），可能为空。</param>
    public static string BuildUserPrompt(
        ShotRow shot,
        RenderSpec spec,
        string workDir,
        string outputDir,
        string renderStageDir,
        string userNote,
        IReadOnlyDictionary<string, string>? edits = null)
    {
        var sb = new StringBuilder();

        int i = shot.Index;
        string video = Path.Combine(outputDir, $"shot_{i:D3}.mp4");
        string prev = Path.Combine(outputDir, $"shot_{i:D3}.prev.mp4");
        var cands = SourceCandidates(spec, outputDir, i);
        string assetsDir = AssetsDirOf(spec, outputDir, i);

        sb.AppendLine("# 单镜重渲任务");
        sb.AppendLine();
        sb.AppendLine($"- 项目：{spec.Project}");
        sb.AppendLine($"- 镜号：#{i:D3}（共 {i} 号这一镜，其它镜头一律不动）");
        sb.AppendLine($"- **时长（默认必须保持）**：{shot.DurationSec:F3} 秒");
        sb.AppendLine($"- 它在成片里的位置：{shot.StartSec:F2}s – {shot.EndSec:F2}s");
        sb.AppendLine($"- outputDir（新 MP4 写这里）：{outputDir}");
        sb.AppendLine($"- workDir（进度 / 回包写这里）：{workDir}");
        sb.AppendLine($"- canvas：{spec.Width}×{spec.Height} · {spec.Fps}fps · 引擎 {Workbench.RenderEngines.Label(spec.Engine)}");
        sb.AppendLine($"- backgroundImage（file:/// URI）：{spec.BackgroundUri}");
        sb.AppendLine($"- templateDir（动效模板库，能复用就复用）：{spec.TemplateDir}");
        sb.AppendLine();

        sb.AppendLine("# 原实现在哪（★ 先 Read 它，在它基础上改）");
        sb.AppendLine();
        for (int k = 0; k < cands.Count; k++)
            sb.AppendLine($"{(k == 0 ? "- 【优先】" : "- 【备选】")}{cands[k]}");
        sb.AppendLine();
        sb.AppendLine($"按上面的顺序找第一个存在的文件，那就是这一镜现在的实现。");
        sb.AppendLine($"它引用的相对资源（底图 / GSAP / 配图）应该在：{assetsDir}");
        sb.AppendLine($"（同样按需自己看，别猜。）");
        sb.AppendLine();
        sb.AppendLine("参考产物：");
        sb.AppendLine($"- 现在的成片：{video}   ← 用户不满意的那一版");
        sb.AppendLine($"- 覆盖它之前先另存为：{prev}");
        sb.AppendLine($"- 缩略图（渲完更新它）：{ThumbPath(renderStageDir, i)}");
        sb.AppendLine();

        sb.AppendLine("# 这一镜原本的分镜设计（用户当初审过的，别丢了）");
        sb.AppendLine();
        sb.AppendLine($"- 口播文案：{shot.Text}");
        sb.AppendLine($"- 画面构思：{(string.IsNullOrWhiteSpace(shot.Description) ? "（当时没写，按文案处理）" : shot.Description)}");
        sb.AppendLine($"- 动效：{(string.IsNullOrWhiteSpace(shot.Motion) ? "（当时没写）" : shot.Motion)}");
        if (!string.IsNullOrWhiteSpace(shot.Image))
            sb.AppendLine($"- 配图需求：{shot.Image}");
        if (!string.IsNullOrWhiteSpace(shot.ImageFile))
            sb.AppendLine($"- 这一镜实际用的图：{shot.ImageFile}");
        sb.AppendLine();

        // ★ 用户在界面上直接改过的字段 —— 改了就是改了口径，必须写进任务
        if (edits != null && edits.Count > 0)
        {
            sb.AppendLine("# 用户在审阅台里直接改过的字段（以这里为准）");
            sb.AppendLine();
            foreach (var kv in edits)
                sb.AppendLine($"- {kv.Key} → {kv.Value}");
            sb.AppendLine();
            sb.AppendLine("这几个字段是用户当前想要的最终值（覆盖上面的\"原本的分镜设计\"）。");
            sb.AppendLine();
        }

        sb.AppendLine("# ★★ 用户这一次的修改意见（最高优先级，原样照办）");
        sb.AppendLine();
        if (string.IsNullOrWhiteSpace(userNote))
        {
            sb.AppendLine("（用户没写修改意见 —— 那就是\"这一镜哪儿不对但说不清\"。）");
            sb.AppendLine();
            sb.AppendLine("这种时候**不要瞎改**：把这一镜重新渲一遍，但只做**保守的打磨**——");
            sb.AppendLine("检查有没有明显的毛病：文字是否压边/超框、元素是否偏出安全区、");
            sb.AppendLine("动效有没有突兀的跳变、首尾帧能不能自然接上前后镜。");
            sb.AppendLine("只修这些确定是问题的地方，**不要改设计**，并在回包里说明你修了什么。");
        }
        else
        {
            sb.AppendLine("```");
            sb.AppendLine(userNote.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("这段意见跟\"原本的分镜设计\"冲突时，**以这段为准**。");
        }
        sb.AppendLine();
        sb.AppendLine("用户的意见可能只说现象、不说做法（\"这一镜太素了\"、\"看不清字\"、\"节奏怪\"）——");
        sb.AppendLine("你要自己判断落点（加层次？加大字号？挪动效节奏？），改完在回包里讲清你怎么理解的。");

        return sb.ToString();
    }
}
