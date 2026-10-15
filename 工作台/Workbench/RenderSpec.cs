// ============================================================
// RenderSpec.cs - 成片画面规格：横屏多少、背景用哪张、拿什么渲染
// ============================================================
//
// 【它解决的是什么问题】
//   用户的频道是固定视觉：所有视频都铺同一张深蓝网格背景图，都出横屏。
//   这件事以前只写在"内容创作工作流"那份文档里 —— 也就是说，
//   它活在人的记忆和提示词里，不在程序里。
//   结果是：渲染这一步到底该铺哪张图、出多大画幅，程序一无所知，
//   分镜设计也不知道画面最终会被放到什么底子上。
//
//   所以把它收成一个明确的规格对象：
//     · 分镜这一步 —— 提示词里写清楚"画面会叠在这张底子上"，
//       Agent 就不会设计出白底黑字、或者铺满全屏把底图盖掉的画面；
//     · 渲染这一步 —— 直接按规格出卡片/出片，不用每次现问用户；
//     · 组接这一步 —— 画幅要跟渲染产物一致，也从这里取。
//
// 【规格的来源与留痕，是两件事，都要】
//   · 来源：全局配置（AppConfig）—— 用户改一次，以后每支视频都跟着走；
//   · 留痕：每个项目 stages 下写一份 render-spec.json ——
//     过几个月回头看这支片子，能知道当时用的到底是哪张图、什么画幅。
//   只有配置没有留痕，将来复现不了；只有留痕没有配置，每支片子都要重设一遍。
// ============================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteCut.Workbench;

/// <summary>渲染方案取值。用常量字符串而不是枚举 —— 配置里看得懂、将来加方案不用改存档。</summary>
public static class RenderEngines
{
    /// <summary>HyperFrames：HTML/CSS/GSAP 编程动效 → 逐镜头 MP4。本工作流采用这套。</summary>
    public const string HyperFrames = "hyperframes";

    /// <summary>Remotion：React 逐帧渲染。留作备选，暂不使用。</summary>
    public const string Remotion = "remotion";

    /// <summary>给人看的名字。</summary>
    public static string Label(string? engine) => engine switch
    {
        Remotion => "Remotion",
        _ => "HyperFrames",
    };
}

/// <summary>
/// 一支成片要用到的画面规格。
/// 是"从配置算出来"的纯数据（不碰磁盘），落盘/读盘由下面的静态方法负责。
/// </summary>
public sealed class RenderSpec
{
    /// <summary>落盘时的文件名（放在项目的 stages/03-shot 与 04-render 下）。</summary>
    public const string FileName = "render-spec.json";

    /// <summary>项目名（渲染工程目录、输出命名都用它）。</summary>
    public string Project { get; set; } = "";

    /// <summary>画幅宽（横屏 1920）。</summary>
    public int Width { get; set; } = 1920;

    /// <summary>画幅高（横屏 1080）。</summary>
    public int Height { get; set; } = 1080;

    /// <summary>帧率。</summary>
    public int Fps { get; set; } = 30;

    /// <summary>
    /// 安全帧 —— 每镜画面尾部的**静止缓冲**（2026-10-01 用户拍板：3 帧）。
    ///
    /// 【它是什么】渲染时长 = 该镜时长（音频长度）+ 安全帧。多出来的尾巴保持定格画面。
    /// 【为什么要它】⑤ 组接时，画面必须裁到"和音频段一模一样长"才能无缝拼接。
    ///   若画面长度恰好等于音频，渲染取整的误差会让部分块比音频短 → 接缝处黑帧/跳变。
    ///   留 3 帧定格尾后，裁剪永远切在静止画面上：既不误伤动画内容，也不用回头补帧。
    /// 【联动铁律】分镜 Agent 必须知道这条 —— 有效内容（台词/关键词/动画落点）要在
    ///   "该镜时长"内收住，最后这几帧不得放新内容。见 ShotPrompt / RenderPrompt。
    /// 【口径】3 帧 @30fps = 0.10s（帧数而非秒数存盘：换帧率时语义不变）。
    /// </summary>
    public int SafetyFrames { get; set; } = 3;

    /// <summary>背景图绝对路径（所有镜头共用的底子）。</summary>
    public string Background { get; set; } = "";

