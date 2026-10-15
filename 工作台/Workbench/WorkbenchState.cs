// ============================================================
// WorkbenchState.cs - 四个步骤共享的那一堆数据
// ============================================================
//
// 【为什么需要】
//   四步流程是一条链：
//     ① 粗剪产出 CutPlan（哪些秒保留）
//     ② 分镜基于"保留之后的时间轴"来切镜头
//     ③ 渲染要拿这组镜头
//     ④ 导出同时需要源文件和保留区间
//
//   后一步要读前一步的结果。如果每步各存各的，就会出现
//   "改了①的参数，④还在用旧数据"这种事故。
//
//   所以数据集中放一份，所有人读写同一处 —— 这是最简单可靠的做法。
//
// 【对应你的 Python 习惯】
//   这就是一个模块级的全局字典。Python 里你会写 STATE = {}，
//   C# 里因为要类型安全，写成一个带属性的类。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;
using LiteCut.Core.Media;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench;

/// <summary>
/// 工作台的共享状态。整个程序只有一个实例（在 App 里写着）。
/// </summary>
public sealed class WorkbenchState
{
    // ========================================================
    // 配置
    // ========================================================

    /// <summary>持久化配置。</summary>
    public AppConfig Config { get; } = AppConfig.Load();

    // ========================================================
    // ① 源文件与音频数据
    // ========================================================

    /// <summary>当前加载的媒体文件路径（音频或视频）。</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>解码后的波形数据。</summary>
    public PcmData? Pcm { get; set; }

    /// <summary>源文件的探测结果（有没有视频轨等）。</summary>
    public MediaProbe Probe { get; set; } = new();

    // ========================================================
    // ① 转写稿（可选）
    // ========================================================

    /// <summary>导入的转写稿。没有就是 null（走单轨）。</summary>
    public Transcript? Transcript { get; set; }

    /// <summary>转写稿的来源路径，界面上显示用。</summary>
    public string TranscriptPath { get; set; } = "";

    // ========================================================
    // ① 粗剪决策
    // ========================================================

    /// <summary>粗剪产生的决策文件。这是整条链的枢纽。</summary>
    public CutPlan? Plan { get; set; }

    /// <summary>跑分析时用的参数，导出时要写进 JSON 作记录。</summary>
    public AutoCutOptions LastOptions { get; set; } = AutoCutOptions.Aggressive();

    // ========================================================
    // ② 精剪（工作台编辑器交接）
    // ========================================================

    /// <summary>
    /// 精剪结果 —— **覆盖层**。
    ///
    /// 用户在完整编辑器（工作台编辑器）里手动精剪后，这里存的就是最终保留区间，
    /// 之后所有步骤（分镜/渲染/导出）都以它为准。
    ///
    /// 【为什么要做覆盖而不是改 Plan】
    ///   精剪是"人在波形上手工挪边界"，产出的区间和算法的 CutPlan 是两种来源。
    ///   直接改 Plan 会把算法的决策记录污染掉（你以后想对比"算法剪的 vs 我改的"就没了依据）；
    ///   覆盖层让两份决策都保留，还能一键还原。
    ///
    /// null = 尚未精剪（或已还原），下游直接用粗剪决策。
    /// </summary>
    public List<FFmpegExporter.Range>? FineCutKeeps { get; set; }

    /// <summary>是否已有精剪结果接管下游。</summary>
    public bool HasFineCut => FineCutKeeps != null && FineCutKeeps.Count > 0;

    // ========================================================
    // ② 分镜
    // ========================================================

    /// <summary>文案稿正文（用户写的稿子，不是转写稿）。没转写稿时用它降级估算口播稿。</summary>
    public string ScriptText { get; set; } = "";

    /// <summary>
    /// ★ 分镜表 —— **已交接版**（V1.1.0 / T-16 起语义变了，认准这条）。
    ///
    /// 【为什么这一份必须等于"已交接"】
    ///   ④ 渲染页与 ⑤ 组接页都是直接读 `_s.Shots`。只要它永远只装"用户点过
    ///   「应用到下游」的那一版"，下游代码**一行都不用改**，
    ///   而 §二 那个"改内存不改磁盘 → 产物静默错位"的坑，也就从根上关掉了
    ///   （不是靠迁移做得好，是靠**压根还没交接**）。
    ///
    ///   编辑中的那份在 <see cref="ShotDraft"/> 里，分镜页改的是它。
    /// </summary>
    public List<ShotRow> Shots { get; set; } = new();

