// ============================================================
// TranscriptPrompt.cs - 第①步的「转写」：给外部 Agent 的提示词
// ============================================================
//
// 【这一环为什么不由工作台自己干】
//   工作台最早的方案就是这样定的：音频不自己转写，交给外部的 Agent 去做
//   （豆包 / 剪映 / 本机 whisper / WorkBuddy 里的 Agent 都行）。
//   工作台只负责三件事：说清「要什么」「写到哪」「什么格式」，
//   以及稿子拿回来之后的清洗、对齐、粗剪决策。
//
//   这么分有道理：转写要模型、要算力、要联网，而工作台是个单文件 exe，
//   不该把这些依赖背在身上；但转写结果对它又是**唯一**必需的外部输入 ——
//   没有稿子，粗剪只能剪静音，剪不掉口误、重录、卡壳。
//
// 【那为什么还要做「复制提示词」这个按钮】
//   因为这段提示词里有两类信息**每次都不一样**：
//     · 这次要转写的是哪个音频（路径 + 时长）
//     · 稿子该写到哪（项目夹里的固定位置）
//   让用户每次自己敲一遍路径，等于给这条路加了一道毫无意义、还容易写错的门槛；
//   写错的下场是 Agent 把稿子写到别处、工作台满硬盘找不到 ——
//   这类失败纯属自找，必须消掉。
//
// 【提示词里最要紧的一条：要「原始 ASR」，不要「整理后的成稿」】
//   这不是洁癖，是实测出来的差距（《伪史论》658 秒口播）：
//     喂整理后的 49 句 → 成片 486.84s（语义层几乎没干活，只剪出 4 处填充词）
//     喂原始 277 片段  → 成片 430.44s（抓到 17 处重录 + 10 处卡壳）
//   差的这 56 秒，全是算法从"没被提前删掉的废话"里省出来的。
//   反面还有一次教训：219 镜被渲成零文字画面（见用户级规则"不要字幕≠画面不要文字"），
//   说明"Agent 替你做主"这件事在这个项目上反复出问题 —— 宁可把要求写死。
//
// 【提示词格式跟着谁走】
//   JSON 契约以 LiteCut.Core\Contracts\Transcript.cs 与
//   LiteCut.Core\Schemas\transcript.schema.json 为准。
//   改了契约就改这里，两边必须同步。
// ============================================================

using System;
using System.IO;
using System.Text;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench.Agent;

/// <summary>
/// 给外部 Agent 的转写提示词。
///
/// 【为什么是纯静态 + 吃显式参数】
///   提示词的内容只取决于三件事：音频在哪、多长、写到哪。
///   不依赖 WorkbenchState，就能在无头验证里直接断言（不用起 WPF）。
/// </summary>
public static class TranscriptPrompt
{
    /// <summary>约定的文件名。工作台导入对话框也预填这个名字，两边必须一致。</summary>
    public const string FileName = "transcript.json";

    /// <summary>
    /// 稿子该写到哪个目录。
    ///
    /// 【三级优先，一个都不能少】
    ///   ① 开了项目 → 项目夹里的 stages\01-transcript
    ///      （2026-10-01 拍板的"项目自包含"：整个项目夹拷走就是完整的）
    ///   ② 没开项目，但配置里那个老目录真的存在 → 用它
    ///      （老用户的 config.json 里存着历史路径，能用就别打扰他）
    ///   ③ 都不成立 → exe 旁边的 转写稿\
    ///      （★ 这一层是为了兜住"配置里那个路径早就没了"的情况 ——
    ///        Config.TranscriptDir 的历史默认值指向 C 盘一个已删除的临时目录，
    ///        不兜的话提示词会指向一个不存在的地方）
    /// </summary>
    public static string OutputDirOf(AppConfig cfg, ProjectRef? project)
    {
        if (project != null) return project.TranscriptDir;

        if (!string.IsNullOrWhiteSpace(cfg.TranscriptDir) && Directory.Exists(cfg.TranscriptDir))
            return cfg.TranscriptDir;

        return Path.Combine(AppContext.BaseDirectory, "转写稿");
    }

