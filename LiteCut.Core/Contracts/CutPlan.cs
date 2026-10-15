// ============================================================
// CutPlan.cs - 粗剪决策契约（流水线第 3 环的产物）★ 流水线枢纽
// ============================================================
//
// 这是整个工作台最重要的一份文件格式。它承载三件事：
//
//   1. AI 的判断  —— "哪几段留、哪几段剪、为什么"
//   2. 用户的审核  —— "我同意 / 我改一下"
//   3. 下游的输入  —— 工作台据此建时间轴；导出器据此生成成片
//
// 【核心设计：只描述"源侧",不描述"时间轴"】
//
//   每个 Segment 说的是"源音频的 [a, b) 秒"怎么处理，
//   而不是"放在时间轴第几秒"。
//
//   为什么？因为时间轴位置是可以算出来的——把 Keep 段按顺序
//   首尾相接，累加即可（见 ComputeTimeline）。
//   让 AI 去算时间轴位置，等于让它做算术，纯属给它添乱。
//
//   一句话：AI 只管"剪哪儿"，"排哪儿"由程序算。
//
// 【为什么被剪掉的段也要记下来】
//
//   Drop 段不是删除，而是"留痕"。好处有三：
//     - 用户能在界面上反悔（"这段气口其实我想留着"）
//     - 能统计"AI 剪了多少、因为什么剪的"
//     - 出问题时能追溯"它当时为什么这么判"
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiteCut.Core.Contracts;

/// <summary>
/// 一份完整的粗剪决策。
/// </summary>
public class CutPlan
{
    /// <summary>契约版本号。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>这份决策属于哪个项目（工作台用它把文件归位）。</summary>
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    /// <summary>源音频的信息（用来核对"这份决策对的是哪个音频"）。</summary>
    [JsonPropertyName("source")]
    public SourceInfo Source { get; set; } = new SourceInfo();

    /// <summary>生成时用的判定参数（记下来，方便复现和对比）。</summary>
    [JsonPropertyName("params")]
    public CutParams Params { get; set; } = new CutParams();

    /// <summary>
    /// 全片的切分列表。按时间顺序、首尾相接、完整覆盖 0 ~ 音频结束。
    ///
    /// 相邻两段的边界一定是咬合的：
    ///   Segments[i].SourceEndSec == Segments[i+1].SourceStartSec
    ///
    /// 这样"剪掉了什么"就是显式的——不用去算空隙。
    /// </summary>
    [JsonPropertyName("segments")]
    public List<CutSegment> Segments { get; set; } = new List<CutSegment>();

    /// <summary>统计数字（界面上显示"剪掉了 61 秒"这类信息）。</summary>
    [JsonPropertyName("stats")]
    public CutStats Stats { get; set; } = new CutStats();

    /// <summary>人工审核状态。</summary>
    [JsonPropertyName("review")]
    public ReviewState Review { get; set; } = new ReviewState();

    /// <summary>生成时间（ISO 8601 格式，如 "2026-09-28T17:40:00+08:00"）。</summary>
    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";

    /// <summary>做决策的模型名（如 "claude-sonnet-4.5"）。换模型后可以对比效果。</summary>
    [JsonPropertyName("generatedBy")]
    public string GeneratedBy { get; set; } = "";

    /// <summary>
    /// 本轮粗剪走了哪条路：波形轨 / 双轨。
    ///
    /// 记下来有两个用处：
    ///   ① 用户能知道自己有没有因为缺转写而损失了能力
    ///   ② 换片子对比效果时能解释"为什么这两次剪得不一样"
    /// </summary>
    [JsonPropertyName("mode")]
    public CutMode Mode { get; set; } = CutMode.WaveformOnly;

    /// <summary>
    /// 转写稿的校验结果。没有转写稿时也存在（Available = false）。
    /// 界面上据此告诉用户"为什么这次没用上语义层"。
    /// </summary>
    [JsonPropertyName("transcript")]
    public TranscriptCheck Transcript { get; set; } = new TranscriptCheck();