    /// <summary>
    /// 编辑中的**草稿**镜头表（分镜页改的就是它）。点「应用到下游」才写回 <see cref="Shots"/>。
    ///
    /// 【为什么要两份而不是一份加差异】
    ///   这层"闸门"是 T-16 的地基：拆 / 合只动草稿，下游一个字都不动 → 不可能错位。
    ///   两份表靠**永久 ID** 对账（谁是谁），靠**镜号**算搬迁计划（文件从哪挪到哪）。
    ///
    /// ⚠ 两份必须是**各自独立的 ShotRow 对象**（改草稿绝不能捎带改了已交接版）。
    ///   初始化走 <see cref="EnsureShotDraft"/>，它做的是深拷贝。
    /// </summary>
    public List<ShotRow> ShotDraft { get; set; } = new();

    /// <summary>上次把镜头表交接给下游的时间（人话显示用，空 = 从没交接）。</summary>
    public string ShotCommittedAt { get; set; } = "";

    /// <summary>
    /// 发号器：下一个镜头 ID 的序号（s{ShotSeq:D2}）。
    /// 只增不减，**永不复用** —— 复用会让历史留痕里的 ID 指向另一个镜头。
    /// </summary>
    public int ShotSeq { get; set; }

    /// <summary>
    /// ★ 用户对这支视频的个性化要求。
    /// 每次调 Agent 做分镜时，它会**原样整段**拼进提示词，优先级高于内置规则。
    /// </summary>
    public string ShotRequirement { get; set; } = "";

    /// <summary>镜头切法：0=一句一镜 1=固定节奏 + 吸附句边界 2=语义合并（新项目默认，见 AppConfig.LastShotSplitMode）。</summary>
    public int ShotSplitMode { get; set; }

    /// <summary>固定节奏模式下的目标镜头时长（秒）。</summary>
    public double ShotPaceSec { get; set; } = 6.0;

    /// <summary>上次算出的口播稿是精确稿还是估算稿（"transcript" / "script"）。</summary>
    public string FinalScriptSource { get; set; } = "";

    /// <summary>
    /// 当前这份成片口播稿（含用户手工改过的句子）。
    /// 存进项目档案 —— 不然重开项目改动全丢。
    /// </summary>
    public FinalScript? FinalScriptDoc { get; set; }

    // ========================================================
    // 镜头表：草稿 ⇄ 已交接（V1.1.0 / T-16）
    // ========================================================
    //
    // 【一句话说清这套东西】
    //   拆 / 合只改草稿（随便拆，不打扰）；点「应用到下游」才交接
    //   （那一刻才重排镜号、改文件名、标「待重渲」）。
    //
    //   所以这一节里的东西都只服务一件事：**把"改内存"和"改磁盘"这两件事分开**。
    //   没交接之前，磁盘上什么都没动，下游读的还是上一版 —— 错不了。

    /// <summary>
    /// 深拷贝一镜。
    /// ⚠ 必须是深拷贝（逐字段 new 一个）—— 草稿和已交接版如果是同一个对象引用，
    ///   在草稿上打字会顺手把"已交接版"也改了，闸门就白设了。
    /// </summary>
    public static ShotRow CloneShot(ShotRow s) => new()
    {
        Id = s.Id,
        Index = s.Index,
        StartSec = s.StartSec,
        EndSec = s.EndSec,
        Text = s.Text,
        Description = s.Description,
        Motion = s.Motion,
        Image = s.Image,
        ImageFile = s.ImageFile,
        SourceStartSec = s.SourceStartSec,
        SourceEndSec = s.SourceEndSec,
        Edited = s.Edited,
        DesignStale = s.DesignStale,
        FromId = s.FromId,
        MergedInto = s.MergedInto,
        // ---- 插片（V1.10.0）：这几位丢了，重切分之后插片就退化成普通镜头（会去找不存在的台词）
        IsInsert = s.IsInsert,
        VideoFile = s.VideoFile,
        VideoDurationSec = s.VideoDurationSec,
        VideoStartSec = s.VideoStartSec,
        DurationPinned = s.DurationPinned,
        // ---- 插片原声（V1.10.15）：丢了就会"悄悄变回静音"，而指纹还显示已就绪
        InsertAudio = s.InsertAudio,
        InsertVolumeDb = s.InsertVolumeDb,
    };