    /// <summary>渲染方案，见 <see cref="RenderEngines"/>。</summary>
    public string Engine { get; set; } = RenderEngines.HyperFrames;

    /// <summary>HyperFrames 工程根（渲染时在这里建 &lt;项目名&gt; 文件夹）。</summary>
    public string EngineRoot { get; set; } = @"D:\HyperFrames";

    /// <summary>HyperFrames 动效模板库。</summary>
    public string TemplateDir { get; set; } = @"D:\HyperFrames\templates";

    /// <summary>镜头产物落哪（项目内，一般是 stages/04-render）。</summary>
    public string OutputDir { get; set; } = "";

    /// <summary>
    /// 项目文件夹（2026-10-01 起传入）。
    /// 【为什么要有它】渲染产物、渲染工作区、背景图全部收进项目文件夹 ——
    ///   软件不再往外部固定目录写东西，整个项目拷走就是完整的。
    /// </summary>
    public string ProjectDir { get; set; } = "";

    // ---------------- 派生 ----------------

    /// <summary>画幅比例，比如 "16:9"。给界面显示用。</summary>
    [JsonIgnore]
    public string AspectText
    {
        get
        {
            int g = Gcd(Width, Height);
            return g <= 0 ? $"{Width}×{Height}" : $"{Width / g}:{Height / g}";
        }
    }

    /// <summary>横屏还是竖屏 —— 本工作流只出横屏，界面照实说。</summary>
    [JsonIgnore]
    public string OrientationText => Width >= Height ? "横屏" : "竖屏";

    /// <summary>背景文件名（可能为空）。</summary>
    [JsonIgnore]
    public string BackgroundName =>
        string.IsNullOrWhiteSpace(Background) ? "（未设）" : Path.GetFileName(Background);