    /// <summary>
    /// ★「对稿」结果（T-28）。拿你写的 script.md 当标准答案，跟录音转写稿对一遍。
    ///
    /// 【它和 Transcript 是两件事，别混】
    ///   Transcript（转写校验）：转写稿**配不配得上这条音频**（时间码对不对）。
    ///   Script（对稿）      ：录音**说没说稿子上的话**（内容对不对）。
    ///
    ///   前者是"物料自检"，后者是"内容比对"，回答的问题完全不同。
    ///   没有稿子时也存在（Available = false），界面上据此说明"对稿没启用"。
    /// </summary>
    [JsonPropertyName("script")]
    public ScriptCheck Script { get; set; } = new ScriptCheck();

    /// <summary>
    /// 发现了但**不敢自动剪**的地方。
    ///
    /// 这是"拿不准就保留"这条要求的落点：
    /// 检测出来但把握不够的位置，不直接放进 Segments 删掉，
    /// 而是列在这里让用户在界面上逐个勾选。
    /// 勾上的会在下一次生成时间轴时变成真正的 Drop。
    /// </summary>
    [JsonPropertyName("suggestions")]
    public List<CutSuggestion> Suggestions { get; set; } = new List<CutSuggestion>();

    // ============================================================
    // 派生计算
    // ============================================================

    /// <summary>
    /// 把 Keep 段首尾相接，算出它们在时间轴上的位置。
    ///
    /// 这就是"AI 只管剪哪儿，排哪儿交给程序"的那一步。
    /// 返回的列表直接能喂给工作台建 TimelineClip。
    /// </summary>
    public List<TimelinePlacement> ComputeTimeline()
    {
        var result = new List<TimelinePlacement>();

        // 按源时间排序，确保顺序正确（防御 AI 输出乱序）
        var ordered = new List<CutSegment>(Segments);
        ordered.Sort((a, b) => a.SourceStartSec.CompareTo(b.SourceStartSec));

        double cursor = 0;   // 时间轴游标：下一个 Keep 段该放在哪
        foreach (var seg in ordered)
        {
            if (seg.Action != CutAction.Keep) continue;

            double dur = seg.SourceEndSec - seg.SourceStartSec;
            if (dur <= 0.0001) continue;   // 忽略零长度（防御坏数据）

            result.Add(new TimelinePlacement
            {
                SegmentId = seg.Id,
                SourceStartSec = seg.SourceStartSec,
                SourceEndSec = seg.SourceEndSec,
                TimelineStartSec = cursor,
                TimelineEndSec = cursor + dur,
                Text = seg.Text,
            });

            cursor += dur;
        }

        return result;
    }

    /// <summary>
    /// 粗剪后的总时长（秒）。只算 Keep 段。
    /// </summary>
    [JsonIgnore]
    public double FinalDurationSec
    {
        get
        {
            double total = 0;
            foreach (var seg in Segments)
            {
                if (seg.Action == CutAction.Keep && seg.SourceEndSec > seg.SourceStartSec)
                    total += seg.SourceEndSec - seg.SourceStartSec;
            }
            return total;
        }
    }

    /// <summary>
    /// 自检：找出这份决策里可疑的地方。
    /// 返回空列表 = 没问题。
    ///
    /// 为什么要自检？AI 输出的 JSON 结构合法，不代表内容合理——
    /// 它可能给出重叠的区间、超出音频长度的边界、或者漏掉一整段。
    /// 这些必须在建时间轴之前抓出来，否则会做出音画错位的片子。
    /// </summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (Segments.Count == 0)
        {
            problems.Add("segments 为空——AI 没有给出任何切分");
            return problems;
        }

        var ordered = new List<CutSegment>(Segments);
        ordered.Sort((a, b) => a.SourceStartSec.CompareTo(b.SourceStartSec));

        // 1. 边界必须落在音频范围内
        foreach (var seg in ordered)
        {
            if (seg.SourceStartSec < -0.001)
                problems.Add($"[{seg.Id}] 起点为负：{seg.SourceStartSec:F3}s");
            if (seg.SourceEndSec > Source.DurationSec + 0.05)
                problems.Add($"[{seg.Id}] 终点超出音频长度：{seg.SourceEndSec:F3}s > {Source.DurationSec:F3}s");
            if (seg.SourceEndSec <= seg.SourceStartSec)
                problems.Add($"[{seg.Id}] 区间非法（终点不大于起点）");
        }