    /// <summary>
    /// 给还没 ID 的镜头补发 ID（老项目升级、新切分都走这一条路）。返回补发了几个。
    ///
    /// 【老项目怎么办】
    ///   像「思辨与立场」那种已经切好 51 镜的项目，档案里没有 Id 字段。
    ///   这里按当前顺序 1..N 各发一个 —— **纯加字段，不改任何文件名、不动任何产物**，
    ///   做完一次永不回头。
    /// </summary>
    public int EnsureShotIds(List<ShotRow> rows)
    {
        int issued = 0;
        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.Id)) continue;
            r.Id = NextShotId();
            issued++;
        }
        return issued;
    }

    /// <summary>发一个新 ID（s01 / s02 …），并推进发号器。</summary>
    public string NextShotId()
    {
        ShotSeq++;
        return "s" + ShotSeq.ToString("D2");
    }

    /// <summary>
    /// 保证草稿存在且与已交接版同步。
    /// 草稿是空的（刚打开老项目 / 刚切完镜头）→ 从已交接版深拷贝一份出来。
    /// </summary>
    public void EnsureShotDraft()
    {
        EnsureShotIds(Shots);
        if (ShotDraft.Count > 0 || Shots.Count == 0) return;
        ShotDraft = Shots.Select(CloneShot).ToList();
    }

    /// <summary>把草稿原样接管为已交接版（点「应用到下游」时调，之后下游就读新表了）。</summary>
    public void CommitShotDraft()
    {
        Shots = ShotDraft.Select(CloneShot).ToList();
        ShotCommittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// 把草稿丢掉、退回已交接版（撤销 / 重开页面用）。
    /// </summary>
    public void ResetShotDraft()
    {
        ShotDraft = Shots.Select(CloneShot).ToList();
    }

    /// <summary>
    /// 镜头表指纹：判"草稿跟已交接版一不一样"，也用于 Agent 并发保护
    /// （任务包发出时的镜头表 vs 回来时的镜头表）。
    ///
    /// 口径：镜号 + 起止 + 四个设计字段 + 两个标记，**不含 ID**
    /// （重排镜号算"变了"，而 ID 本身不是内容）。
    /// </summary>
    public static string ShotsFingerprint(IEnumerable<ShotRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(r.Index).Append('\u0001')
              .Append(r.StartSec.ToString("F3")).Append('\u0001')
              .Append(r.EndSec.ToString("F3")).Append('\u0001')
              .Append(r.Text).Append('\u0001')
              .Append(r.Description).Append('\u0001')
              .Append(r.Motion).Append('\u0001')
              .Append(r.Image).Append('\u0001')
              .Append(r.DesignStale ? '1' : '0').Append('\u0001')
              .Append(r.Edited ? '1' : '0').Append('\u0002');
        }
        return Fnv1a(sb.ToString());
    }

    /// <summary>FNV-1a 64 位。不用 string.GetHashCode()：它每次进程启动换种子，比不了。</summary>
    public static string Fnv1a(string s)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= prime; }
            return h.ToString("x16");
        }
    }

    /// <summary>草稿跟已交接版是不是不一样（不一样 = 有还没交接的改动）。</summary>
    public bool ShotDraftDirty =>
        ShotDraft.Count > 0 &&
        !string.Equals(ShotsFingerprint(ShotDraft), ShotsFingerprint(Shots), StringComparison.Ordinal);

    // ========================================================
    // ③ 渲染
    // ========================================================

    /// <summary>渲染输出目录。</summary>
    public string RenderOutputDir { get; set; } = "";

    // ========================================================
    // ④ 导出
    // ========================================================

    /// <summary>最后一次导出的文件路径。</summary>
    public string LastExportPath { get; set; } = "";

    // ========================================================
    // ⑤ 精剪时间轴的"存档态"
    // ========================================================
    //
    // 【为什么要单独存一份"完整块列表"】
    //   精剪以前的对外口径是"保留区间"（FineCutKeeps），
    //   那是给下游（分镜/渲染/导出）用的最小信息。
    //   但存项目时只存区间就亏了：手工挪过的块边界、每块的音量、
    //   加的淡入淡出，全都会被压平消失。
    //   所以额外存一份完整块列表，下次打开能原样还原。

    /// <summary>
    /// 【2026-09-29 新增】当前时间轴快照是剪哪个源文件的。
    ///
    /// 【为什么必须有这个】
    ///   时间轴块里虽然各自记着 SourcePath，但整份时间轴是"针对某一个源素材
    ///   排好的版面"。换了源文件之后，旧版面在新素材上毫无意义 ——
    ///   最坏的情况是：拿旧文件的区间去当下游的剪辑决策，剪出来完全不是你要的。
    ///   所以给整份快照盖一个"它是谁的"的戳，换源文件后自动作废。
    /// </summary>
    public string TimelineSourcePath { get; set; } = "";

    /// <summary>
    /// 精剪时间轴的最新快照（完整块列表）。
    /// 由精剪页在同步时写入；保存项目时直接读它。
    /// </summary>
    public List<ClipPart> TimelineSnapshot { get; set; } = new();

    /// <summary>快照对应的播放线位置（秒）</summary>
    public double TimelinePlayheadSec { get; set; }

    /// <summary>快照对应的时间轴视口起点（秒）</summary>
    public double TimelineViewStartSec { get; set; }

    /// <summary>快照对应的时间轴缩放（秒/像素）</summary>
    public double TimelineSecPerPixel { get; set; } = 0.05;

    /// <summary>
    /// 待还原的时间轴快照。打开项目时由 ProjectSession 填上，
    /// 精剪页第一次激活时消费掉（消费后置 null，避免每次进页都重铺）。
    /// </summary>
    public List<ClipPart>? PendingTimelineSnapshot { get; set; }

    // ========================================================
    // 派生数据
    // ========================================================

    /// <summary>
    /// 当前决策里要保留的区间。导出、试听、预览全都读这个。
    /// 精剪结果存在时优先用它（精剪 > 粗剪，人工 > 算法）；
    /// 否则用粗剪决策（含用户在界面上逐段改过的 keep/drop）。
    /// </summary>
    public List<FFmpegExporter.Range> KeepRanges()
    {
        var r = new List<FFmpegExporter.Range>();
        if (HasFineCut)
        {
            foreach (var k in FineCutKeeps!)
                r.Add(new FFmpegExporter.Range(k.Start, k.End));
            return r;
        }

        if (Plan == null) return r;

        foreach (var t in Plan.ComputeTimeline())
            r.Add(new FFmpegExporter.Range(t.SourceStartSec, t.SourceEndSec));

        return r;
    }

    /// <summary>
    /// 源文件的文件名（不含目录）。界面标题用。
    /// </summary>
    public string SourceFileName =>
        string.IsNullOrEmpty(SourcePath) ? "未加载" : Path.GetFileName(SourcePath);

    /// <summary>
    /// 源素材的**波形/探测已经解码好了**（Pcm 非空）。
    ///
    /// ⚠ 它回答的是"解码这件事做完没有"，**不是"有没有源素材"**。
    ///   解码只有第①步会做（打开项目时把路径排进 `Step1CutView.PendingFile`，
    ///   等用户进①页才真正解码）。所以"本次运行没进过①"的会话里它恒为 false。
    ///   拿它当"能不能往下走"的门槛，下游页会永远灰着 —— 见 HasSourceFile。
    /// </summary>
    public bool HasSource => Pcm != null && !string.IsNullOrEmpty(SourcePath);

    /// <summary>
    /// **有没有源素材** —— 逻辑义：路径非空，且文件还在磁盘上。
    ///
    /// 【为什么必须和 HasSource 分开】（2026-10-03 用户实报「⑤ 开始出片按钮灰着」）
    ///   `HasSource` 要求 `Pcm != null`，而 Pcm 只有**第①步构造时**才解码。
    ///   用户打开项目后落在写作台（或上次停的那一页），直接进⑤ ——
    ///   ① 从没构造过，Pcm 就是 null。于是"开始出片"永远灰着，
    ///   界面还谎报"还没有加载源素材"（文件明明躺在磁盘上）。
    ///   判"有没有源素材"该看**磁盘事实**，跟"解码做到哪一步"无关。
    ///
    ///   ★ 这条和 R-17（"渲完不变绿"）是同一类病：**状态只在某一页里被建立**，
    ///     别的页拿它当门槛就必然对不上。谁要判"能不能往下走"，用这个。
    /// </summary>
    public bool HasSourceFile =>
        !string.IsNullOrWhiteSpace(SourcePath) && File.Exists(SourcePath);

    /// <summary>
    /// 是否已经有粗剪决策。
    /// </summary>
    public bool HasPlan => Plan != null && Plan.Segments.Count > 0;

    /// <summary>
    /// 是否已有"剪辑决策"（粗剪或精剪任一）。
    ///
    /// 【为什么要和 HasPlan 分开】
    ///   下游各页（分镜/渲染/导出）以前一律只认 HasPlan，但精剪页支持
    ///   直接导入素材手工剪（不跑粗剪）—— 那条路的决策就是精剪时间轴本身。
    ///   只认粗剪的话，纯精剪项目在下游处处碰壁：「应用到下游」永远灰着、
    ///   导出页永远说"还没有粗剪结果"（2026-09-29 用户报的）。
    ///   下游判断"能不能往下走"一律用这个，不要直接用 HasPlan。
    /// </summary>
    public bool HasDecision => HasPlan || HasFineCut;

    // ========================================================
    // 生效决策（下游此刻到底在用哪一层）
    // ========================================================
    //
    // 【为什么要把这件事显式说出来】
    //   以前"下游用的是谁"是个隐式推论：只要 FineCutKeeps 非空就用精剪，
    //   否则用粗剪。代码上没问题，但对用户是黑箱 —— 他重跑了一次粗剪、
    //   又或者某次精剪没应用成功，界面上完全看不出来下游到底按哪份在剪。
    //   （2026-09-29 用户报"点了应用到下游但下游什么都没变"，就是撞在这上面。）
    //   所以现在把它变成一个能显示、能比较、能一句话说清的属性。

    /// <summary>下游此刻真正使用的那一层决策。</summary>
    public DecisionSource ActiveDecision =>
        HasFineCut ? DecisionSource.Fine : (HasPlan ? DecisionSource.Coarse : DecisionSource.None);

    /// <summary>
    /// 当前生效决策的人话描述，例如「精剪 · 184 段 · 6分12秒」。
    /// 没有决策时是空串（界面据此显示"尚未应用"）。
    /// </summary>
    public string ActiveDecisionLabel
    {
        get
        {
            var ranges = KeepRanges();
            if (ranges.Count == 0) return "";

            string who = ActiveDecision == DecisionSource.Fine ? "精剪" : "粗剪";
            double total = ranges.Sum(r => r.End - r.Start);
            return $"{who} · {ranges.Count} 段 · {FormatDuration(total)}";
        }
    }

    /// <summary>
    /// 把下游切回粗剪决策。
    ///
    /// 【注意：这不是"删掉精剪"】
    ///   精剪时间轴本体存在 TimelineSnapshot 里（也存进项目档案），
    ///   这里只把"谁来当决策"的开关拨回粗剪。下次回精剪页，
    ///   你上次排的时间轴原样还在，再点一次「应用到下游」就能重新接管。
    ///
    ///   这正是"重新调粗剪不该毁掉精剪结果"的实现方式：
    ///   改粗剪不动精剪，要切换必须明确点一下。
    /// </summary>
    public void UseCoarseDecision() => FineCutKeeps = null;

    /// <summary>时长格式化：秒 → "6分12秒" / "48.3秒"。全工作台统一用这一个。</summary>
    public static string FormatDuration(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        int totalMin = (int)(sec / 60);
        if (totalMin >= 1)
        {
            int s = (int)Math.Round(sec - totalMin * 60);
            if (s >= 60) { totalMin++; s -= 60; }
            return totalMin >= 60
                ? $"{totalMin / 60}小时{totalMin % 60:D2}分"
                : $"{totalMin}分{s:D2}秒";
        }
        return $"{sec:F1}秒";
    }

    // ========================================================
    // 源文件解码完成 —— 决策要不要作废，只看"素材换没换"
    // ========================================================
    //
    // 【为什么这条规矩必须单独拎出来，而且必须写在状态层】
    //
    //   2026-09-29 踩过一个大坑：第①步的加载函数里无条件清空
    //   Plan / FineCutKeeps / Shots。看着合理（"换了素材，旧决策当然作废"），
    //   但它漏了一件事 —— **打开项目也会走到那里**：
    //     打开项目 → ProjectSession 把源文件路径排进待加载槽位
    //     → 第①步一构造就自动解码 → 决策被清 → 自动保存把空状态写回磁盘。
    //   于是"重启后粗剪白剪了"、"精剪应用到下游但下次打开又不生效"。
    //
    //   现在改成：同一个源文件重新解码 = 决策原样保留；
    //             只有真的换了素材才作废。
    //
    //   放在这里（而不是留在界面代码里）的理由很实际：它是纯数据操作，
    //   可以被无头验证直接断言，不用去启动 WPF 界面。

    /// <summary>两个路径是不是同一个源文件（同路径即重载，不同才是换素材）。</summary>
    public static bool IsSameSource(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 源文件解码完成，把 PCM 与探测结果装进状态，并按需作废旧决策。
    /// </summary>
    /// <param name="switchedSource">
    /// true = 真的换了素材（作废和旧素材绑定的决策）。
    /// false = 同一个文件重新解码（开项目 / 重新加载）—— 决策一个都不动。
    /// </param>
    public void ApplyDecodedSource(string path, PcmData pcm, MediaProbe probe, bool switchedSource)
    {
        SourcePath = path;
        Pcm = pcm;
        Probe = probe;

        if (!switchedSource) return;

        // 切了源文件 → 这些全是"针对旧素材"的判断，留着就会串台：
        //   旧粗剪决策会当新素材的剪法、旧精剪区间会当新素材的决策、
        //   旧分镜表会配到新素材上。
        Plan = null;
        FineCutKeeps = null;
        Transcript = null;
        TranscriptPath = "";
        Shots.Clear();
        // 分镜的草稿、已交接时间、发号器同理 —— 它们全是"针对旧素材"的。
        // 发号器一并归零：新素材的镜头表从头发号，不会跟旧素材的 ID 撞上
        // （旧素材的产物已经毫无意义，留着旧序号只会让新 ID 变得难读）。
        ShotDraft.Clear();
        ShotCommittedAt = "";
        ShotSeq = 0;

        // 时间轴快照同理 —— 它是"针对旧素材排好的版面"，换素材后毫无意义。
        // 只清内存里的这一份（项目档案里的副本不动，用户回不去也不会丢）。
        TimelineSnapshot = new List<ClipPart>();
        TimelineSourcePath = "";
    }

    /// <summary>
    /// 当前决策的成片总时长（保留区间长度之和）。
    /// 有粗剪时和 Plan.FinalDurationSec 相等；纯精剪时就是精剪块的总长。
    /// </summary>
    public double DecisionDurationSec => KeepRanges().Sum(r => r.End - r.Start);

    /// <summary>
    /// 当前决策的"成片时间轴"：把保留区间依序首尾相接，给出每段在成片里的
    /// 位置和对应的源素材区间。分镜页要把"成片第几秒"反推回"源素材第几秒"，读这个。
    /// 有粗剪时等价于 Plan.ComputeTimeline()；精剪接管后就是精剪块序列。
    /// </summary>
    public List<(double TimelineStartSec, double TimelineEndSec, double SourceStartSec, double SourceEndSec)>
        DecisionTimeline()
    {
        var result = new List<(double, double, double, double)>();
        double cursor = 0;
        foreach (var r in KeepRanges())
        {
            double len = r.End - r.Start;
            result.Add((cursor, cursor + len, r.Start, r.End));
            cursor += len;
        }
        return result;
    }

    // ========================================================
    // 成片口播稿 —— 交接给分镜的主角
    // ========================================================
    //
    // 【这是什么】
    //   剪完之后观众**实际会听到**的那篇稿子，每句带成片时间码。
    //   算法：转写稿（词级时间码）∩ 剪辑决策（保留区间），纯算术。
    //   没有转写稿时退回文案稿 + 字数占比，但会标成"估算"。
    //
    // 【为什么放在状态层】
    //   它需要 Transcript + 决策 + 文案稿三样，全在状态里；
    //   放这里能被无头验证直接断言，不用起 WPF。

    /// <summary>算（或重算）成片口播稿。</summary>
    public FinalScript BuildFinalScript()
    {
        var spans = new List<DecisionSpan>();
        foreach (var t in DecisionTimeline())
        {
            spans.Add(new DecisionSpan
            {
                TimelineStartSec = t.TimelineStartSec,
                TimelineEndSec = t.TimelineEndSec,
                SourceStartSec = t.SourceStartSec,
                SourceEndSec = t.SourceEndSec,
            });
        }

        // 把粗剪标为"剪了不影响内容"的段（停顿、语气词、重说/重录的那几遍）也告诉算法。
        // 有了它，"剪掉 2 秒口误"就不会被当成"这句少了 2 秒话"。
        // 精剪里用户手动删掉的段不在这里面（那些是真删了话），会被正确算成内容缺失。
        List<HarmlessSpan>? harmless = null;
        if (Plan?.Segments is { Count: > 0 })
        {
            harmless = new List<HarmlessSpan>();
            foreach (var s in Plan.Segments)
            {
                if (s.Action == CutAction.Keep) continue;
                if (!FinalScriptBuilder.IsHarmless(s.Reason)) continue;
                harmless.Add(new HarmlessSpan
                {
                    SourceStartSec = s.SourceStartSec,
                    SourceEndSec = s.SourceEndSec,
                });
            }
            if (harmless.Count == 0) harmless = null;
        }

        var fs = FinalScriptBuilder.Build(Transcript, spans, ScriptText, DecisionDurationSec, harmless);
        FinalScriptSource = fs.Source;

        // ★ 手工改过的句子要跟着稿子走。
        //   重算时（不管是页面里点「重算」还是重开项目）如果丢了这些改动，
        //   用户等于白改 —— 转写错字会原样回来。
        //   匹配口径：源起止时间都对齐（量化到 0.05 秒）才算同一句。
        if (FinalScriptDoc?.Lines is { Count: > 0 } old)
        {
            var edits = new Dictionary<string, ScriptLine>();
            foreach (var l in old)
                if (l.Edited) edits[EditKeyOf(l.SourceStartSec, l.SourceEndSec)] = l;

            if (edits.Count > 0)
                foreach (var l in fs.Lines)
                    if (edits.TryGetValue(EditKeyOf(l.SourceStartSec, l.SourceEndSec), out var kept))
                    {
                        l.Text = kept.Text;
                        l.Edited = true;
                    }
        }

        FinalScriptDoc = fs;
        return fs;
    }

    private static string EditKeyOf(double a, double b) =>
        $"{Math.Round(a * 20) / 20:0.00}|{Math.Round(b * 20) / 20:0.00}";

    /// <summary>
    /// 保存配置到磁盘。
    /// </summary>
    public void SaveConfig() => Config.Save();
}