    /// <summary>
    /// 背景图的 file:// URI。写进 HTML 卡片时用这个 ——
    /// 路径里有中文，直接拼字符串浏览器多半能认，但用 Uri 生成才是稳的。
    /// 背景图不存在 / 路径非法时返回空串（调用方回退纯色底）。
    /// </summary>
    [JsonIgnore]
    public string BackgroundUri
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Background)) return "";
            try
            {
                if (!File.Exists(Background)) return "";
                return new Uri(Background).AbsoluteUri;
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>安全帧折合的秒数（3 帧 @30fps = 0.10s）。提示词里给人看的长度用它。</summary>
    [JsonIgnore]
    public double SafetySec => SafetyFrames / (double)(Fps > 0 ? Fps : 30);

    /// <summary>一行话，界面和日志都用它（口径统一，别各处各写一遍）。</summary>
    public string Describe() =>
        $"{OrientationText} {Width}×{Height}（{AspectText}）· {Fps}fps · " +
        $"{RenderEngines.Label(Engine)} · 背景 {BackgroundName}";

    /// <summary>背景图是不是真的在（不在就得提醒，否则渲染出来是纯色底还不明所以）。</summary>
    public bool BackgroundExists =>
        !string.IsNullOrWhiteSpace(Background) && File.Exists(Background);

    // ---------------- 从配置算 ----------------

    /// <summary>
    /// 由全局配置 + 项目信息算出这次的规格。
    ///
    /// 【为什么要传项目名/项目目录】
    ///   渲染产物、渲染工作区都在项目文件夹里；背景图也优先取项目自己的素材。
    ///   规格里带上这些，下一个环节拿到 json 就知道该往哪儿写，不用再问一遍。
    /// </summary>
    public static RenderSpec FromConfig(AppConfig cfg, string projectName, string outputDir = "",
                                        string projectDir = "")
    {
        return new RenderSpec
        {
            Project = projectName ?? "",
            Width = cfg.VideoWidth > 0 ? cfg.VideoWidth : 1920,
            Height = cfg.VideoHeight > 0 ? cfg.VideoHeight : 1080,
            Fps = cfg.VideoFps > 0 ? cfg.VideoFps : 30,
            // 背景图：优先项目自己的素材（素材\公共素材\），没有才退到全局配置。
            // 2026-10-01 起素材不再指向任何外部固定夹。
            Background = ResolveBackground(projectDir, cfg.VideoBackgroundImage),
            Engine = string.IsNullOrWhiteSpace(cfg.RenderEngine) ? RenderEngines.HyperFrames : cfg.RenderEngine,
            EngineRoot = string.IsNullOrWhiteSpace(cfg.RenderAssetRoot) ? @"D:\HyperFrames" : cfg.RenderAssetRoot,
            TemplateDir = string.IsNullOrWhiteSpace(cfg.HyperFramesTemplateDir)
                          ? Path.Combine(cfg.RenderAssetRoot ?? @"D:\HyperFrames", "templates")
                          : cfg.HyperFramesTemplateDir,
            OutputDir = outputDir ?? "",
            ProjectDir = projectDir ?? "",
        };
    }

    /// <summary>
    /// 背景图解析：项目素材优先，全局配置兜底。
    /// 先认标准文件名，再退到「公共素材」夹里的第一张图（用户换了图也能被认出来）。
    /// </summary>
    public static string ResolveBackground(string? projectDir, string? configFallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(projectDir))
            {
                string common = Path.Combine(projectDir, "素材", "公共素材");
                string standard = Path.Combine(common, "grid-bg-deep-blue-grid.jpg");
                if (File.Exists(standard)) return standard;

                if (Directory.Exists(common))
                {
                    string? first = Directory.EnumerateFiles(common)
                        .FirstOrDefault(f =>
                        {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            return ext is ".jpg" or ".jpeg" or ".png";
                        });
                    if (first != null) return first;
                }
            }
        }
        catch
        {
            // 读不了就继续走全局兜底
        }
        return configFallback ?? "";
    }

    /// <summary>
    /// HyperFrames 渲染工作区（HTML 卡片、shots、脚本落这里）。
    /// 2026-10-01 起：&lt;项目夹&gt;\渲染工作区\ —— 渲染产生的文件待在项目肚子里，
    /// 不再往引擎目录（D:\HyperFrames）里建项目子夹。
    /// 老项目没有 ProjectDir 时退回旧口径（&lt;引擎根&gt;\&lt;项目名&gt;）。
    /// </summary>
    public string EngineProjectDir =>
        !string.IsNullOrWhiteSpace(ProjectDir)
            ? Path.Combine(ProjectDir, "渲染工作区")
            : (string.IsNullOrWhiteSpace(Project) ? EngineRoot : Path.Combine(EngineRoot, Project));

    /// <summary>
    /// 渲染产物目录（逐镜头 MP4）：&lt;项目夹&gt;\渲染产物\。ProjectDir 未设时为空串，
    /// 由调用方决定兜底（GenerateTaskPackage 会显式传 outputDir）。
    /// </summary>
    public string ProjectRenderOutputDir => RenderOutputDirOf(ProjectDir);

    /// <summary>
    /// 按项目夹算渲染产物目录（逐镜头 MP4 落哪）—— 「&lt;项目夹&gt;\渲染产物\」这条约定
    /// 的**唯一出处**。
    ///
    /// 【为什么单独立成一个静态方法】
    ///   这条约定原先散落好几处（分镜页写 spec、渲染页找产物、任务包读回兜底），
    ///   各写一遍迟早走偏 —— T-10 正是这么来的：同一个 render-spec.json 的
    ///   OutputDir，分镜页写 stages\04-render、渲染页写 渲染产物。
    ///   统一从这里取，将来改约定只改这一行。projectDir 为空返回空串。
    /// </summary>
    public static string RenderOutputDirOf(string? projectDir) =>
        string.IsNullOrWhiteSpace(projectDir) ? "" : Path.Combine(projectDir, "渲染产物");

    // ---------------- 落盘 / 读回 ----------------

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把规格写到目录下（render-spec.json）。
    /// 写失败不抛 —— 规格丢失不该打断用户当前正在做的事。
    /// </summary>
    public static bool Write(string dir, RenderSpec spec)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(spec, Opts));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读一份规格。读不到/坏了返回 null（调用方回退到配置）。</summary>
    public static RenderSpec? Load(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            string p = Path.Combine(dir, FileName);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<RenderSpec>(File.ReadAllText(p));
        }
        catch
        {
            return null;
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return Math.Abs(a);
    }
}
