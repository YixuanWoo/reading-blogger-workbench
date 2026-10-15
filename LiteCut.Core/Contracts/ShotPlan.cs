// ============================================================
// ShotPlan.cs - 镜头设计契约（流水线第 6 环的产物）
// ============================================================
//
// 粗剪定稿之后，音频的"最终形态"就固定了：
//   总长 X 秒，每个时间点说的是哪句话，都是确定的。
//
// 这一环就是回答："这 X 秒，画面上放什么？"
//
// 【一个 Shot 是什么】
//   音频时间轴上的一段 [start, end)，配一个视觉呈现。
//   可能是：
//     - 一个 HyperFrames 动效卡片（口播里最常见）
//     - 一段实拍素材
//     - 一个静态图 + 字幕
//
// 【为什么要并行生成】
//   54 个镜头互相独立——镜头 7 长什么样，不影响镜头 8。
//   所以可以同时让多个 Agent 各设计几个，墙钟时间从 54×t 变成 ceil(54/n)×t。
//   实测：3 个任务并行 = 8.2 秒，串行要 24.6 秒，加速 3.0×。
//
// 【谁写】第 6 环：Agent（可并行）
// 【谁读】第 7 环（渲染）、第 8 环（组接成片）
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiteCut.Core.Contracts;

/// <summary>
/// 一份完整的镜头设计。
/// </summary>
public class ShotPlan
{
    /// <summary>契约版本号。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>所属项目。</summary>
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    /// <summary>这份设计对应的音频总长（秒）——粗剪后的长度，不是原始长度。</summary>
    [JsonPropertyName("audioDurationSec")]
    public double AudioDurationSec { get; set; }

    /// <summary>
    /// 风格档案的标识（用了哪套配色/字号/动效规范）。
    /// 换风格时能看出"这批镜头是按哪版规范做的"。
    /// </summary>
    [JsonPropertyName("styleRef")]
    public string StyleRef { get; set; } = "";

    /// <summary>画布规格，如 { "width": 1280, "height": 720, "fps": 30 }。</summary>
    [JsonPropertyName("canvas")]
    public ShotCanvas Canvas { get; set; } = new ShotCanvas();

    /// <summary>镜头列表（按时间顺序、首尾相接、覆盖全片）。</summary>
    [JsonPropertyName("shots")]
    public List<Shot> Shots { get; set; } = new List<Shot>();

    /// <summary>生成时间（ISO 8601）。</summary>
    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = "";
}

/// <summary>画布规格。所有镜头必须统一，否则拼不起来。</summary>
public class ShotCanvas
{
    [JsonPropertyName("width")]
    public int Width { get; set; } = 1280;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 720;

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 30;
}

/// <summary>
/// 一个镜头：音频上的一段 + 配什么画面。
/// </summary>
public class Shot
{
    /// <summary>镜头序号，从 1 开始（对应分镜稿里的编号）。</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>在（粗剪后）音频上的开始时间（秒）。</summary>
    [JsonPropertyName("startSec")]
    public double StartSec { get; set; }

    /// <summary>在音频上的结束时间（秒）。</summary>
    [JsonPropertyName("endSec")]
    public double EndSec { get; set; }

    /// <summary>这段时间里说的话（直接抄自粗剪后的文案，供渲染时排版用）。</summary>
    [JsonPropertyName("script")]
    public string Script { get; set; } = "";

    /// <summary>画面描述——"这个镜头要表达什么"。给人看的，也是给渲染 Agent 的指令。</summary>
    [JsonPropertyName("visualIdea")]
    public string VisualIdea { get; set; } = "";

    /// <summary>用哪个 HyperFrames 模板（如 "card-demo"、"grid-bg"）。</summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>模板参数（键值对）。不同模板认不同参数，所以做成自由字典。</summary>
    [JsonPropertyName("params")]
    public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>();

    /// <summary>渲染出来的视频文件路径（渲染完成后填）。</summary>
    [JsonPropertyName("renderPath")]
    public string? RenderPath { get; set; }

    /// <summary>
    /// 渲染产物的实际时长（秒）。
    /// 重点关注：如果这个数小于 (EndSec - StartSec)，
    /// 组接时会用"定格最后一帧"补齐（实测 50 个镜头都有这个差）。
    /// </summary>
    [JsonPropertyName("renderDurationSec")]
    public double? RenderDurationSec { get; set; }

    /// <summary>渲染状态。</summary>
    [JsonPropertyName("status")]
    public ShotStatus Status { get; set; } = ShotStatus.Planned;

    /// <summary>失败原因（失败时填）。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>尝试了几次（含重试）。用来发现"这个镜头怎么老失败"。</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    /// <summary>这个镜头在音频上应该占多长（秒）。</summary>
    [JsonIgnore]
    public double TargetDurationSec => EndSec - StartSec;
}

/// <summary>镜头的渲染状态。工作台靠它画进度条。</summary>
public enum ShotStatus
{
    /// <summary>已设计，还没开始渲染。</summary>
    Planned = 0,

    /// <summary>正在渲染。</summary>
    Rendering = 1,

    /// <summary>渲染完成，产物就绪。</summary>
    Rendered = 2,

    /// <summary>渲染失败（看 Error 字段）。</summary>
    Failed = 3,

    /// <summary>主动跳过（比如用户觉得这个镜头不需要）。</summary>
    Skipped = 4,
}