/// <summary>
/// 下游此刻在用哪一层决策。
/// </summary>
public enum DecisionSource
{
    /// <summary>还没有任何决策 —— 分镜/渲染/导出都无从下手。</summary>
    None = 0,

    /// <summary>用第①步的粗剪决策（Plan）。</summary>
    Coarse = 1,

    /// <summary>用第②步的精剪时间轴。人工结果优先于算法。</summary>
    Fine = 2,
}

/// <summary>
/// 一行分镜。界面表格里的一行，也是导出 JSON 里的一个对象。
/// </summary>
public sealed class ShotRow
{
    /// <summary>
    /// ★ 镜头的**永久 ID**（V1.1.0 / T-16 新增）。切分时发号，之后**永不改变、永不复用**。
    ///
    /// 【为什么一个镜号不够用 —— 一个 `Index` 扛着三个身份】
    ///   ① 顺序：分镜表里排第几          → 拆合重排会变
    ///   ② 身份：这份设计 / 这个产物是谁的 → **绝不能跟着镜号走**
    ///   ③ 文件名：shot_007.mp4          → 跟镜号走（人翻目录要能对上）
    ///
    ///   三件事全压在 Index 上，一拆镜头就出事：内存里顺序变了、磁盘上文件名没动，
    ///   于是第 9 镜去读 shot_009.mp4 —— 那是原第 9 镜的画面，**整片错位且不报错**。
    ///
    ///   所以把「身份」单独拎出来给 ID：镜号随便重排，设计归属、产物归属、
    ///   对账全靠 ID。产物**命名**仍然按镜号（人看得懂），但**清单里的键**是 ID。
    ///
    /// 【格式】与位置无关的递增短码：s01 / s02 / …。
    ///   绝不要写成 `123a` 这种"镜号 + 后缀" —— 镜号一变，那个 123 就成了
    ///   撒谎的历史遗迹（同一个 ID 迁移完挂到了别的序号上，看 ID 反而误导）。
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// 设计可能已不覆盖当前范围（拆 / 合之后打上）。
    ///
    /// 【为什么不自动清】
    ///   自动清 = 系统替用户判断"这份设计还行"。拆开之后原设计只为"前后半合起来"
    ///   写过（比如动效写了 0-3s/3-6s/6-9s 三段，现在这镜只剩前 3.2 秒），
    ///   到底还对不对只有人知道。所以标成「待复核」，等用户点「确认无误」才清。
    /// </summary>
    public bool DesignStale { get; set; }

