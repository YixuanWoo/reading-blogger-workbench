// ============================================================
// ProjectModel.cs - 一个「项目」在磁盘上的样子
// ============================================================
//
// 【为什么单独定义一份"存档结构"，不直接把 WorkbenchState 存下来】
//   WorkbenchState 是运行时状态，里面有解码好的波形、探测结果这类
//   "大且可以从源文件重新算出来"的东西，没必要进存档。
//   而且运行时状态想怎么改就怎么改，存档结构却必须长期稳定 ——
//   一旦改坏，用户以前的项目就打不开了。所以两者分开。
//
// 【向后兼容规矩】
//   1. 加字段 = 安全。老档案没有这个字段 → 读到默认值。
//   2. 删/改字段名 = 不安全。必须 Version +1，并在 ProjectStore 里写迁移。
//   3. 所有集合/字符串默认给空值，不要给 null（读老档案不会炸）。
//
// 【这个文件里只有数据，没有逻辑】
//   读写盘在 ProjectStore，内存搬运在 ProjectSession。
// ============================================================

using System;
using System.Collections.Generic;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;

namespace LiteCut.Workbench.Project;

/// <summary>项目在磁盘上的一份完整状态（= 项目文件夹里的 project.json）。</summary>
public sealed class ProjectModel
{
    /// <summary>当前结构版本</summary>
    public const int CurrentVersion = 1;

    /// <summary>结构版本号。不兼容地改了结构就 +1。</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>项目唯一 Id（改名/移动文件夹都不受影响）</summary>
    public string Id { get; set; } = "";

    /// <summary>项目名（显示用，可以随便改）</summary>
    public string Name { get; set; } = "";

    /// <summary>所属频道，比如「读书博主」。用来给项目分组、给默认目录定位。</summary>
    public string Channel { get; set; } = "";

    /// <summary>状态：active = 进行中；archived = 已归档。见 ProjectStatus。</summary>
    public string Status { get; set; } = ProjectStatus.Active;

    /// <summary>一句话备注（灵感立项时会带过来）</summary>
    public string Note { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public SourcePart Source { get; set; } = new();
    public CutPart Cut { get; set; } = new();
    public FineCutPart FineCut { get; set; } = new();
    public ShotPart Shots { get; set; } = new();
    public OutputPart Output { get; set; } = new();
    public UiPart Ui { get; set; } = new();

    /// <summary>
    /// ★ V1.13.0 归档信息。null = 这个项目从来没归档过。
    ///
    /// 【为什么挂在 project.json 里，而不是另开一个文件】
    ///   归档后本地只剩一个小桩，桩里唯一"永远存在"的文件就是 project.json。
    ///   把归档信息挂在它身上，工作台扫一遍就知道"这个是归档项目、工程在哪"——
    ///   不用再找第二个文件，也就不会出现"桩还在、索引丢了"的半死状态。
    ///
    /// 【⚠ 加字段之后必须同时改 ProjectSession.Projected()】
    ///   那个方法每次保存都会**新建**一个 ProjectModel 再逐个字段搬过去。
    ///   漏搬 = 每存一次盘，归档信息就被抹一次。
    /// </summary>
    public ArchivePart? Archive { get; set; }

    /// <summary>
    /// 走到哪一步了（0~1），项目卡片墙上的进度条用它。
    /// 不存盘也行（能从各段数据推出来），但存下来列表页读起来更快。
    /// </summary>
    public double Progress { get; set; }
}

/// <summary>项目状态取值。用常量字符串而不是枚举，将来加状态不用改存档格式。</summary>
public static class ProjectStatus
{
    public const string Active = "active";
    public const string Archived = "archived";
}

// ============================================================
// 各段
// ============================================================

/// <summary>① 源素材</summary>
public sealed class SourcePart
{
    /// <summary>第①步加载的口播源文件（音频或视频）</summary>
    public string AudioPath { get; set; } = "";

    /// <summary>转写稿来源路径（展示用）</summary>
    public string TranscriptPath { get; set; } = "";

