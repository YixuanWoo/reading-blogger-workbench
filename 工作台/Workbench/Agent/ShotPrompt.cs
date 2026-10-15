// ============================================================
// ShotPrompt.cs - 分镜这一环：给 Agent 的提示词 + 回包解析
// ============================================================
//
// 【内置提示词为什么写在这里，而不是让用户每次自己敲】
//   用户的要求（2026-09-29）："生成分镜设计的时候，要有个性化要求一个栏目，
//   我填写我对这个视频的特殊要求。然后工作台把我的特别要求，和内置提示词一起传给你。"
//
//   所以提示词 = 内置规则（稳定、每次都一样）+ 用户要求（每次可能不同）。
//   内置部分写死在代码里，用户只要填"这次特殊在哪"就够了 —— 不用每次
//   重复交代"你是分镜师""输出 JSON""长句要动起来"。
//
// 【内置规则里最要紧的一条：长镜头必须"动"】
//   一句一镜头时，长句会撑到十几秒。十几秒盯着一行不动的字幕，看着就呆。
//   所以规则里明确要求：长镜头要设计成**多阶段动效**，让句子内容随着说
//   出来的节奏一层层出现（关键词高亮、要点逐条落下、数据跳数……），
//   而不是把整句压在屏幕上不动。
//
// 【解析器为什么要宽容】
//   Agent 回来的东西可能是标准 JSON，也可能外面裹了一层 ```json 代码块，
//   还可能是"NN｜画面构思"的行格式（老格式）。三种都认 ——
//   让用户为了格式问题重跑一次大模型，是这个环节最容易劝退人的地方。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;

namespace LiteCut.Workbench.Agent;

/// <summary>分镜任务：内置提示词 + 用户个性化要求 + 镜头表。</summary>
public static class ShotPrompt
{
    /// <summary>
    /// 内置系统提示词 —— 稳定不变的那部分规则。
    /// </summary>
    public const string SystemPrompt = """
你是资深短视频分镜师，服务对象是**口播类短视频**（读书、思辨、知识分享这类）。
你的任务是：给每一个镜头设计"画面上放什么、怎么动"。

## 硬规则

1. **只设计画面，不要改文案**。文案列是剪辑后确定下来的口播内容，一个字都不能动。
2. **每个镜头一个画面主张**。一句话说清这个镜头"出现什么、怎么出现"。
   不要写"可以考虑……也可以……"这种双面描述，下游要的是一个确定方案。
3. **画面必须能渲染出来**。允许的素材：文字卡、要点列表、数据图、图标、
   半透明色块与渐变遮罩、简单的几何动效、**网络图片**、**用户给的视频素材**。
   禁止出现：具体明星脸、受版权保护的剧照、需要你实地拍摄的画面。
   ★ 画面是"叠在统一底图上的一层"，不是"自己带一张全屏底图" ——
   具体底子见文末「这次的画面规格」。
4. **不要写镜头语言术语堆砌**（"推拉摇移""特写"这类），要写"观众看到什么"。
5. ★ **标了「插片」的镜头没有台词**（文案列为空）：
   不要替它编一句文案，"画面构思"照常写完整 —— 没有声音的时候画面就是全部内容。
   它允许用视频素材（表里有 videoFile 的），也可以不用素材做纯画面。
   ★ 插片**默认成片静音**；用户可以在分镜卡上给某一段开「素材原声」，
   开了之后那一段会带上素材自己的声音（音量可调）—— 这不是你在这里要处理的事，
   写"动效"时知道有这种可能即可（别把画面设计成"必须靠人声才成立"）。
6. 输出**严格 JSON**，不要 Markdown 代码块，不要任何解释文字。

## 可以用网络图片（重要）

这个工作流**允许使用网络上的图片素材**：需要一张现成的图时（书籍封面、历史照片、
地图、示意图、数据截图、人物肖像、场景照片……），直接在 image 字段里写清楚
"要一张什么样的图"，下游会真的去搜图下载进素材库，渲染时直接用。

写 image 的时候，两件事一起说清：
1. **什么样的图** —— 主体 + 风格 + 用途。例如"一本摊开的古籍特写，暖色调"
   "19 世纪欧洲城市街景的黑白版画""一张世界地图，亚欧大陆居中"。
2. **用什么动效展示它** —— 图片不能干巴巴贴在那儿，要说明它怎么出现、怎么动。
   例如"从右侧缓慢推入，随口播报出年份时轻微放大""先模糊后聚焦，说到人名时
   图左下角弹出姓名条""作为背景缓慢平移（Ken Burns），前景压文字卡"。

★ image 写的是**画面需求**，不是文件名也不是 URL —— 下游按它去搜图。
★ 只在"确实需要一张图"时才写 image。纯文字卡、纯几何动效的镜头把 image 留空
  （空字符串），不要为了凑字段硬塞一张无关的图。
★ 优先选**通用、容易搜到**的图：越具体的稀缺画面越可能搜不到，反而是
  概念示意图（"抽象的数据流动线条""打开的空白书本"）又好搜又安全。

## 长镜头必须"动起来"（很重要）

标记了 **[长镜]** 的镜头时长超过 12 秒。整段字幕顶在屏幕中间一动不动是最差解。
长镜头必须设计成**多阶段动效**，让内容随着口播节奏一层层展开，例如：
- 关键词随说到该词时依次高亮 / 放大 / 换色
- 要点逐条落下（说到第 2 条时第 1 条缩小到角落）
- 数字从 0 跳到目标值，随口播报数同步
- 长句拆成 2~3 个视觉层次，每层次用一次转场衔接
在 motion 字段里写清楚"第几秒出现什么、第几秒变成什么"。

## 输出格式

{"shots":[{"index":1,"visual":"画面构思（一到两句）","motion":"动效设计（长镜必填，短镜可写「轻微缩放」）","image":"需要网络图片时写「什么样的图 + 怎么展示它」；不需要就留空字符串"}]}

index 必须和给你的镜头编号一一对应，一个都不能少。
""";