        // 2. 相邻两段应该咬合，不能重叠也不能留缝
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            double gap = ordered[i + 1].SourceStartSec - ordered[i].SourceEndSec;
            if (gap > 0.05)
                problems.Add($"第 {i} 段与第 {i + 1} 段之间有空隙 {gap:F3}s（应为 0）");
            else if (gap < -0.05)
                problems.Add($"第 {i} 段与第 {i + 1} 段重叠 {-gap:F3}s（应为 0）");
        }

        // 3. 至少要留下一点内容，否则成片是空的
        if (FinalDurationSec < 1.0)
            problems.Add("把所有内容都剪光了（剩余不足 1 秒）——大概率是判定出错");

        return problems;
    }
}

/// <summary>
/// 源音频的信息。
/// </summary>
public class SourceInfo
{
    /// <summary>音频文件路径。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>总时长（秒）。</summary>
    [JsonPropertyName("durationSec")]
    public double DurationSec { get; set; }

    /// <summary>采样率（如 48000）。</summary>
    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; set; }

    /// <summary>声道数（1 = 单声道，2 = 立体声）。</summary>
    [JsonPropertyName("channels")]
    public int Channels { get; set; }

    /// <summary>
    /// 音频文件的 SHA256（可选）。
    /// 用来防止"决策文件配错了音频"——如果音频被换过，哈希就对不上。
    /// </summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// 判定参数。这三个数就是工作台上那三个滑动条的默认值。
/// </summary>
public class CutParams
{
    /// <summary>静音超过多少秒算"气口"（默认 1.5 秒）。调小剪得狠，调大留得多。</summary>
    [JsonPropertyName("silenceMinSec")]
    public double SilenceMinSec { get; set; } = 1.5;

    /// <summary>停顿短于多少秒一律不剪（默认 0.6 秒）。防止把正常语气的停顿剪碎。</summary>
    [JsonPropertyName("fillerMaxSec")]
    public double FillerMaxSec { get; set; } = 0.6;

    /// <summary>同一句重复几次算"卡壳"（默认 2 次）。</summary>
    [JsonPropertyName("repeatMinCount")]
    public int RepeatMinCount { get; set; } = 2;

    /// <summary>判断重复的时间窗口（默认 12 秒）。超出这个距离的重说不算卡壳。</summary>
    [JsonPropertyName("repeatWindowSec")]
    public double RepeatWindowSec { get; set; } = 12.0;

    /// <summary>剪掉气口后保留多少余量（默认 0.4 秒）。全剪掉会显得急促。</summary>
    [JsonPropertyName("breathKeepSec")]
    public double BreathKeepSec { get; set; } = 0.4;

    /// <summary>切点前留白（默认 0.15 秒）。防止切在字头上。</summary>
    [JsonPropertyName("leadPadSec")]
    public double LeadPadSec { get; set; } = 0.15;

    /// <summary>切点后留白（默认 0.25 秒）。防止切在字尾上。</summary>
    [JsonPropertyName("tailPadSec")]
    public double TailPadSec { get; set; } = 0.25;

    /// <summary>短于多少秒的 Keep 段不值得保留（默认 0.5 秒），会并入相邻段。</summary>
    [JsonPropertyName("minKeepSec")]
    public double MinKeepSec { get; set; } = 0.5;
}

/// <summary>
/// 一段（一段连续的源音频区间）。
/// 全片被切成若干段，每段标记 Keep 或 Drop。
/// </summary>
public class CutSegment
{
    /// <summary>段 ID，如 "s0001"。用于界面上引用、以及用户反悔时定位。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>源音频里的起点（秒）。</summary>
    [JsonPropertyName("sourceStartSec")]
    public double SourceStartSec { get; set; }

    /// <summary>源音频里的终点（秒）。</summary>
    [JsonPropertyName("sourceEndSec")]
    public double SourceEndSec { get; set; }

    /// <summary>保留还是剪掉。</summary>
    [JsonPropertyName("action")]
    public CutAction Action { get; set; } = CutAction.Keep;

    /// <summary>为什么这么判。</summary>
    [JsonPropertyName("reason")]
    public CutReason Reason { get; set; } = CutReason.Normal;

    /// <summary>AI 对这个判断的把握（0~1）。低把握的段会在界面上高亮，提醒用户重点看。</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 1.0;