    /// <summary>转写稿本体（外部工具产出的，直接存下来省得再找）</summary>
    public Transcript? Transcript { get; set; }
}

/// <summary>① 粗剪决策</summary>
public sealed class CutPart
{
    /// <summary>粗剪档位：0 激进 / 1 适中 / 2 保守</summary>
    public int Preset { get; set; }

    /// <summary>跑分析时用的参数</summary>
    public AutoCutOptions? Options { get; set; }

    /// <summary>粗剪决策本体（含用户在第①步里逐段手改的 keep/drop）</summary>
    public CutPlan? Plan { get; set; }
}

/// <summary>一个时间轴块的存档</summary>
public sealed class ClipPart
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceName { get; set; } = "";
    public double SourceStartSec { get; set; }
    public double SourceEndSec { get; set; }
    public double SourceTotalSec { get; set; }
    public double TimelineStartSec { get; set; }
    public double VolumeDb { get; set; }
    public double FadeInSec { get; set; }
    public double FadeOutSec { get; set; }
}

/// <summary>② 精剪</summary>
public sealed class FineCutPart
{
    /// <summary>
    /// 【关键】完整的时间轴块列表。
    ///
    /// 这里为什么不是"只存保留区间"：
    ///   以前只把保留区间交回工作台，下次要靠"用区间重新铺一遍"来恢复。
    ///   但你手工挪过的块边界、调过的每块音量、加的淡入淡出，
    ///   一旦被压成"区间"就全没了。
    ///   存完整块列表 → 下次打开就是你上次离开时的样子，一个像素都不差。
    /// </summary>
    public List<ClipPart> Clips { get; set; } = new();

    /// <summary>播放线位置（秒）</summary>
    public double PlayheadSec { get; set; }

    /// <summary>时间轴视图：视口起点（秒）</summary>
    public double ViewStartSec { get; set; }

    /// <summary>时间轴视图：缩放（秒/像素）</summary>
    public double SecPerPixel { get; set; } = 0.05;

    /// <summary>
    /// 【2026-09-29 新增】这份时间轴是剪哪个源文件的。
    /// 打开项目时若和当前源文件对不上，整份时间轴作废（不还原、不参与决策）——
    /// 否则会出现"拿 A 片的区间去剪 B 片"这种最坏情况。
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// 精剪结果是不是**当前生效**的决策（对应 WorkbenchState.HasFineCut）。
    ///
    /// 【语义要认准：这是"谁在生效"，不是"剪没剪过"】
    ///   精剪时间轴本身永远存在 Clips 里，跟这个标记无关 ——
    ///   在工作台里把下游切回粗剪决策只会把这个标记打成 false，
    ///   下次打开照样能看到你上次排好的时间轴。
    ///   （所以别把它当成"有没有精剪过"来判断，那样会误伤用户的工作。）
    /// </summary>
    public bool Applied { get; set; }
}

/// <summary>③ 分镜</summary>
public sealed class ShotPart
{
    /// <summary>降级用的文案稿（没导入转写稿时，口播稿由它按字数占比估算）。</summary>
    public string ScriptText { get; set; } = "";

    /// <summary>
    /// ★ **已交接版**镜头表（V1.1.0 / T-16 起语义收紧）。
    ///   下游 ④ 渲染页 / ⑤ 组接页读的就是这一份 —— 它只装"用户点过
    ///   「应用到下游」的那一版"。编辑中的草稿在 <see cref="DraftRows"/>。
    /// </summary>
    public List<ShotRow> Rows { get; set; } = new();

    /// <summary>
    /// 编辑中的**草稿**镜头表。老档案没有这个字段 → 读出来是空表，
    /// 打开时按"草稿 = 已交接版"处理（向后兼容，见 ProjectSession.ApplyToState）。
    /// </summary>
    public List<ShotRow> DraftRows { get; set; } = new();

    /// <summary>上次交接给下游的时间（人话，如 "2026-10-01 14:20"）。空 = 从没交接。</summary>
    public string CommittedAt { get; set; } = "";