    /// <summary>
    /// 拼出真正发给 Agent 的系统提示词 = 内置规则 + **这次的成片规格**。
    ///
    /// 【为什么规格要拼进来，不能只写在文档里】
    ///   分镜师如果不知道画面最终会铺在哪张底子上，就会设计出
    ///   "白底黑字的大标题""铺满全屏的深色照片"这类方案 ——
    ///   而实际渲染时，这些都是叠在一张**深蓝网格背景图**上的，
    ///   白底会像贴了张纸，铺满全屏则把底图整个盖掉。
    ///   这些错在分镜稿上完全看不出来，等渲染完才发现，返工成本最大。
    ///
    ///   另一件事是渲染方案：走 HyperFrames（HTML/CSS/GSAP 出 MP4）。
    ///   所以动效必须能用网页技术做出来 —— 否则设计得再漂亮也落不了地。
    /// </summary>
    public static string BuildSystemPrompt(RenderSpec? spec)
    {
        // ★ T-26①（V1.11.3）：《视觉规范.md》注入 —— 画面标准的单一真源，用户改文件即生效。
        //   没读到规范（文件缺失）→ 空串跳过，行为与 V1.11.2 完全一致。
        string standard = VisualStandard.BuildSection();

        var sb = new StringBuilder();
        if (standard.Length > 0)
        {
            sb.Append(standard);
        }
        sb.Append(SystemPrompt);
        if (spec == null) return sb.ToString();
        sb.AppendLine();
        sb.AppendLine("## 这次的画面规格（所有镜头共用，硬约束）");
        sb.AppendLine();
        sb.AppendLine($"- 画幅：**{spec.OrientationText} {spec.Width}×{spec.Height}（{spec.AspectText}）**，{spec.Fps}fps。");
        sb.AppendLine("  画面按横屏构图：主体居中偏上，左右留出安全边距，不要把重要内容压到边缘。");
        sb.AppendLine();
        if (spec.BackgroundExists || !string.IsNullOrWhiteSpace(spec.Background))
        {
            sb.AppendLine($"- **背景：所有镜头统一铺这张底图 —— `{spec.BackgroundName}`**");
            if (!string.IsNullOrWhiteSpace(spec.Background))
                sb.AppendLine($"  （{spec.Background}）");
            sb.AppendLine("  它是一张**深蓝底 + 细网格线**的图，不是纯黑底、也不是白底。所以：");
            sb.AppendLine("  1. 文字和卡片要用**浅色**（米白 / 亮金 / 浅蓝），深灰深黑在这个底子上看不清；");
            sb.AppendLine("  2. 主体做成**压在背景上的卡片或文字块**（圆角卡片、细边框、大留白），");
            sb.AppendLine("     让网格底子透出来 —— 不要用白底/纯色底的大色块把背景糊住；");
            sb.AppendLine("  3. 需要图片时，图片也是**叠在背景上的一个块**（可带金色细边和阴影），");
            sb.AppendLine("     不要设计成铺满全屏的图，那会把统一的底子盖掉，整支片子的视觉就断了；");
            sb.AppendLine("  4. 网格本身可以当成设计语言用：让元素沿网格对齐，或让线条沿着网格走。");
        }
        sb.AppendLine();
        sb.AppendLine($"- **渲染引擎：{RenderEngines.Label(spec.Engine)}**（HTML/CSS/GSAP 编排动效 → 逐镜头出 MP4）。");
        sb.AppendLine("  这意味着你设计的动效**必须能用网页技术实现**。能用：位移、缩放、旋转、透明度、");
        sb.AppendLine("  遮罩/裁剪、描边动画、逐字逐行出现、数字滚动、图片缓慢推拉（Ken Burns）、层叠错峰。");
        sb.AppendLine("  不能用：实拍航拍镜头、三维建模、需要真人出镜的画面 —— 这类写了也渲染不出来。");
        sb.AppendLine();
        sb.AppendLine("  动效还要能给出**可执行的参数感**：在 motion 里写清\"第几秒出现什么、怎么变、变到什么程度\"，");
        sb.AppendLine("  例如\"卡片从下方 40px 上浮 + 淡入，0.6 秒完成\"，而不是笼统的\"优雅地出现\"。");
        sb.AppendLine();
        sb.AppendLine($"- **每镜画面时长 = 该镜时长 + {spec.SafetyFrames} 帧静止尾**（{spec.SafetySec:F2}s @ {spec.Fps}fps）。");
        sb.AppendLine($"  镜头表里的时间列是**音频**的长度：前段要把这段时间铺满，");
        sb.AppendLine($"  最后 {spec.SafetyFrames} 帧（约 {spec.SafetySec:F2} 秒）**保持定格、不放任何新内容** ——");
        sb.AppendLine("  这是给下游拼接留的安全缓冲，成片时会被裁掉。");
        sb.AppendLine($"  ★ 反过来最要命：**该镜时长用完动画还没收住 = 内容被裁掉**。");
        sb.AppendLine($"  所以动画的落点（最后一句词、最后一个数字）必须落在该镜时长之内，再往后才是缓冲。");

        return sb.ToString();
    }