    /// <summary>这段说的话（来自转写稿）。用户审核时主要靠它判断"该不该删"。</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>补充说明（比如"与第 12 段重复"）。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>用户是否动过这一段（改过 Action 或边界）。界面上会标记出来。</summary>
    [JsonPropertyName("userEdited")]
    public bool UserEdited { get; set; }

    /// <summary>这段的时长（秒）。</summary>
    [JsonIgnore]
    public double DurationSec => SourceEndSec - SourceStartSec;
}

/// <summary>
/// 本轮粗剪用的是哪条路。
///
/// 【为什么分成两条轨】
///   文本轨依赖转写稿，而转写稿可能来自本地 whisper，也可能来自外部 Agent，
///   甚至可能压根没有。任何一环掉了，第二条轨（波形）都必须能独立跑完流程。
///   这不是为了"优雅降级"这种虚的理由 ——
///   而是为了保证：**whisper 挂了、卸载了、换电脑了，这套流程照样能出片。**
/// </summary>
public enum CutMode
{
    /// <summary>只有波形轨：静音 + 波形级重复检测。</summary>
    WaveformOnly = 0,

    /// <summary>双轨：波形层 + 语义层（填充词、半截重说、口吃、整句重复）叠加。</summary>
    WaveformAndText = 1,
}

/// <summary>
/// 转写稿的可用性检查结果。
///
/// 为什么连"没有转写稿"这种情况也要有对象：
///   界面上要显示"本次未使用语义层，原因：没有转写稿"，
///   而不是让用户面对一个空空荡荡的结果猜原因。
/// </summary>
public class TranscriptCheck
{
    /// <summary>拿到了转写稿没有。</summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    /// <summary>最后有没有真的用它（通过了校验才算）。</summary>
    [JsonPropertyName("used")]
    public bool Used { get; set; }

    /// <summary>来源（"external-agent" / "whisper-local" / ""）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>校验之后的综合分数（0~1）。没校验过填 -1。</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; } = -1;

    /// <summary>有多少比例的句子确实对应着人声。</summary>
    [JsonPropertyName("voicedRatio")]
    public double VoicedRatio { get; set; }

    /// <summary>检测到并已校正的整体时间偏移（秒）。</summary>
    [JsonPropertyName("globalShiftSec")]
    public double GlobalShiftSec { get; set; }

    /// <summary>发现的问题（界面上逐条显示给用户）。</summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>
/// ★「对稿」结果（T-28）。
///
/// 【对稿是什么】
///   你写了一份 script.md，然后照着它录音。录音的时候难免口误、卡壳、重说。
///   对稿 = 把"稿子上怎么写的"和"你实际怎么说的"按顺序对一遍，
///   于是就能回答三个问题：
///
///     ① 稿子上有、但你没说的（忘词 / 跳过了）        → 偏差 NotSpoken
///     ② 你说了、但稿子上没有的（临场发挥 / 加词）      → 偏差 AdLib
///     ③ 同一句稿子你录了好几遍（前几遍是废片）        → 偏差 Retake
///
/// 【它凭什么能提升粗剪】
///   光看录音，"这句到底是重录、还是有意的修辞重复"很难分清。
///   有了稿子就有了标准答案：**稿子上只出现一次的那句话，你要是说了两遍，
///   那前一遍必然是废片。** 这条判据比纯文本重录检测稳得多。
///
/// 【永远降级】
///   对不上就是对不上 —— 稿子改成别的版本了、或者压根没稿子，
///   它就什么都不动，粗剪退回原来的做法（下限 = 今天）。
/// </summary>
public class ScriptCheck
{
    /// <summary>有没有找到稿子（script.md 存在且非空）。</summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    /// <summary>对稿结果到底有没有被采纳（对齐质量过线才算）。</summary>
    [JsonPropertyName("used")]
    public bool Used { get; set; }

    /// <summary>稿子路径（界面上显示"用的是哪一份"）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>从稿子里拆出多少句。</summary>
    [JsonPropertyName("sentenceCount")]
    public int SentenceCount { get; set; }

    /// <summary>其中有多少句在录音里找到了对应（说出去了）。</summary>
    [JsonPropertyName("alignedCount")]
    public int AlignedCount { get; set; }

    /// <summary>录音里"有话说、但稿子上没有"的片段数（临场发挥）。</summary>
    [JsonPropertyName("adLibCount")]
    public int AdLibCount { get; set; }

