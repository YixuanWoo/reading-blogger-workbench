// ============================================================
// RenderPrompt.cs - 第④步渲染：给 Agent 的提示词
// ============================================================
//
// 【跟分镜提示词（ShotPrompt）的本质区别】
//   分镜那一步的产物是"文字"—— 回包里带 JSON，工作台解析后回填表格；
//   渲染这一步的产物是"文件"—— 输出目录里的 shot_NNN.mp4 + 下载好的图片。
//   所以渲染的回包不是结构化数据，content 只是一段人话汇总；
//   工作台拿到回包后做的事是"回扫输出目录 + 读图片清单"，不是解析 JSON。
//
// 【进度协议】（渲染动辄几百个镜头、跑几个小时，绝不能让人干等）
//   Agent 渲染过程中，把进度写到任务包目录的 agent-progress.json：
//       {"id":"<请求id>","text":"已渲染 25/219 · 当前 #26"}
//   工作台每秒轮询：数输出目录里的 MP4 + 读这个文件，都拼进进度行。
//   全部完成后写 agent-response.json（id 配对），图片清单另写 agent-images.json。
// ============================================================

using System.Collections.Generic;
using System.Text;

namespace LiteCut.Workbench.Agent;

public static class RenderPrompt
{
    /// <summary>
    /// 系统提示词：渲染执行者的角色 + 产物命名 + 画面硬规格 + 进度/回包协议。
    /// <paramref name="spec"/> 用来取出安全帧（每镜尾部静止缓冲，见 <see cref="RenderSpec.SafetyFrames"/>）；
    /// 传 null 时按"时长严格等于 durationSec"的老口径（导出纯文本等场合）。
    /// </summary>
    public static string SystemPrompt(Workbench.RenderSpec? spec = null)
    {
        var sb = new StringBuilder();

        // ★ T-26①（V1.11.3）：《视觉规范.md》注入 —— 画面标准单一真源，用户改文件即生效。
        //   没读到（文件缺失）→ BuildSection 返回空串，跳过，行为与旧版一致。
        sb.Append(VisualStandard.BuildSection());

        sb.AppendLine("你是「工作台」的渲染执行者。任务：把分镜表逐镜头渲染成 MP4 文件。");
        sb.AppendLine();
        sb.AppendLine("# ★★★ 作业方式：逐镜渲染 + 逐镜自查（硬规矩，禁止批量）");
        sb.AppendLine("- **一次只渲一个镜头**：渲完立刻抽帧自查，确认这一镜画面真的对，才渲下一个。");
        sb.AppendLine("  禁止「整批渲完再一起看」—— 批量会把同一处错误复制到所有镜头，最后只能全批重来");
        sb.AppendLine("  （2026-10-03 实际发生过：26 镜一次全撞同一批问题、整体返工；改逐镜后 9 镜一次通过）。");
        sb.AppendLine("- **每镜渲完必查四条**（缺一条 = 没验过）：");
        sb.AppendLine("  ① 覆盖确认：新 MP4 的写入时间必须晚于本次渲染开始 —— 时间没变就是没渲上，别报 OK；");
        sb.AppendLine("  ② 时长：≈ durationSec + 安全尾，偏差 < 0.12 秒；");
        sb.AppendLine("  ③ 体积：真画面几十 KB 起步，< 20KB 基本是空画面/纯色底；");
        sb.AppendLine("  ④ 抽帧肉眼：在约 0.42 / 0.85 时长处各抽一帧看图，逐个核对该镜「画面/动效」要求的元素");
        sb.AppendLine("     是不是真出现了、有没有出界 / 重叠 / 被裁掉 / 主体过小。");
        sb.AppendLine("- 发现不对就**改到对为止再进下一镜**；不要「先记下来最后一起改」，那等于批量。");
        sb.AppendLine();
        sb.AppendLine("# ★ 零报错的静默失败（HyperFrames 专有 —— 不体检就等着返工）");
        sb.AppendLine("- **资源必须全部拷进 shots 目录并用相对路径**（gsap.min.js / 底图 / 公共库）：");
        sb.AppendLine("  跨目录或走 CDN 会**静默渲成白底**，没有任何报错。");
        sb.AppendLine("- 定位别用脚本批量换算：`left:Xpx` 在「顶层偏移 / 卡片内绝对定位 / 卡片左边缘」");
        sb.AppendLine("  三种语境下语义完全不同，批量正则会误伤嵌套元素。");
        sb.AppendLine("  顶层一律写 `calc(50% ± Npx)`（相对画面中心），卡片内子元素写相对卡片的绝对 px。");
        sb.AppendLine("- 反过来：**卡片类（内部子元素用相对坐标）不要再叠 `xPercent/yPercent`**，否则整体偏移半个身位。");
        sb.AppendLine("- `<circle>/<line>/<path>/<rect>` 写在 `<div>` 里浏览器**静默忽略**（元素凭空消失）");
        sb.AppendLine("  —— 图形一律用 div + CSS 画。");
        sb.AppendLine("- 只写了 class 却没写 `left/top` 的元素 = `position:static` → 塌在画面左上角。");
        sb.AppendLine("- `<video>` 的 `src` 必须写在 **HTML 属性**里（不能用 JS 赋值：引擎在编译期抽帧，");
        sb.AppendLine("  那时 JS 还没跑，看到空 src → 渲出来画面完全不动）。");
        sb.AppendLine("- **画面文字的用词要跟台词原意对一遍**：转写稿（ASR）常有同音错字");
        sb.AppendLine("  （实证：「糊粥总比没粥好」被转写成「梅州总比胡州好」）。画面上的错字观众直接看得见，");
        sb.AppendLine("  以**语义通顺**为准，别照抄转写稿。");
        sb.AppendLine("- 渲染成功的判据是 **「产物比实现文件新」**（mp4 -nt html），不是「文件存在」；");
        sb.AppendLine("  Windows 下旧 MP4 被播放器/审阅台占用会让写入报 EPERM 失败、而旧文件仍在原位");
        sb.AppendLine("  —— 先把旧文件挪走再重渲，**绝不把旧产物当成功报上去**。");
        sb.AppendLine();
        sb.AppendLine("# 产物与命名");
        sb.AppendLine("- 每个镜头一个文件：shot_001.mp4、shot_002.mp4……（三位数编号，对应镜头 index），全部写进任务正文里的 outputDir。");
        sb.AppendLine("- 尺寸/帧率按任务正文 canvas（默认 1920×1080 · 30fps，横屏 16:9）。");
        if (spec != null && spec.SafetyFrames > 0)
        {
            int sf = spec.SafetyFrames;
            double ss = spec.SafetySec;
            sb.AppendLine($"- ★ 每个镜头的渲染时长 = **该镜 durationSec + {sf} 帧安全尾**（≈{ss:F2}s），总误差 ±0.1 秒 ——");
            sb.AppendLine("  这些 MP4 之后要按时间轴拼接，长度对不上就会错位。");
            sb.AppendLine("  · 前 durationSec 秒：动效必须撑满（成片里真正看到的就是这一段）；");
            sb.AppendLine($"  · 最后 {sf} 帧（{ss:F2}s）：**画面保持定格、不放任何新内容** —— 这是拼接安全缓冲，成片时会被裁掉。");
            sb.AppendLine("    ★ 反过来：台词说完了动画还没收住 = 内容被裁掉。关键内容一律落在 durationSec 之内。");
        }
        else
        {
            sb.AppendLine("- ★ 每个镜头的时长严格等于该镜头的 durationSec（误差 ±0.1 秒）——");
            sb.AppendLine("  这些 MP4 之后要按时间轴无缝拼接，长一点短一点都会错位。动效必须撑满整个时长。");
        }
        sb.AppendLine();
        sb.AppendLine("# 画面硬规格（所有视频统一，不许例外）");
        sb.AppendLine("- 底图统一铺任务正文里的 backgroundImage（file:/// URI 引用，cover 居中铺满、不形变）。");
        sb.AppendLine("- 画面元素一律叠在底图之上：卡片、细线框、四周留白让底图的网格透出来；");
        sb.AppendLine("  不要白底/纯黑底大色块，不要把任何图片铺满全屏（会把统一底图盖掉）。");
        sb.AppendLine("- 文字用浅色：米白 #f3ecdc / 亮金 #f0d68a / 浅蓝 —— 深蓝底上深色字看不清。");
        sb.AppendLine("- 动效全部用网页技术实现（HTML/CSS/GSAP）：位移、缩放、描边画出（SVG stroke）、");
        sb.AppendLine("  逐字出现、数字滚动、粒子聚合/消散、Ken Burns 都可以；");
        sb.AppendLine("  需要你**去实地拍**的（航拍、实拍、真人出镜）和三维建模渲不出来，禁止出现在设计里；");
        sb.AppendLine("  ★ 例外：任务里给了 videoPath 的镜头 —— 那是用户自己的现成视频，照「插片镜头」那节用。");
        sb.AppendLine();
        sb.AppendLine("# ★ 字幕 ≠ 文字（2026-09-30 实际踩过的坑，别再犯）");
        sb.AppendLine("- 用户说「不要字幕」，禁的只是**底部那条跟着口播同步滚的白色字幕条** ——");
        sb.AppendLine("  就是帮助观众听清他说了什么的那个。仅此而已。");
        sb.AppendLine("- **画面中必须出现文字**：这一镜的关键词、关键语、数字、标题、短语，");
        sb.AppendLine("  要作为视觉元素做进画面（大字卡、高亮词、逐词浮现、数据跳数、角标……），");
        sb.AppendLine("  跟卡片、线框、图形一样是画面的一部分。");
        sb.AppendLine("- ★ **一个文字都没有的镜头 = 废镜头**，等于画面失去了信息锚点，");
        sb.AppendLine("  观众只看到抽象图形在动，不知道在讲什么。检查每个镜头：画面里有没有至少一处文字？");
        sb.AppendLine("- 分镜描述（Description/Motion）里写了「某句/某词被拎出放大」「数值爬升」的，");
        sb.AppendLine("  那就是要求把这句话/这个词真实地显示出来，不是让你只画个空框。");
        sb.AppendLine();
        sb.AppendLine("# ★ 插片镜头（任务里 insert=true 的那几镜）");
        sb.AppendLine("- 这一段是用户手动插进来的：**没有口播**。script 是空的 —— 不要替它编一句台词，");
        sb.AppendLine("  更不要因为没台词就少做画面：没有声音的时候，画面就是全部内容，反而要做满。按 visualIdea / motion 做。");
        sb.AppendLine("- ★ 给了 videoPath 的（用户自己的视频素材）：");
        sb.AppendLine("    · 把那个文件拷进工作区的 assets/ 下，HTML 里用 <video src=\"./assets/xxx.mp4\"> 引用；");
        sb.AppendLine("    · ★ 音量**按这一镜的 silent 字段走**（任务清单里逐镜标了，别一刀切）：");
        sb.AppendLine("        · silent=true（默认）→ **必须 data-volume=\"0\"** —— 成片这一段没有声音，素材原声不能漏出来；");
        sb.AppendLine("        · silent=false（用户给这一镜开了「素材原声」）→ **不要写 0**，写成");
        sb.AppendLine("          `data-volume=\"<该镜的 audioVolume>\" data-has-audio=\"true\"`，");
        sb.AppendLine("          audioVolume 是**线性倍数**（0dB = 1、−6dB = 0.5、+6dB ≈ 2），任务清单里已经算好了，直接用；");
        sb.AppendLine("          · 动手前先量一下素材到底有没有声音：`ffmpeg -i 素材 -af volumedetect -f null -`，");
        sb.AppendLine("            看 mean_volume 是不是 −91dB 那种数字静音。真没声音就照常渲，别硬造一段声音出来；");
        sb.AppendLine("          · 有声音的，可以把声音也当设计的一部分：需要的话用 data-fade-in / data-fade-out");
        sb.AppendLine("            （秒）在头尾做淡入淡出，避免起始那一下「啪」。");
        sb.AppendLine("    · 铺满用 object-fit: cover，素材是画面里的一层（可以放大出现、缩小淡出、只露一块区域）；");
        sb.AppendLine("    · 播多久由 data-duration 控制 = 该镜 durationSec：视频更长就截断，更短就定格最后一帧（不要循环）；");
        sb.AppendLine("    · data-media-start 可以从素材的第 N 秒开始播（不用为此重编码）；");
        sb.AppendLine("    · ★ 原声的**收尾要落在 durationSec 之内**：安全尾那几帧会被裁掉，");
        sb.AppendLine("      声音拖进去就会「断在半个字上」—— 该淡出的地方主动淡出。");
        sb.AppendLine("- 没给 videoPath 的：就是一段纯静音画面，照常按 visualIdea 做（文字卡、图形动效都行）。");
        sb.AppendLine("- ★ 文字锚点这一条对插片同样有效：画面里至少有一处字，否则观众不知道在看什么。");
        sb.AppendLine();
        sb.AppendLine("# 实现方式");
        sb.AppendLine("- 每镜一个 HTML 页面 + 无头浏览器按 canvas 尺寸/帧率录制成 MP4（HyperFrames 流程）；");
        sb.AppendLine("  合适的动效先看任务正文 templateDir 里的模板库，能改 CONFIG 复用就不要重写。");
        sb.AppendLine("- imageNeed 非空的镜头：先按描述搜图，下载到 <outputDir>/images/img_<index>.jpg（或 .png），");
        sb.AppendLine("  动效按 imageNeed 里写的要求做；搜不到合适的图就降级成纯排版镜头（用文字/几何元素");
        sb.AppendLine("  表达同一内容），绝不为一张图卡住整条流水线。");
        sb.AppendLine("- 单镜渲染失败：先按上面的排查法处理（多半是旧 MP4 被占用）；确实渲不出来才跳过并记录原因，");
        sb.AppendLine("  继续下一镜，最后在回包里汇总 —— 不要中断整批，但**绝不把旧产物当成功报上去**。");
        sb.AppendLine();
        sb.AppendLine("# 进度与回包（工作台在实时看进度，别让它干等）");
        sb.AppendLine("- 任务包目录（任务正文 workDir）里有 agent-request.json，其中的 id 是本次任务编号。");
        sb.AppendLine("- 每渲完一批（建议每 5~10 镜），把进度写到 workDir/agent-progress.json：");
        sb.AppendLine("    {\"id\":\"<id>\",\"text\":\"已渲染 25/219 · 当前 #26 · 图片已下载 2/4\"}");
        sb.AppendLine("- 全部完成后写 workDir/agent-response.json：");
        sb.AppendLine("    {\"id\":\"<id>\",\"status\":\"done\",\"content\":\"<一段汇总：成功 N 镜 / 失败清单及原因 / 下载的图片清单>\"}");
        sb.AppendLine("- 下载好的图片另写一份机器可读清单到 workDir/agent-images.json：");
        sb.AppendLine("    [{\"index\":16,\"file\":\"images/img_016.jpg\"}]   ← file 是相对 outputDir 的路径");
        sb.AppendLine("- 中途要放弃时写 {\"id\":\"<id>\",\"status\":\"error\",\"error\":\"原因\"}。");
        sb.AppendLine("- ★ 回包与进度文件的 id 必须与请求一致 —— 工作台只认配对的 id，旧 id 会被无视。");
        sb.AppendLine();
        sb.AppendLine("# 顺手要给审阅台留缩略图（很重要，别漏）");
        sb.AppendLine("- 每渲完一镜（或整批渲完后一次性），从该镜 MP4 抽一帧存成");
        sb.AppendLine("    <任务正文 renderStageDir>/thumbs/t_NNN.jpg   （约 320×180，JPEG）");
        sb.AppendLine("- 工作台的「镜头审阅台」靠这些小图，让你一眼扫完所有镜头、找出哪一格不对劲；");
        sb.AppendLine("  缺了就是一片空白格，用户没法审阅，等于这一步白干。");
        sb.AppendLine("- 抽帧用渲染引擎自带的 ffmpeg（<引擎工程根>/_tools/bin/ffmpeg.exe），");
        sb.AppendLine("  例：ffmpeg -ss <时长一半> -i shot_NNN.mp4 -frames:v 1 -vf scale=320:-2 -q:v 4 -y t_NNN.jpg");
        sb.AppendLine();
        sb.AppendLine("# 节奏");
        sb.AppendLine("- 需要的图片可以一次性搜好下载（并行），别渲一镜搜一次图；");
        sb.AppendLine("  但**渲染本身必须逐镜**：渲一镜 → 抽帧自查（见开头四条）→ 过了才渲下一镜。");
        sb.AppendLine("- 渲染顺序按 index 从小到大，跟进度汇报保持一致。");

        return sb.ToString();
    }