    /// <summary>
    /// 拼出给 Agent 的任务正文。
    /// </summary>
    /// <param name="projectName">项目名（给 Agent 一点背景）。</param>
    /// <param name="requirement">★ 用户填的个性化要求 —— 原样整段插进去，不做任何改写。</param>
    /// <param name="shots">切好的镜头。</param>
    /// <param name="script">成片口播稿（用来告诉 Agent 这是精确稿还是估算稿）。</param>
    /// <param name="spec">成片画面规格（可为空 —— 老调用点/导出纯文本时不必传）。</param>
    public static string BuildUserPrompt(
        string projectName,
        string requirement,
        IReadOnlyList<ShotSpan> shots,
        FinalScript script,
        RenderSpec? spec = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# 视频信息");
        sb.AppendLine();
        sb.AppendLine($"- 项目：{projectName}");
        sb.AppendLine($"- 成片总时长：{WorkbenchState.FormatDuration(script.TotalSec)}（{script.TotalSec:F1} 秒）");
        sb.AppendLine($"- 镜头数：{shots.Count}");
        if (spec != null)
        {
            sb.AppendLine($"- 画幅：{spec.OrientationText} {spec.Width}×{spec.Height}（{spec.AspectText}）· {spec.Fps}fps");
            sb.AppendLine($"- 背景：{spec.BackgroundName}（所有镜头共用的底图）");
            sb.AppendLine($"- 渲染引擎：{RenderEngines.Label(spec.Engine)}");
        }
        sb.AppendLine($"- 口播稿来源：{(script.IsEstimate ? "文案稿估算（没有转写稿，时间可能不准）" : "转写稿与精剪决策求交算出的精确稿")}");
        sb.AppendLine();

        // ★ 用户的个性化要求 —— 原样整段插入，一个字都不改
        if (!string.IsNullOrWhiteSpace(requirement))
        {
            sb.AppendLine("# 这支视频的特殊要求（用户亲笔，优先级最高，与上面的规则冲突时以这里为准）");
            sb.AppendLine();
            sb.AppendLine(requirement.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("# 镜头表");
        sb.AppendLine();
        if (spec != null && spec.SafetyFrames > 0)
            sb.AppendLine($"（表内「时长」= 该镜**音频**时长；渲染时每镜另加 {spec.SafetyFrames} 帧静止尾，规则见系统提示词。）");
        sb.AppendLine("| 镜号 | 起止(秒) | 时长 | 文案 |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var sh in shots)
        {
            string tag = sh.IsLong ? " **[长镜]**" : "";
            sb.AppendLine($"| {sh.Index} | {sh.StartSec:F1}–{sh.EndSec:F1} | {sh.DurationSec:F1}s{tag} | {EscapeCell(sh.Text)} |");
        }

        sb.AppendLine();
        sb.AppendLine("请按系统提示词的要求，输出每个镜头的 JSON 数组。");

        return sb.ToString();
    }

    private static string EscapeCell(string s) =>
        (s ?? "").Replace("|", "丨").Replace("\n", " ").Replace("\r", "");

    // ========================================================
    // 回包解析
    // ========================================================

    /// <summary>Agent 回来的一个镜头的画面设计。</summary>
    public sealed class VisualItem
    {
        public int Index { get; set; }
        public string Visual { get; set; } = "";
        public string Motion { get; set; } = "";

        /// <summary>
        /// 需要网络图片时写"什么样的图 + 怎么用动效展示它"。
        /// 下游会照着它去搜图下载到素材库（渲染环节用）。
        /// </summary>
        public string Image { get; set; } = "";
    }

    /// <summary>
    /// 解析 Agent 的回包。三种格式都认：裸 JSON / 裹在代码块里的 JSON / "NN｜画面" 行格式。
    /// </summary>
    public static List<VisualItem> Parse(string content)
    {
        var res = new List<VisualItem>();
        if (string.IsNullOrWhiteSpace(content)) return res;

        // ① 代码块里的 JSON
        var m = Regex.Match(content, "```(?:json)?\\s*(\\{[\\s\\S]*?\\}|[\\[][\\s\\S]*?[\\]])\\s*```");
        string candidate = m.Success ? m.Groups[1].Value : content.Trim();

        // ② 裸 JSON：从第一个 { 或 [ 截到最后一个 } 或 ]
        int i = candidate.IndexOfAny(new[] { '{', '[' });
        int j = candidate.LastIndexOfAny(new[] { '}', ']' });
        if (i >= 0 && j > i) candidate = candidate.Substring(i, j - i + 1);

        try
        {
            using var doc = JsonDocument.Parse(candidate);
            var root = doc.RootElement;

            JsonElement arr = default;
            bool hasArr = false;

            if (root.ValueKind == JsonValueKind.Array) { arr = root; hasArr = true; }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("shots", out var s))
            { arr = s; hasArr = true; }

            if (hasArr)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    int idx = item.TryGetProperty("index", out var v) && v.TryGetInt32(out int n) ? n : res.Count + 1;
                    string visual = item.TryGetProperty("visual", out var vs) ? vs.GetString() ?? ""
                                   : (item.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "");
                    string motion = item.TryGetProperty("motion", out var mo) ? mo.GetString() ?? "" : "";
                    // 图片需求：字段名各家写法不一（image / img / 图片），都认
                    string image = "";
                    foreach (string key in new[] { "image", "img", "imageQuery", "图片", "配图" })
                    {
                        if (item.TryGetProperty(key, out var im))
                        {
                            image = im.GetString() ?? "";
                            if (image.Length > 0) break;
                        }
                    }

                    res.Add(new VisualItem
                    {
                        Index = idx,
                        Visual = visual.Trim(),
                        Motion = motion.Trim(),
                        Image = image.Trim(),
                    });
                }
            }
        }
        catch
        {
            // JSON 解析不了 → 退回行格式
        }

        // ③ 行格式兜底： "3｜画面构思" 或 "3. 画面构思"
        if (res.Count == 0)
        {
            foreach (string raw in content.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                var lm = Regex.Match(line, "^(\\d+)\\s*[｜|.、:：\\-]\\s*(.+)$");
                if (!lm.Success) continue;

                res.Add(new VisualItem
                {
                    Index = int.Parse(lm.Groups[1].Value),
                    Visual = lm.Groups[2].Value.Trim(),
                });
            }
        }

        return res;
    }
}