    /// <summary>血缘：这一镜是从哪个 ID 拆出来的（新镜记录；不是拆出来的就是空）。</summary>
    public string FromId { get; set; } = "";

    /// <summary>血缘：这一镜被并到哪个 ID 去了（被合并掉的那一镜记录）。</summary>
    public string MergedInto { get; set; } = "";

    /// <summary>镜头编号，从 1 开始（★ 会随拆合重排，所以别拿它当身份，身份看 Id）。</summary>
    public int Index { get; set; }

    /// <summary>在成片时间轴上的起点（秒）。</summary>
    public double StartSec { get; set; }

    /// <summary>在成片时间轴上的终点（秒）。</summary>
    public double EndSec { get; set; }

    /// <summary>时长（秒）。</summary>
    public double DurationSec => EndSec - StartSec;

    /// <summary>这一镜对应的文案句子。</summary>
    public string Text { get; set; } = "";

    /// <summary>画面/镜头设计描述。由 Agent 或用户填。</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// 动效设计（长镜头必填）。
    ///
    /// 【为什么单独一列】
    ///   十几秒的长镜头如果只在"画面构思"里写一句，渲染出来就是一行字幕顶到底。
    ///   把"第几秒出现什么、第几秒变成什么"单独拆出来，是为了逼着设计往"动"上想。
    /// </summary>
    public string Motion { get; set; } = "";