    /// <summary>镜头 ID 发号器：下一个号是 s{NextShotSeq:D2}。只增不减、永不复用。</summary>
    public int NextShotSeq { get; set; }

    /// <summary>
    /// ★ 用户对这支视频的个性化要求。
    ///
    /// 【为什么必须存进档案】
    ///   它是"这次为什么这么设计"的依据。分镜重跑、过几天回来看、
    ///   或者换一支视频复制流程，都要能找回当初写的要求。
    ///   每次调 Agent 时它会原样整段拼进提示词（优先级高于内置规则）。
    /// </summary>
    public string Requirement { get; set; } = "";

    /// <summary>镜头切法：0=一句一镜 1=固定节奏+吸附句边界 2=语义合并（新项目默认）。</summary>
    public int SplitMode { get; set; }

    /// <summary>固定节奏模式下的目标镜头时长（秒）。</summary>
    public double PaceSec { get; set; } = 6.0;

    /// <summary>上次生成分镜时用的口播稿来源："transcript" / "script"（用于界面标注）。</summary>
    public string ScriptSource { get; set; } = "";

    /// <summary>
    /// ★ 算出来的成片口播稿（含用户手工改过的句子）。
    ///
    /// 【为什么要整份存】
    ///   转写稿有错字（豆包 ASR 实测一堆），这一栏是能改的。
    ///   改过的字如果不存进档案，重开项目就全丢了 —— 每开一次都要重改一遍，
    ///   那这个"能改"的口子等于白开。
    ///   存整份而不是只存改动，是为了重开时不用先重算一遍才能显示。
    /// </summary>
    public FinalScript? FinalScript { get; set; }
}

/// <summary>④⑤ 渲染与导出</summary>
public sealed class OutputPart
{
    public string RenderDir { get; set; } = "";
    public string LastExportPath { get; set; } = "";
}

/// <summary>界面记忆（下次打开接着上次的位置）</summary>
public sealed class UiPart
{
    /// <summary>上次停在哪一步（存导航项 Id，而不是序号 —— 菜单顺序将来会变）</summary>
    public string NavId { get; set; } = "";
}

// ============================================================
// ★ V1.13.0 归档（整包搬去移动硬盘，本地只留小桩）
// ============================================================
//
// 【为什么"归档"要留下一份记录】
//   归档之后本地就没有源视频和成片了。如果档案里连一句"它去哪了"都没写，
//   这个项目在工作台里就变成了一个黑洞 —— 看得出名字，点不开、找不回。
//   所以这一段是**取回工程的唯一线索**：目标目录 + 认盘的卷序列号。
//
// 【为什么记卷序列号，不记盘符】
//   移动硬盘插到不同 USB 口、或系统里其它盘插拔之后，盘符是会变的（E→F→G）。
//   盘符变了还照着老路径找，可能把文件搬到**别的盘**上去 —— 那是灾难。
//   卷序列号是分区格式化时写死的，跟盘符无关，是"就是这块盘"的唯一证据。
// ============================================================

/// <summary>一个项目的归档记录。</summary>
public sealed class ArchivePart
{
    /// <summary>归档时间（人话，如 "2026-10-15 14:02"）。</summary>
    public string ArchivedAt { get; set; } = "";

    /// <summary>工程在移动硬盘上的完整目录，如 E:\1_创作素材\项目\思辨与立场</summary>
    public string TargetDir { get; set; } = "";

    /// <summary>归档时那块盘的卷序列号（十六进制，如 "A1B2C3D4"）。认盘就靠它。</summary>
    public string VolumeSerial { get; set; } = "";

    /// <summary>归档时那块盘的卷标（如 "Elements"）。只用于界面上给人看。</summary>
    public string VolumeLabel { get; set; } = "";

    /// <summary>搬到硬盘的文件数。</summary>
    public int FileCount { get; set; }

    /// <summary>搬走的总体积（字节）。</summary>
    public long TotalBytes { get; set; }

    /// <summary>归档时留下的"页面数"之外的啰嗦话（可空）。</summary>
    public string Note { get; set; } = "";
}