    /// <summary>稿子的完整落盘路径。</summary>
    public static string OutputPathOf(AppConfig cfg, ProjectRef? project)
        => Path.Combine(OutputDirOf(cfg, project), FileName);

    /// <summary>
    /// 生成提示词全文。复制到剪贴板的就是这一段。
    /// </summary>
    /// <param name="sourcePath">要转写的音频/视频的完整路径。空 = 还没加载（会留占位提示）。</param>
    /// <param name="durationSec">音频总时长（秒）。0 = 未知。</param>
    /// <param name="outPath">稿子要写到哪（见 <see cref="OutputPathOf"/>）。</param>
    public static string Build(string sourcePath, double durationSec, string outPath)
    {
        bool hasSource = !string.IsNullOrWhiteSpace(sourcePath);
        string srcText = hasSource
            ? sourcePath
            : "（★ 还没在工作台里加载音频：请把这次要转写的音频路径填在这里）";

        string durText = durationSec > 0
            ? $"{WorkbenchState.FormatDuration(durationSec)}（{durationSec:F3} 秒）"
            : "（未知）";

        // JSON 骨架里路径要转义（反斜杠在 JSON 里是转义符）—— 不转的话
        // Agent 照抄会写出非法 JSON，而它往往不会意识到自己写错了。
        string srcJson = hasSource ? sourcePath.Replace("\\", "\\\\") : "";

        var sb = new StringBuilder();

        sb.AppendLine("把下面这段口播录音转写成「逐句稿」。");
        sb.AppendLine("我这边是视频剪辑工作台，拿到稿子后要拿它判断口误、半截话、重复和卡壳，");
        sb.AppendLine("所以对格式有硬要求 —— 请严格按下面写的来，**不要自由发挥**。");
        sb.AppendLine();

        sb.AppendLine("# 一、要转写的音频");
        sb.AppendLine($"路径：{srcText}");
        sb.AppendLine($"时长：{durText}");
        sb.AppendLine("语言：中文（普通话）");
        sb.AppendLine();

        sb.AppendLine("# 二、稿子写到哪（★ 尽量直接写文件，不要只在对话里回给我）");
        sb.AppendLine(outPath);
        sb.AppendLine("· 目录不存在就自己建出来");
        sb.AppendLine($"· 文件名固定用 {FileName}，不要改名、不要加日期后缀");
        sb.AppendLine("· 编码必须是 UTF-8 **且不带 BOM**（带 BOM 会让下游解析器直接报错）");
        sb.AppendLine("· 如果你不能写本机文件（例如用的是网页版工具），就把 JSON 原文完整贴在回复里，我自己存");
        sb.AppendLine();

        sb.AppendLine("# 三、★ 最要紧的一条：要「原始 ASR」，不要「整理后的成稿」");
        sb.AppendLine();
        sb.AppendLine("我要的是**语音识别的原始输出**，不是一份读起来通顺的文稿：");
        sb.AppendLine();
        sb.AppendLine("· 同一句话录了 4 遍 → 4 遍**都要在文件里**，一遍都不要删");
        sb.AppendLine("· 说到一半卡住又重说 → 前半截和后半截**都要留着**");
        sb.AppendLine("· 口吃、拖音、试录、念错又重念 → **全部保留**");
        sb.AppendLine("· 语气词（嗯 / 啊 / 呃 / 就是 / 那个）→ **保留**，删不删由我的算法决定");
        sb.AppendLine("· 明显的同音错字 → **不要替我改**，宁可留着，下游有对齐和容错流程");
        sb.AppendLine("· 不要合并相邻短句、不要按语义重新分段、不要重排顺序");
        sb.AppendLine();
        sb.AppendLine("为什么这么较真：我剪片子要剪掉的就是上面这些「废话」。");
        sb.AppendLine("如果提前把它们删干净了，我的算法拿到一份清清爽爽的稿子，就只能剪剪静音 ——");
        sb.AppendLine("那不是它的活儿，是它的原料被吃掉了。");
        sb.AppendLine();

        sb.AppendLine("# 四、切句与时间");
        sb.AppendLine();
        sb.AppendLine("· 切句：一个自然的停顿单元算一句，通常 **2~4 秒**一句。");
        sb.AppendLine("  10 分钟的口播大约 **200~350 句**；如果你只切出几十句，多半是被整理过了，请重来。");
        sb.AppendLine("· 时间单位一律是**秒**，浮点数（如 5.52）。");
        sb.AppendLine("  ★ 不要毫秒、不要字符串 \"5.52\"、不要 \"00:05.52\" 这种时间串。");
        sb.AppendLine("· startSec / endSec 必须落在音频范围内，逐句**单调递增、不许倒挂**。");
        sb.AppendLine("· 最后一句的 endSec 应该接近音频总时长。");
        sb.AppendLine("· 遇到整段沉默：跳过就行，不要为沉默造句子，");
        sb.AppendLine("  ★ 也不要把沉默\"填\"进上一句的 endSec —— 那会让一句看起来长得离谱。");
        sb.AppendLine();

        sb.AppendLine("# 五、词级时间码（强烈建议给）");
        sb.AppendLine();
        sb.AppendLine("每一句尽量带上 words 数组（每个词/字的时间）。粗剪要在\"字与字之间\"下刀、");
        sb.AppendLine("字幕要按词切行 —— 没有词级时间码就只能整句进出，精度差一档。");
        sb.AppendLine("中文按字或按词给都可以，但同一个文件里请保持一致。");
        sb.AppendLine();

        sb.AppendLine("# 六、输出格式（JSON，字段名一个都别改）");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"schemaVersion\": \"1.0\",");
        sb.AppendLine("  \"language\": \"zh\",");
        sb.AppendLine("  \"engine\": \"你实际用的引擎名，例如 whisper / funasr / doubao\",");
        sb.AppendLine("  \"model\": \"模型规格，例如 large-v3\",");
        sb.AppendLine($"  \"audioPath\": \"{srcJson}\",");
        // ★ 时长未知时写 0，**不要写一个看着像真的示例数字** ——
        //   Agent 照抄示例值是常事，抄进去一个别的项目的时长比留空还糟。
        sb.AppendLine($"  \"audioDurationSec\": {(durationSec > 0 ? durationSec.ToString("F3") : "0")},");
        sb.AppendLine("  \"fullText\": \"把 segments 里每个 text 按顺序拼起来的全文\",");
        sb.AppendLine("  \"segments\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"index\": 0,");
        sb.AppendLine("      \"startSec\": 5.52,");
        sb.AppendLine("      \"endSec\": 8.9,");
        sb.AppendLine("      \"text\": \"这一句是识别出来的原文\",");
        sb.AppendLine("      \"words\": [");
        sb.AppendLine("        { \"text\": \"这一句\", \"startSec\": 5.52, \"endSec\": 6.4 },");
        sb.AppendLine("        { \"text\": \"是识别出来的原文\", \"startSec\": 6.4, \"endSec\": 8.9 }");
        sb.AppendLine("      ]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("（上面这份只是骨架，别原样照抄 —— 里面的示例文字要换成真正识别出来的内容。");
        sb.AppendLine(" words 确实拿不到时留空数组 [] 也行，但不要瞎编时间。）");
        sb.AppendLine();

        sb.AppendLine("# 七、交付前自查");
        sb.AppendLine();
        sb.AppendLine("[ ] 段数与音频长度匹配（10 分钟 ≈ 200~350 段）");
        sb.AppendLine("[ ] 时间单调递增，没有倒挂");
        sb.AppendLine("[ ] 每段 text 非空；words 里的时间落在该句范围内");
        sb.AppendLine("[ ] 重录、口吃、语气词**还能在 JSON 里找到**（说明没被\"整理\"）");
        sb.AppendLine("[ ] JSON 能被直接解析：无注释、无尾逗号、没有被 Markdown 代码块包在外面");
        sb.AppendLine($"[ ] 文件已经写在 {outPath}，UTF-8 无 BOM");
        sb.AppendLine();
        sb.AppendLine("写完就回一句\"好了，稿子在 <路径>，共 N 句\"。不用把 JSON 全文贴回来。");

        return sb.ToString();
    }
}