    /// <summary>
    /// 需要配一张网络图片时，这里写"什么样的图 + 用什么动效展示它"。
    /// 由 Agent 填（用户也能改）。渲染环节按它去搜图下载进素材库。
    /// 留空 = 这个镜头不需要图片（纯文字卡 / 几何动效）。
    /// </summary>
    public string Image { get; set; } = "";

    /// <summary>
    /// 图片搜到之后落在素材库里的文件名（相对路径）。
    /// 空 = 还没下载；非空 = 已经有了，渲染直接用。
    /// </summary>
    public string ImageFile { get; set; } = "";

    /// <summary>对应源文件的起点（导出渲染任务时要用）。</summary>
    public double SourceStartSec { get; set; }

    /// <summary>对应源文件的终点。</summary>
    public double SourceEndSec { get; set; }

    /// <summary>
    /// 用户手动合并/拆分/调过边界的镜头 —— 点「重新自动切分」时不被覆盖（会先确认）。
    /// 匹配口径是源起止时间，与句子 Edited 同一套思路。
    /// </summary>
    public bool Edited { get; set; }

    // ========================================================
    // ★ 插片（V1.10.0）
    //   用户手动插入的一段：**没有口播**，所以成片里这段是静音的；
    //   时长由用户自己定；素材（视频）可选 —— 不引用素材就是一段纯静音画面。
    //
    //   【为什么做成"普通镜头 + 几个开关"，而不是单独一套插片数据】
    //     用户要的是"插片也能跟别的镜头一样写分镜"，只有两处不同：静音 + 时长自己定。
    //     单独建一套表，写分镜的地方、渲染的地方、交接的地方就都要写第二遍。
    //     所以插片就是 ShotRow 本身，只是 IsInsert = true。
    // ========================================================

