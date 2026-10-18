// ============================================================
// Transcript.cs - 转写稿契约（流水线第 2 环的产物）
// ============================================================
//
// 一份"谁在第几秒说了什么"的完整记录，细到每个字的时间码。
//
// 【为什么要词级时间码（Words）】
//   1. 粗剪要在"字与字之间"下刀，不能切在词中间
//   2. 字幕要按词切行，否则断句会很难看
//   3. 镜头可以对齐到"某句话说完"的位置
//
// 【两个粒度的区别】
//   TranscriptSegment ≈ 一句话（一个自然的停顿单元，用于阅读）
//   TranscriptWord    ≈ 一个词（最小可切单元，用于下刀和字幕）
//
// 【谁来写这个文件】
//   流水线第 2 环：Agent 拿到音频 → 调转写引擎 → 产出这个
//
// 【谁来读这个文件】
//   第 3 环（粗剪决策）：靠它知道哪几段是"废话"
//   第 6 环（镜头设计）：靠它知道"这句话配什么画面"
//   字幕生成：靠 Word 级时间码切行
// ============================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiteCut.Core.Contracts;

/// <summary>
/// 一份完整的转写稿。
/// </summary>
public class Transcript
{
    /// <summary>契约版本号。格式变了就改这个数，下游可以据此拒绝旧格式。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>语言代码（如 "zh"、"en"）。中文内容固定填 "zh"。</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh";

    /// <summary>转写引擎名（如 "whisper"、"funasr"）。用于排查"这次为什么转得差"。</summary>
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "";

    /// <summary>引擎的模型规格（如 "large-v3"、"medium"）。模型越大越准但越慢。</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>源音频路径。用于核对"这份稿子对的是哪个音频"。</summary>
    [JsonPropertyName("audioPath")]
    public string AudioPath { get; set; } = "";

    /// <summary>音频总时长（秒）。</summary>
    [JsonPropertyName("audioDurationSec")]
    public double AudioDurationSec { get; set; }

    /// <summary>
    /// 整段转写的全文（把所有句子的 Text 拼起来，方便全文搜索和给 AI 看整体）。
    /// </summary>
    [JsonPropertyName("fullText")]
    public string FullText { get; set; } = "";

    /// <summary>逐句列表（按时间顺序）。</summary>
    [JsonPropertyName("segments")]
    public List<TranscriptSegment> Segments { get; set; } = new List<TranscriptSegment>();
}

/// <summary>
/// 一句话（一个自然的停顿单元）。
/// </summary>
public class TranscriptSegment
{
    /// <summary>序号，从 0 开始。用于在界面上定位"第几句"。</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>这句话在音频里的开始时间（秒）。</summary>
    [JsonPropertyName("startSec")]
    public double StartSec { get; set; }

    /// <summary>这句话在音频里的结束时间（秒）。</summary>
    [JsonPropertyName("endSec")]
    public double EndSec { get; set; }

    /// <summary>这句话的文字内容。</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>这句话拆成的一个个词（可能为空——有些引擎不给词级时间码）。</summary>
    [JsonPropertyName("words")]
    public List<TranscriptWord> Words { get; set; } = new List<TranscriptWord>();

    /// <summary>这句话在音频里的时长（秒）。</summary>
    [JsonPropertyName("durationSec")]
    public double DurationSec => EndSec - StartSec;
}

/// <summary>
/// 一个词（最小可切单元）。
/// </summary>
public class TranscriptWord
{
    /// <summary>词本身（中文一般就是一个字或一个词）。</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>这个词在音频里的开始时间（秒）。</summary>
    [JsonPropertyName("startSec")]
    public double StartSec { get; set; }

    /// <summary>这个词在音频里的结束时间（秒）。</summary>
    [JsonPropertyName("endSec")]
    public double EndSec { get; set; }

    /// <summary>
    /// 引擎对"这个词是什么"的把握（0~1）。
    /// 明显偏低（&lt;0.5）的地方，往往是引擎听错了——可以提示用户重点核对这些位置。
    /// 引擎不提供时填空。
    /// </summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}