    /// <summary>对稿认出来的重录处数（同一句录了好几遍）。</summary>
    [JsonPropertyName("retakeCount")]
    public int RetakeCount { get; set; }

    /// <summary>
    /// 对齐率：AlignedCount / SentenceCount（0~1）。
    /// 太低说明稿子和录音对不上（譬如稿子改过、或拿错了文件）→ 自动退回纯音频做法。
    /// </summary>
    [JsonPropertyName("alignmentRatio")]
    public double AlignmentRatio { get; set; }

    /// <summary>发现的问题（界面上逐条显示给用户）。</summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>偏差清单（界面上"对稿偏差（需你看）"那个列表就读它）。</summary>
    [JsonPropertyName("deviations")]
    public List<ScriptDeviation> Deviations { get; set; } = new List<ScriptDeviation>();
}

/// <summary>对稿发现的一处偏差。给人看的一句话，不做机器决策。</summary>
public class ScriptDeviation
{
    /// <summary>偏差类型：<c>notSpoken</c>（稿上有没录）/ <c>adLib</c>（录了稿上没有）/ <c>retake</c>（同一句录了多遍）。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>对应源音频的起点（秒）。"稿上有没录"这类没有确切时间，给 0。</summary>
    [JsonPropertyName("sourceStartSec")]
    public double SourceStartSec { get; set; }

    /// <summary>对应源音频的终点（秒）。</summary>
    [JsonPropertyName("sourceEndSec")]
    public double SourceEndSec { get; set; }

    /// <summary>稿子上怎么写的那一句。</summary>
    [JsonPropertyName("script")]
    public string Script { get; set; } = "";

    /// <summary>你实际说了什么（重录/临场发挥才有）。</summary>
    [JsonPropertyName("spoken")]
    public string Spoken { get; set; } = "";

    /// <summary>相似度（0~1，只对 adLib / retake 有意义）。</summary>
    [JsonPropertyName("similarity")]
    public double Similarity { get; set; }

    /// <summary>一句话说明（界面上直接显示）。</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

/// <summary>
/// 一处"建议你把这段也剪了"的候选 —— 算法发现了，但把握不够自动下手。
///
/// 【为什么不直接删】
///   自动剪错的代价是"内容没了"，而标出来的代价是"用户多点一下"。
///   风险不对称的时候，让默认值偏向保守。
///
/// 【用户怎么用】
///   审核界面会把这些列成一个带勾选框的清单，旁边显示文字证据。
///   用户勾了 → 下一次生成时间轴时就真的剪掉。
/// </summary>
public class CutSuggestion
{
    /// <summary>候选区间的起点（秒）。</summary>
    [JsonPropertyName("sourceStartSec")]
    public double SourceStartSec { get; set; }

    /// <summary>候选区间的终点（秒）。</summary>
    [JsonPropertyName("sourceEndSec")]
    public double SourceEndSec { get; set; }

    /// <summary>认为它是什么问题。</summary>
    [JsonPropertyName("reason")]
    public CutReason Reason { get; set; }

    /// <summary>把握（0~1）。低于自动剪阈值才出现在这里。</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>文字证据（多余的那几个字）。</summary>
    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = "";

    /// <summary>说明。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>时长。</summary>
    [JsonIgnore]
    public double DurationSec => SourceEndSec - SourceStartSec;
}

/// <summary>对一段的处置。</summary>
public enum CutAction
{
    /// <summary>保留，进入成片。</summary>
    Keep = 0,

    /// <summary>剪掉，不进成片（但留痕，可反悔）。</summary>
    Drop = 1,
}

/// <summary>剪掉（或保留）的原因。用户在界面上可以按这个分类筛选。</summary>
public enum CutReason
{
    /// <summary>正常语句。</summary>
    Normal = 0,

    /// <summary>气口——沉默太久。</summary>
    Silence = 1,

    /// <summary>语气词/口水话（"嗯""那个""就是说"）。</summary>
    Filler = 2,

    /// <summary>重复——同一句说了好几遍。</summary>
    Repeat = 3,

    /// <summary>卡顿——说到一半停住，然后又重新说。</summary>
    Stutter = 4,

    /// <summary>口误——说错了重来（"不对，我是说……"）。</summary>
    FalseStart = 5,