    /// <summary>
    /// 这一段是不是插片。true = 没有口播（成片里静音）、时长不受转写稿约束。
    /// </summary>
    public bool IsInsert { get; set; }

    /// <summary>
    /// 插片引用的**视频素材**（相对项目素材库的路径，如 "插片素材/街景.mp4"）。
    /// 空 = 不用素材（照样是一段有画面的静音镜头）。
    /// </summary>
    public string VideoFile { get; set; } = "";

    /// <summary>素材本身的全长（秒）。只用来在卡上提示"只播前 N 秒"。</summary>
    public double VideoDurationSec { get; set; }

    /// <summary>素材从第几秒开始播（默认 0 = 从头）。</summary>
    public double VideoStartSec { get; set; }

    /// <summary>
    /// 用户**亲手改过**这一段的时长。
    ///
    /// 【为什么要记这一位】选了素材之后我们会"好心"把时长改成素材全长 ——
    ///   但只该做一次：用户一旦自己定过时长，就**永远以用户为准**，
    ///   视频播不完就截断（不补、不循环、不拉伸）。自动改一次是帮忙，改两次是添乱。
    /// </summary>
    public bool DurationPinned { get; set; }

    /// <summary>
    /// ★ 插片要不要用**素材自带的原声**（V1.10.15 / T-25）。默认 false = 沿用老行为「成片静音」。
    ///
    /// 【为什么默认关】插片最早的定义就是"一段没有口播的画面"，默认静音是安全的 ——
    ///   带声音的素材（比如从抖音扒来的视频）如果不打招呼就把原声放出来，
    ///   会跟口播撞在一起。所以这件事必须由用户显式打开。
    ///
    /// 【打开之后全链路会变三处】
    ///   ① 渲染提示词按 <see cref="InsertVolumeDb"/> 写 data-volume（不再写死 0）；
    ///   ② 渲染任务包的 silent 字段变 false；
    ///   ③ 组接层不再给这一段补静音，而是把该镜 MP4 的音轨接进成片音轨。
    ///
    /// 【为什么不是"自动探测素材有没有声音"】素材有声音 ≠ 用户想放出来。
    ///   这是**意图**问题，不是技术问题，只能问用户。
    /// </summary>
    public bool InsertAudio { get; set; }

    /// <summary>
    /// 插片原声的音量（dB）。0 = 原样、-6 ≈ 减半、+6 ≈ 加倍。
    /// 只有 <see cref="InsertAudio"/> 为真时才有意义。
    /// </summary>
    public double InsertVolumeDb { get; set; }
}
