// ============================================================
// PipelineJob.cs - 流水线任务状态
// ============================================================
//
// 这个契约不是给 AI 的，是工作台自己用的：追踪"当前这个活干到哪了"。
//
// 【工作台怎么用】
//   工作台投一条指令给 Agent，同时生成一个 PipelineJob：
//
//     job = new PipelineJob { Stage = Transcribe, Status = Running }
//     落盘 → 界面显示进度条
//     Agent 干完 → 写产物文件 → job.Status = Done
//     工作台轮询到 Done → 推进到下一个 Stage
//
// 【为什么要落盘】
//   工作台可能被关掉、电脑可能重启。
//   落盘的 job 能让程序下次启动时"知道刚才干到哪了"，可以续上。
//
// 【关键字段：AgentRunId】
//   网关返回的 run 标识。用它去查 Agent 是否还在跑。
//   注意：Agent 侧是"串行队列 + 3 并发上限"，
//   所以工作台要控制同时 in-flight 的任务数不超过 3。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiteCut.Core.Contracts;

/// <summary>
/// 一个流水线环节的执行状态。
/// </summary>
public class PipelineJob
{
    /// <summary>任务 ID（工作台自己生成）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>所属项目。</summary>
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    /// <summary>这是流水线的哪一环。</summary>
    [JsonPropertyName("stage")]
    public PipelineStage Stage { get; set; } = PipelineStage.Transcribe;

    /// <summary>当前状态。</summary>
    [JsonPropertyName("status")]
    public PipelineStatus Status { get; set; } = PipelineStatus.Pending;

    /// <summary>进度（0.0 ~ 1.0）。不方便估算时填 0。</summary>
    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>给用户看的一句话，如"正在转写（第 3/8 段）"。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>输入文件路径（如原始音频）。</summary>
    [JsonPropertyName("inputPath")]
    public string InputPath { get; set; } = "";

    /// <summary>产物文件路径（如 cutplan.json）。</summary>
    [JsonPropertyName("outputPath")]
    public string? OutputPath { get; set; }

    /// <summary>失败原因（失败时填）。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// 对应的 Agent 运行标识（网关返回的那个 id）。
    /// 用来查"Agent 那边还在跑吗"，也用来在卡住时取消。
    /// </summary>
    [JsonPropertyName("agentRunId")]
    public string? AgentRunId { get; set; }

    /// <summary>这次任务用哪个 Agent（如 "general-purpose"、"Explore"）。</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    /// <summary>重试了几次。</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    /// <summary>创建时间（ISO 8601）。</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    /// <summary>开始执行时间。</summary>
    [JsonPropertyName("startedAt")]
    public string? StartedAt { get; set; }

    /// <summary>结束时间（成功或失败）。</summary>
    [JsonPropertyName("finishedAt")]
    public string? FinishedAt { get; set; }

    /// <summary>这一环产出的所有文件（可能不止一个，比如转写稿 + SRT 字幕）。</summary>
    [JsonPropertyName("artifacts")]
    public List<string> Artifacts { get; set; } = new List<string>();

    /// <summary>
    /// 超时秒数（默认 10 分钟）。
    /// 工作台超过这个时间还看到 Status = Running，就该判定卡住并取消。
    ///
    /// 为什么必须设：Agent 侧一个 run 如果永远不结束，
    /// 它会一直占着并发名额，导致后面的任务全部被限流。
    /// </summary>
    [JsonPropertyName("timeoutSec")]
    public double TimeoutSec { get; set; } = 600;

    /// <summary>是否还在进行中（界面上决定要不要显示转圈）。</summary>
    [JsonIgnore]
    public bool IsActive => Status == PipelineStatus.Pending || Status == PipelineStatus.Running;

    /// <summary>这个环节是否卡住了（在跑但已超时）。</summary>
    public bool IsStuck()
    {
        if (Status != PipelineStatus.Running) return false;
        if (string.IsNullOrEmpty(StartedAt)) return false;
        if (!DateTimeOffset.TryParse(StartedAt, out var started)) return false;
        return (DateTimeOffset.Now - started).TotalSeconds > TimeoutSec;
    }
}

/// <summary>流水线的环节。顺序就是执行顺序。</summary>
public enum PipelineStage
{
    /// <summary>1. 转写：音频 → 文字 + 时间码。</summary>
    Transcribe = 0,

    /// <summary>2. 粗剪决策：转写稿 → 哪段留哪段剪。</summary>
    CutPlan = 1,

    /// <summary>3. 人工审核：用户确认粗剪结果（工作台停在这里等）。</summary>
    Review = 2,

    /// <summary>4. 镜头设计：定稿的音频 → 每个时间段配什么画面。</summary>
    ShotPlan = 3,

    /// <summary>5. 渲染：逐个镜头出 MP4（可并行）。</summary>
    Render = 4,

    /// <summary>6. 组接：镜头 + 音频 + 字幕 → 成片。</summary>
    Assemble = 5,

    /// <summary>7. 导出：可选，生成 FCP7 XML 交给达芬奇（不做精修时可以跳过）。</summary>
    Export = 6,
}

/// <summary>任务状态。</summary>
public enum PipelineStatus
{
    /// <summary>还没开始。</summary>
    Pending = 0,

    /// <summary>正在跑。</summary>
    Running = 1,

    /// <summary>等用户确认（只有 Review 环节会停在这）。</summary>
    WaitingReview = 2,

    /// <summary>成功完成。</summary>
    Done = 3,

    /// <summary>失败（看 Error 字段）。</summary>
    Failed = 4,

    /// <summary>用户取消。</summary>
    Cancelled = 5,
}