    /// <summary>跑题——说了但与主题无关。</summary>
    OffTopic = 6,

    /// <summary>用户手动删的（"这段正式文案我改了"）。</summary>
    Manual = 7,

    /// <summary>
    /// 重录——同一句话录了好几遍，只留最后/最完整的那一遍。
    ///
    /// 【和 Repeat 的区别】
    ///   Repeat  = 隔了一段距离又把同一句话说一遍（修辞性重复也算）
    ///   Retake  = 录制现场的同句多遍重录，前面几遍是废的
    ///
    ///   分开记是因为处理策略不同：Repeat 要谨慎（可能是修辞），
    ///   Retake 则可以大胆删（前面的版本本来就是废片）。
    /// </summary>
    Retake = 8,
}

/// <summary>
/// 统计数字。界面上那个"剪掉 61.4 秒 / 共 42 处"就是从这里来的。
/// </summary>
public class CutStats
{
    /// <summary>粗剪前的原始时长（秒）。</summary>
    [JsonPropertyName("originalSec")]
    public double OriginalSec { get; set; }

    /// <summary>粗剪后的时长（秒）。</summary>
    [JsonPropertyName("finalSec")]
    public double FinalSec { get; set; }

    /// <summary>剪掉了多少秒。</summary>
    [JsonPropertyName("removedSec")]
    public double RemovedSec { get; set; }

    /// <summary>总共切了多少段。</summary>
    [JsonPropertyName("segmentCount")]
    public int SegmentCount { get; set; }

    /// <summary>保留了多少段。</summary>
    [JsonPropertyName("keepCount")]
    public int KeepCount { get; set; }

    /// <summary>剪掉了多少段。</summary>
    [JsonPropertyName("dropCount")]
    public int DropCount { get; set; }

    /// <summary>
    /// 按原因分组的剪掉情况，如 { "silence": 12, "repeat": 3 }。
    /// 用来回答"剪掉的都是些什么"。
    /// </summary>
    [JsonPropertyName("dropByReason")]
    public Dictionary<string, int> DropByReason { get; set; } = new Dictionary<string, int>();
}

/// <summary>人工审核状态。对应流程图里那个橙色节点。</summary>
public class ReviewState
{
    /// <summary>审核进度。</summary>
    [JsonPropertyName("status")]
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    /// <summary>审核时间（ISO 8601）。</summary>
    [JsonPropertyName("reviewedAt")]
    public string? ReviewedAt { get; set; }

    /// <summary>用户在审核时额外删掉了几段。</summary>
    [JsonPropertyName("userDropAdded")]
    public int UserDropAdded { get; set; }

    /// <summary>用户在审核时救回了几段（AI 剪了但我不同意）。</summary>
    [JsonPropertyName("userKeepRestored")]
    public int UserKeepRestored { get; set; }

    /// <summary>用户的备注。</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

/// <summary>审核进度。</summary>
public enum ReviewStatus
{
    /// <summary>等着用户看。工作台停在这里不往下走。</summary>
    Pending = 0,

    /// <summary>用户已确认。可以进入镜头设计。</summary>
    Approved = 1,

    /// <summary>用户打回了（比如"剪太狠了"），需要重新生成。</summary>
    Rejected = 2,
}

/// <summary>
/// 时间轴排布结果（派生数据，不需要 AI 提供）。
/// 一个 Keep 段 = 一个 placement。
///
/// 它跟工作台的 TimelineClip 字段一一对应，转换大约 20 行。
/// </summary>
public class TimelinePlacement
{
    /// <summary>来自哪个 Segment。</summary>
    public string SegmentId { get; set; } = "";

    /// <summary>从源音频的哪一秒开始取。</summary>
    public double SourceStartSec { get; set; }

    /// <summary>取到源音频的哪一秒结束。</summary>
    public double SourceEndSec { get; set; }

    /// <summary>放在时间轴的第几秒。</summary>
    public double TimelineStartSec { get; set; }

    /// <summary>在时间轴上的结束位置。</summary>
    public double TimelineEndSec { get; set; }

    /// <summary>这段的内容（界面上显示用）。</summary>
    public string Text { get; set; } = "";

    /// <summary>这段在时间轴上的长度。</summary>
    public double DurationSec => TimelineEndSec - TimelineStartSec;
}