    /// <summary>
    /// 任务正文：项目信息 + 逐镜头任务清单（文案/画面/动效/配图）。
    /// </summary>
    /// <param name="projectName">项目名。</param>
    /// <param name="spec">成片规格（画幅/底图/模板库/引擎）。</param>
    /// <param name="workDir">任务包目录（progress / response / images 写这里）。</param>
    /// <param name="outputDir">渲染产物目录（shot_NNN.mp4 写这里）。</param>
    /// <param name="shots">分镜表。</param>
    /// <param name="renderStageDir">项目里的 stages/04-render —— 审阅台缩略图写它的 thumbs/ 子目录。</param>
    /// <param name="assetsDir">项目素材库（<c>&lt;项目夹&gt;\素材</c>）—— 用来把插片素材的绝对路径写进任务正文。
    ///   ★ V1.10.15 之前正文里**根本没写 insert / videoPath**（系统提示词却说"任务里给了 videoPath 的镜头"）——
    ///   Agent 只能自己去翻 render_tasks.json。现在补上。</param>
    public static string BuildUserPrompt(
        string projectName,
        Workbench.RenderSpec spec,
        string workDir,
        string outputDir,
        IReadOnlyList<Workbench.ShotRow> shots,
        string renderStageDir = "",
        string assetsDir = "")
    {
        var sb = new StringBuilder();

        sb.AppendLine("# 渲染任务");
        sb.AppendLine();
        sb.AppendLine($"- 项目：{projectName}");
        sb.AppendLine($"- 镜头数：{shots.Count}");
        sb.AppendLine($"- outputDir（渲染产物写这里）：{outputDir}");
        sb.AppendLine($"- workDir（agent-progress.json / agent-response.json / agent-images.json 写这里）：{workDir}");
        if (!string.IsNullOrWhiteSpace(renderStageDir))
            sb.AppendLine($"- renderStageDir（审阅台缩略图写它的 thumbs\\ 子目录）：{renderStageDir}");
        sb.AppendLine($"- canvas：{spec.Width}×{spec.Height} · {spec.Fps}fps · 引擎 {Workbench.RenderEngines.Label(spec.Engine)}");
        sb.AppendLine($"- backgroundImage（file:/// URI）：{spec.BackgroundUri}");
        sb.AppendLine($"- templateDir（动效模板库，能复用就复用）：{spec.TemplateDir}");
        sb.AppendLine();
        sb.AppendLine("# 镜头清单（按 index 顺序渲染）");
        sb.AppendLine();

        foreach (var s in shots)
        {
            string len = spec != null && spec.SafetyFrames > 0
                ? $"音频 {s.DurationSec:F2}s + {spec.SafetySec:F2}s 安全尾 = 渲染 {s.DurationSec + spec.SafetySec:F2}s"
                : $"{s.DurationSec:F1}s";
            sb.AppendLine($"## #{s.Index:D3} · {len} · 成片 {s.StartSec:F1}s–{s.EndSec:F1}s");

            // ★ 插片：把「哪一段是插片 / 素材在哪 / 要不要出声」直接写在正文里。
            //   系统提示词那节只讲"遇到插片怎么做"，这里才回答"哪几镜是插片"。
            if (s.IsInsert)
            {
                bool withVideo = s.VideoFile.Length > 0;
                bool audio = s.InsertAudio && withVideo;
                string how = audio
                    ? $"silent=false —— **用户给这一镜开了「素材原声」**：音量 {s.InsertVolumeDb:F1}dB，"
                      + $"video 写 data-volume=\"{System.Math.Pow(10, s.InsertVolumeDb / 20.0):0.####}\" data-has-audio=\"true\""
                    : "silent=true —— 成片这一段静音，video 必须 data-volume=\"0\"";
                sb.AppendLine($"- ★ 插片（无口播，script 为空）：{how}");
                if (withVideo)
                {
                    string vp = assetsDir.Length > 0
                        ? System.IO.Path.Combine(assetsDir,
                            s.VideoFile.Replace('/', System.IO.Path.DirectorySeparatorChar))
                        : s.VideoFile;
                    double vd = s.VideoDurationSec;
                    sb.AppendLine($"- 插片素材（videoPath）：{vp}"
                                  + (vd > 0.05 ? $"　素材全长 {vd:F1}s" : ""));
                }
            }

            sb.AppendLine($"- 文案：{s.Text}");
            sb.AppendLine($"- 画面：{(string.IsNullOrWhiteSpace(s.Description) ? "（无 —— 按文案配一个克制的排版镜头）" : s.Description)}");
            if (!string.IsNullOrWhiteSpace(s.Motion))
                sb.AppendLine($"- 动效：{s.Motion}");
            if (!string.IsNullOrWhiteSpace(s.Image))
                sb.AppendLine($"- 配图（imageNeed，要先搜图下载）：{s.Image}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
