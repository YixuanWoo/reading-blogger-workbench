// ============================================================
// FFmpegAssembler.cs - ⑤ 组接：把 ④ 渲出来的镜头拼成成片
// ============================================================
//
// 【它和 FFmpegExporter 的分工】
//   FFmpegExporter（已有）—— 拿**源素材**按决策裁一遍再编码。
//     源是音频就只能出 m4a，源是视频就把原视频跟着裁。它解决的是
//     "把一条素材剪短"，画面自始至终只有源素材那一份。
//
//   FFmpegAssembler（本文件）—— 拿**第④步渲好的一堆镜头 MP4**，
//     按镜号顺序拼起来，再挂上按决策重建的音轨。画面来自渲染产物，
//     不是源素材。这才是"渲染完了 → 出成片"那一步。
//
// 【为什么以前只能出 m4a】
//   口播视频的源素材就是一条录音（没有画面）。第④步把画面一镜一镜渲在别处，
//   组接页却只会"裁源素材" —— 源没画面，所以永远只能出 m4a。
//   设计草案第五节早就写了 A/B/C 三条路，但只实现了 A（音频成片）和 C（XML），
//   B（画面成片）一直空着。本文件就是补 B。
//
// 【★ 音画对齐：2026-10-01 从「拉伸音频」改成「conform 画面」（T-07）】
//
//   【问题】渲染器出片时长只能是整帧的整数倍（30fps 下 1 帧 = 33.3ms）。
//     设计 8.325s 的块，实际出片 8.333s —— 每块多 0~33ms。
//     单块看着无所谓，51 块拼起来就是画面比口播长了近 1 秒，
//     而且是渐进偏的，从中间就开始音画错位。
//     （同类累积误差在口播稿上踩过一次：漏算句间气口 → 219 句累差 16.8s。
//       "每段都很小"不等于"整体不重要"。）
//
//   【旧解法（已废弃）】把每块的音频按实测时长微拉伸（asetrate）去凑画面。
//     能用，但**动了音频**：音高同比偏移，且成片音轨再也无法与精剪源逐样本对账。
//     旧路径还额外需要一个外部 Python 脚本（build_audio.py::warp_to_blocks），
//     那套东西随本次改造一并作废。
//
//   【现解法】音频全程零处理，**画面去迁就音频**：
//     · 音频：从 FineCut.Clips 原生重建，一个采样都不改（不拉伸、不变调）。
//     · 画面：每块 conform 到目标时长 —— 长了 trim 裁掉尾部，短了 tpad 冻最后一帧。
//     · 目标是"这一块的音频段长度"，所以音画在**每一块边界**都严格对齐，误差不累积。
//
//   【代价：画面要过一次编码】
//     旧的直拷（-c:v copy）一个字节都不动，但**裁不了** —— 精确裁剪必须过一遍编码。
//     换来的是音频零处理。这是认过的交换，见 文档/优化与风险登记.md 的 R-03。
//
//   【帧对齐的尾声：每块末尾垫 ≤1 帧静音】
//     视频只能整帧，音频段长度却是任意小数。于是把每块目标时长**向上取整到整帧**，
//     画面正好等于这个长度；音频随后补 ≤33ms 静音（apad）补齐。
//     音频内容一个采样都没变，只是末尾垫了一小段静音 ——
//     而块边界本来就是句末（语义合并保证切点落句末），句末自带气口，听不出来。
//     （真要听出"顿"感，见 优化与风险登记 R-04 / O-01。）
//
// 【★ 立体声：用 pan 复制，别用 aformat/-ac 2】
//   源是单声道时，aformat=channel_layouts=stereo 或 -ac 2 走的是
//   "下混"路径，实测会把整体音量压低 3dB。用 pan=stereo|c0=c0|c1=c0
//   按单位增益复制到左右两轨，才是"听起来和精剪里一样"。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LiteCut.Core.Media;

/// <summary>
/// 成片音轨里的一段：从源素材的哪一段来、多大音量、要不要淡化。
/// 顺序 = 它在成片里的先后（不是源时间先后 —— 精剪允许挪块）。
/// </summary>
public sealed class FilmSegment
{
    public double SourceStartSec { get; set; }
    public double SourceEndSec { get; set; }

    /// <summary>本段音量（dB）。精剪里每块调过的音量就记在这。</summary>
    public double VolumeDb { get; set; }

    public double FadeInSec { get; set; }
    public double FadeOutSec { get; set; }

    /// <summary>
    /// 这一段是**纯静音**（插片用的）。
    ///
    /// 【为什么要有它】插片那一段画面里没有口播，成片在那儿必须是静音的；
    ///   但它照样要占时间轴的位置 —— 后面的口播得整体顺延，不然音画就错位了。
    ///
    /// 【为什么不干脆"不放这一段"】音频时间轴是依序首尾相接的一条轴，
    ///   少放一段 = 后面所有声音提前，成片就成了"画面还在放插片、声音已经在说下一句"。
    /// </summary>
    public bool IsSilence { get; set; }

    /// <summary>静音段的长度（秒）。只有 <see cref="IsSilence"/> 为真时才有意义。</summary>
    public double SilenceSec { get; set; }

    // ========================================================
    // ★ 插片原声段（V1.10.15 / T-25）
    // ========================================================
    //
    // 【为什么音轨要回头去抠渲染产物】
    //   成片的音轨是从**原录音**（精剪决策 + 源文件）重建的。插片不属于这次录音 ——
    //   它在原录音里没有任何对应位置。所以插片想出声，唯一的来源就是**它自己的产物音轨**。
    //   这也解释了为什么"插片原声"必须连渲染一起打开：产物静音，成片就只能是静音
    //   （V1.10.15 之前正是如此：提示词硬写 data-volume="0"，而组接层从不读产物音轨）。

    /// <summary>
    /// 这一段的声音来自**这个 MP4 的音轨**（插片开了「素材原声」时用）。
    /// 空 = 不是这种段。
    /// </summary>
    public string VideoAudioPath { get; set; } = "";

    /// <summary>在 <see cref="VideoAudioPath"/> 这个产物里的起止（秒）。</summary>
    public double VideoAudioStartSec { get; set; }
    public double VideoAudioEndSec { get; set; }

    public double LengthSec => IsSilence
        ? SilenceSec
        : VideoAudioPath.Length > 0 ? VideoAudioEndSec - VideoAudioStartSec
        : SourceEndSec - SourceStartSec;

    /// <summary>线性增益。0dB = 1.0。</summary>
    public double Gain => Math.Abs(VolumeDb) < 1e-9 ? 1.0 : Math.Pow(10, VolumeDb / 20.0);
}

/// <summary>
/// 成片里的一块画面（= 第④步渲染出来的一个 MP4）。
/// </summary>
public sealed class FilmBlock
{
    /// <summary>镜号，从 1 开始。决定拼接顺序。</summary>
    public int Index { get; set; }

    /// <summary>渲染产物路径。</summary>
    public string VideoPath { get; set; } = "";

    /// <summary>在成片时间轴上的设计起点/终点（秒）。音轨就按这个切。</summary>
    public double DesignStartSec { get; set; }
    public double DesignEndSec { get; set; }

    /// <summary>设计时长。</summary>
    public double DesignSec => DesignEndSec - DesignStartSec;

    /// <summary>实测时长（探测后回填）。渲染器会向上取整到整帧，所以一般略大于设计值。</summary>
    public double ActualSec { get; set; }

    /// <summary>文件大小。小于 <see cref="FFmpegAssembler.MinBlockBytes"/> 视为没出片。</summary>
    public long SizeBytes { get; set; }
}

/// <summary>
/// 出片前自检的结果。**先自检再拼** —— 缺一块就拼出来的片子是断的，
/// 与其拼完才发现，不如拼之前就把"缺哪几块"列出来。
/// </summary>
public sealed class FilmPlan
{
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<FilmBlock> Blocks { get; set; } = new();

    /// <summary>画面设计总长（= 各块设计时长之和）。</summary>
    public double DesignTotalSec { get; set; }

    /// <summary>画面实测总长（= 各块 MP4 实测时长之和）。</summary>
    public double ActualTotalSec { get; set; }

    /// <summary>
    /// 帧对齐后的画面**目标**总长（各块目标时长之和）。成片就该这么长。
    /// 每块目标 = 该块音频段长度向上取整到整帧 —— 所以它 ≥ AudioTotalSec，
    /// 差值就是"每块末尾那不到一帧的静音"之和（见文件头的说明）。
    /// </summary>
    public double TargetTotalSec { get; set; }

    /// <summary>音轨总长（= 决策保留区间之和）。</summary>
    public double AudioTotalSec { get; set; }

    public bool Ok => Problems.Count == 0;

    /// <summary>整帧累积差：实测比设计长多少秒（>0 表示画面到头会拖后）。</summary>
    public double DriftSec => ActualTotalSec - DesignTotalSec;

    /// <summary>音轨和画面设计长度的差。这个不为 0 就是决策对不上，必须报错。</summary>
    public double AudioDriftSec => AudioTotalSec - DesignTotalSec;
}

/// <summary>出片参数。</summary>
public sealed class FilmAssembleOptions
{
    /// <summary>音轨来源（源录音）。画面走渲染产物，声音还得回源素材。</summary>
    public string SourcePath { get; set; } = "";

    public string AudioBitrate { get; set; } = "192k";

    /// <summary>成片帧率。每块目标时长按它向上取整到整帧。</summary>
    public int Fps { get; set; } = 30;

    /// <summary>视频画质（libx264 用 crf、NVENC 用 cq）。conform 要过一次编码，默认 18 留足画质。</summary>
    public int Quality { get; set; } = 18;

    /// <summary>视频编码器。null = 自动（优先 NVENC，失败回落 libx264）。</summary>
    public string? VideoCodec { get; set; }

    /// <summary>
    /// 逐块微调，单位**帧**（key = 镜号）。⑤ 页"轻量剪辑区"给用户肉眼微调用的。
    /// 正数 = 这一块多留几帧（画面更长、音频末尾静音更多）；负数 = 少留几帧。
    /// 不填 / 缺项 = 0（严格按该块音频段长度走）。
    /// </summary>
    public Dictionary<int, int>? TrimFrames { get; set; }

    public int SampleRate { get; set; } = 48000;

    /// <summary>进度回调（ffmpeg 原始输出行 + 本文件的阶段说明）。</summary>
    public IProgress<string>? Progress { get; set; }
}

/// <summary>出片结果。</summary>
public sealed class FilmAssembleResult
{
    public bool Success { get; set; }
    public string OutputPath { get; set; } = "";
    public string Error { get; set; } = "";
    public double ElapsedSec { get; set; }

    /// <summary>成片实测时长（探测输出得到）。</summary>
    public double FilmSec { get; set; }

    public string LogTail { get; set; } = "";

    /// <summary>这一次出片的过程记录，界面直接显示。</summary>
    public List<string> Steps { get; } = new();

    /// <summary>说明文件（成片_说明.json）的路径，写失败就是空。</summary>
    public string NotePath { get; set; } = "";
}

/// <summary>
/// 把渲染好的镜头 MP4 拼成成片。
/// </summary>
public static class FFmpegAssembler
{
    /// <summary>小于这个大小的 MP4 当作"没渲成功"（渲染中途挂掉会留一个零头文件）。</summary>
    public const long MinBlockBytes = 50 * 1024;

    /// <summary>音画长度差超过这个值就拒绝拼（对不齐的片子不如不出）。</summary>
    public const double MaxAudioDriftSec = 0.5;

    // ========================================================
    // 帧对齐：目标时长（每块向上取整到整帧）
    // ========================================================

    /// <summary>
    /// 把秒数**向上**取整到整帧，返回帧数。
    /// 向上（而不是就近）是有意的：保证目标 ≥ 音频段长度，
    /// 于是只需要"裁画面 / 垫静音"，**永远不会被迫去裁音频**。
    /// </summary>
    public static int UpToFrames(double sec, int fps)
    {
        if (fps <= 0) fps = 30;
        if (sec <= 0) return 0;
        // 减一点 epsilon：sec 正好是整帧时，别被浮点误差顶上一帧
        return (int)Math.Ceiling(sec * fps - 1e-6);
    }

    /// <summary>帧数 → 秒。写进 ffmpeg 的目标时长用它，保证是整帧的整数倍。</summary>
    public static double FramesToSec(int frames, int fps) =>
        frames / (double)(fps > 0 ? fps : 30);

    /// <summary>这一块的目标时长（秒）= 音频段长度向上取整到整帧，再加用户微调的帧数。</summary>
    public static double TargetSecOf(FilmBlock b, int fps, int adjustFrames = 0)
    {
        int f = UpToFrames(b.DesignSec, fps) + adjustFrames;
        if (f < 1) f = 1;
        return FramesToSec(f, fps);
    }

    // ========================================================
    // Δ 预览（④ 页「应用到下游」）—— 只读，不碰任何文件
    // ========================================================

    /// <summary>逐块 Δ 预览的一条：画面实测 vs 音频段长度。</summary>
    public sealed class BlockDelta
    {
        public int Index { get; set; }
        public string VideoPath { get; set; } = "";

        /// <summary>产物在不在（文件存在、且不是渲染中途挂掉留下的零头文件）。</summary>
        public bool HasVideo { get; set; }

        /// <summary>画面**实测**时长（秒）。没出片时为 0。</summary>
        public double ActualSec { get; set; }

        /// <summary>该块的**音频段**长度（秒）—— 组接时要 conform 到的基准。</summary>
        public double AudioSec { get; set; }

        /// <summary>正 = 画面偏长（待裁）；负 = 画面偏短（待冻帧补齐）。</summary>
        public double DeltaSec => ActualSec - AudioSec;

        /// <summary>帧对齐后的目标时长（conform 实际会做到的，恒 ≥ AudioSec）。</summary>
        public double TargetSec { get; set; }
    }

    /// <summary>
    /// 【T-06】逐块算 Δ —— ④ 页「应用到下游」按钮用。
    /// 每块一次 ffprobe（51 块约几秒）。★ **只读磁盘，不写、不改任何文件**。
    /// </summary>
    public static List<BlockDelta> PreviewDelta(List<FilmBlock> blocks, int fps = 30,
                                                IProgress<int>? progress = null)
    {
        var res = new List<BlockDelta>();
        if (blocks == null || blocks.Count == 0) return res;
        if (fps <= 0) fps = 30;

        int n = 0;
        foreach (var b in blocks.OrderBy(b => b.Index))
        {
            var d = new BlockDelta
            {
                Index = b.Index,
                VideoPath = b.VideoPath,
                AudioSec = b.DesignSec,
                TargetSec = TargetSecOf(b, fps),
            };

            try
            {
                var fi = string.IsNullOrWhiteSpace(b.VideoPath) ? null : new FileInfo(b.VideoPath);
                d.HasVideo = fi != null && fi.Exists && fi.Length >= MinBlockBytes;
                if (d.HasVideo)
                {
                    var probe = FFmpegDecoder.Probe(b.VideoPath);
                    d.ActualSec = probe.DurationSec > 0.01 ? probe.DurationSec : b.DesignSec;
                }
            }
            catch
            {
                d.HasVideo = false;
            }

            res.Add(d);
            progress?.Report(++n);
        }
        return res;
    }

    // ========================================================
    // 自检：拼之前先问"齐不齐、对不对得上"
    // ========================================================

    /// <summary>
    /// 出片前自检。会去探测每个块的实测时长（每块一次 ffprobe，51 块约几秒）。
    /// 探测失败不影响自检结论（退回用设计时长，只记一条警告）。
    /// </summary>
    public static FilmPlan Precheck(List<FilmBlock> blocks, List<FilmSegment> segments,
                                    IProgress<string>? progress = null)
    {
        var plan = new FilmPlan();
        plan.Blocks = blocks ?? new List<FilmBlock>();

        if (plan.Blocks.Count == 0)
        {
            plan.Problems.Add("没有可拼的镜头 —— 第④步还没有渲染产物（先出卡片、渲染，再回来组接）。");
            return plan;
        }

        plan.DesignTotalSec = plan.Blocks.Sum(b => b.DesignSec);
        plan.AudioTotalSec = segments?.Sum(s => s.LengthSec) ?? 0;

        // ---- 逐块查文件 ----
        var missing = new List<int>();
        double actualSum = 0;
        int probedFail = 0;

        foreach (var b in plan.Blocks.OrderBy(b => b.Index))
        {
            var fi = string.IsNullOrWhiteSpace(b.VideoPath) ? null : new FileInfo(b.VideoPath);
            b.SizeBytes = fi != null && fi.Exists ? fi.Length : 0;

            if (b.SizeBytes < MinBlockBytes)
            {
                missing.Add(b.Index);
                b.ActualSec = b.DesignSec;   // 缺的块不参与累积差计算
                continue;
            }

            var probe = FFmpegDecoder.Probe(b.VideoPath);
            if (probe.DurationSec > 0.01)
            {
                b.ActualSec = probe.DurationSec;
            }
            else
            {
                b.ActualSec = b.DesignSec;
                probedFail++;
            }
            actualSum += b.ActualSec;
        }

        if (missing.Count > 0)
        {
            string head = string.Join("、", missing.Take(12).Select(i => "#" + i.ToString("D3")));
            plan.Problems.Add($"缺 {missing.Count} 块渲染产物：{head}" +
                              (missing.Count > 12 ? $" …（共 {missing.Count} 块）" : "") +
                              $"。去第④步把这些块渲出来再组接。");
        }

        if (probedFail > 0)
            plan.Warnings.Add($"{probedFail} 块探测不到时长，已按设计时长计算（时间校验会不准）。");

        // ---- 音画对不上就不拼 ----
        if (plan.AudioTotalSec <= 0.01)
            plan.Problems.Add("算不出音轨长度 —— 没有生效的剪辑决策（先在第①步跑粗剪，或在精剪页点「应用到下游」）。");
        else if (Math.Abs(plan.AudioDriftSec) > MaxAudioDriftSec)
            plan.Problems.Add($"音轨 {plan.AudioTotalSec:F3}s 与画面时间轴 {plan.DesignTotalSec:F3}s 差 " +
                              $"{plan.AudioDriftSec:+0.000;-0.000}s —— 分镜不是按当前决策切的，" +
                              "去第③步重算口播稿并重切镜头，或重新渲染。");

        // ---- 画面补偿说明（V1.10.14）----
        //
        // 【为什么"实测 ≠ 设计"不再是警告】
        //   设计时长（DesignSec）自 V1.10.14 起 = **组接目标**：除了块自己的口播，
        //   还含"归还给它"的精剪气口（片头前导 / 落在块边界上的句间停顿 / 片尾余量）。
        //   已经渲好的画面是按"不含气口"的旧时长出的，所以差值是**预期的**，
        //   由 conform 逐块裁/补（偏短 → 尾部冻最后一帧）。这是 B 方案认下的交换，不是错。
        //   真正该让用户心里有数的是"补得多不多、补在哪几块"。
        var gaps = plan.Blocks
                       .Where(b => b.SizeBytes >= MinBlockBytes && b.ActualSec > 0.01)
                       .Select(b => (B: b, D: b.DesignSec - b.ActualSec))
                       .Where(x => Math.Abs(x.D) > 0.05)
                       .ToList();
        if (gaps.Count > 0)
        {
            // ★ 只报"定格"：画面比目标短、要冻最后一帧补齐 —— 这是观感上唯一能看出来的。
            //   反方向（画面比目标长、裁掉）大头是渲染时的安全帧 + 整帧余量，
            //   本来就是要去掉的，报出来只会吓人。
            var freeze = gaps.Where(x => x.D > 0.05).Select(x => x.D).ToList();
            if (freeze.Count > 0)
                plan.Warnings.Add($"画面补偿：{freeze.Count} 块画面比目标短，尾部会定格补帧（最长 {freeze.Max():F3}s）" +
                                  "—— 这是把精剪留的气口归还给画面块、又不重渲的代价");

            // 单块差得离谱（> 2s）才单独点名：多半是拿旧模板渲的
            foreach (var x in gaps.Where(x => Math.Abs(x.D) > 2.0).OrderBy(x => x.B.Index))
                plan.Warnings.Add($"#{x.B.Index:D3} 目标 {x.B.DesignSec:F3}s 实测 {x.B.ActualSec:F3}s 差 " +
                                  $"{x.D:+0.000;-0.000}s（差得有点多，确认一下是不是按当前设计渲的）");
        }

        plan.ActualTotalSec = actualSum;
        return plan;
    }

    // ========================================================
    // 出片
    // ========================================================

    /// <summary>
    /// 拼成片：渲染产物顺序拼接（无损直拷）+ 按决策重建音轨 + 挂在一起。
    /// </summary>
    public static FilmAssembleResult Assemble(string outputPath, List<FilmSegment> segments,
                                              FilmPlan plan, FilmAssembleOptions? opt = null)
    {
        var res = new FilmAssembleResult { OutputPath = outputPath };
        opt ??= new FilmAssembleOptions();
        res.Steps.Add($"成片输出：{outputPath}");

        string ffmpeg = FFmpegLocator.FFmpegPath
                        ?? throw new InvalidOperationException("找不到 ffmpeg，无法组接成片。");

        if (plan == null || !plan.Ok)
        {
            res.Error = plan == null ? "没有自检结果" : string.Join("；", plan.Problems);
            return res;
        }
        if (string.IsNullOrWhiteSpace(opt.SourcePath) || !File.Exists(opt.SourcePath))
        {
            res.Error = "找不到音轨来源（源录音）：" + (opt.SourcePath ?? "");
            return res;
        }
        if (segments == null || segments.Count == 0)
        {
            res.Error = "没有可用的音轨区间";
            return res;
        }

        var blocks = plan.Blocks.OrderBy(b => b.Index).ToList();
        var tempFiles = new List<string>();
        string conformDir = "";   // 阶段一的临时目录（逐块 conform 的产物），出片后整个删掉

        try
        {
            int fps = opt.Fps > 0 ? opt.Fps : 30;

            // ---- 1. 逐块算目标时长（帧对齐 + 用户微调）----
            var targets = new List<(FilmBlock B, double Target, int Frames)>();
            foreach (var b in blocks)
            {
                int adj = 0;
                if (opt.TrimFrames != null && opt.TrimFrames.TryGetValue(b.Index, out int a)) adj = a;
                int f = Math.Max(1, UpToFrames(b.DesignSec, fps) + adj);
                targets.Add((b, FramesToSec(f, fps), f));
            }
            double targetTotal = targets.Sum(t => t.Target);
            plan.TargetTotalSec = targetTotal;

            int adjusted = targets.Count(t => t.Frames != UpToFrames(t.B.DesignSec, fps));
            res.Steps.Add($"画面 conform：{blocks.Count} 块逐块对齐到各自音频段长度（按 {fps}fps 帧对齐）"
                          + $"，目标总长 {targetTotal:F3}s"
                          + (adjusted > 0 ? $"，其中 {adjusted} 块带人工微调" : ""));

            string codec = opt.VideoCodec
                           ?? (FFmpegExporter.IsNvencAvailable(ffmpeg) ? "h264_nvenc" : "libx264");

            // ---- 2. 阶段一：逐块 conform 到临时文件 ----
            //
            // 【为什么不是"一条命令带 51 个输入"】
            //   实测过：一条命令同时打开 51 个 MP4 输入 + concat 滤镜，
            //   进程内存峰值 **7.7 GB**。机器扛得住也不该这么用 ——
            //   出片时用户多半还开着浏览器和渲染器。
            //   拆成"每块一次独立调用"后，每个进程只开 1 进 1 出，内存掉到几百 MB，
            //   代价只是多几十次进程启动（总耗时几乎不变，还顺带有了逐块进度）。
            conformDir = Path.Combine(Path.GetTempPath(),
                                      "litecut_conform_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(conformDir);

            var conformed = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                var (b, T, F) = targets[i];
                string dst = Path.Combine(conformDir, $"c{i + 1:D3}.mp4");

                opt.Progress?.Report($"conform {i + 1}/{targets.Count} · #{b.Index:D3}");
                if (!ConformOne(ffmpeg, b.VideoPath, dst, F, T, fps, opt, codec, out string cerr))
                {
                    res.Error = $"第 #{b.Index:D3} 块 conform 失败：{cerr}";
                    return res;
                }
                conformed.Add(dst);
            }
            res.Steps.Add($"阶段一：{conformed.Count} 块按各自目标时长裁/补完毕（{codec}）");

            // ---- 3. 阶段二：拼接 + 音轨 ----
            //
            //  输入 0 = 拼接清单（画面，-c:v copy 直拼，不再重编码）
            //  输入 1 = 源录音（音轨来源）
            //  阶段一的输出参数完全一致（同编码器/同 crf/同 timescale），所以这里能直拷。
            string listPath = TempFile("concat", ".txt");
            tempFiles.Add(listPath);
            var sbList = new StringBuilder();
            foreach (var f in conformed) sbList.AppendLine("file '" + ConcatEscape(f) + "'");
            File.WriteAllText(listPath, sbList.ToString(), new UTF8Encoding(false));  // ★ 不能带 BOM

            // ★ V1.10.15：插片原声 —— 为每个用到的产物音轨申请一路 ffmpeg 输入。
            //   序号从 2 起（0 = 画面 concat 清单、1 = 源录音）。
            //   ★ 先按顺序定好"路径 → 序号"，再按同一顺序加 -i —— 两边顺序必须一致。
            var videoAudioPaths = segments
                .Where(s => s.VideoAudioPath.Length > 0)
                .Select(s => s.VideoAudioPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();
            var videoAudioInputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < videoAudioPaths.Count; i++)
                videoAudioInputs[videoAudioPaths[i]] = 2 + i;

            string graphPath = TempFile("audiograph", ".txt");
            tempFiles.Add(graphPath);
            File.WriteAllText(graphPath, BuildAudioGraph(targets, segments, opt, 1, videoAudioInputs),
                              new UTF8Encoding(false));
            res.Steps.Add($"阶段二：画面无损拼接 + 音轨 {segments.Count} 段重建，零处理"
                          + "（不拉伸、不变调；每块末尾垫 <1 帧静音做帧对齐）"
                          + (videoAudioPaths.Count > 0
                              ? $"；其中插片原声 {videoAudioPaths.Count} 段取自渲染产物"
                              : ""));

            var args = new StringBuilder();
            args.Append("-hide_banner -y -stats -stats_period 2 ");
            args.Append("-f concat -safe 0 -i \"").Append(listPath).Append("\" ");
            args.Append("-i \"").Append(opt.SourcePath).Append("\" ");
            foreach (var p in videoAudioPaths) args.Append("-i \"").Append(p).Append("\" ");
            args.Append("-filter_complex_script \"").Append(graphPath).Append("\" ");
            args.Append("-map 0:v -map \"[aout]\" ");
            args.Append("-c:v copy ");
            args.Append("-c:a aac -b:a ").Append(opt.AudioBitrate).Append(" ");
            args.Append("-movflags +faststart ");
            args.Append("\"").Append(outputPath).Append("\"");

            var sw = Stopwatch.StartNew();
            bool ok = FFmpegExporter.RunProcess(ffmpeg, args.ToString(), opt.Progress,
                                                out string stderr, out int exitCode);
            sw.Stop();
            res.ElapsedSec = sw.Elapsed.TotalSeconds;
            res.LogTail = Tail(stderr, 1200);

            if (!ok || exitCode != 0 || !File.Exists(outputPath))
            {
                res.Error = $"ffmpeg 退出码 {exitCode}";
                return res;
            }

            // ---- 4. 校验：成片时长该等于帧对齐后的目标总长 ----
            var outProbe = FFmpegDecoder.Probe(outputPath);
            res.FilmSec = outProbe.DurationSec;
            res.Success = res.FilmSec > 0.5;

            if (res.Success && Math.Abs(res.FilmSec - targetTotal) > 0.2)
            {
                res.Steps.Add($"⚠ 成片 {res.FilmSec:F3}s 与目标总长 {targetTotal:F3}s 差 " +
                              $"{res.FilmSec - targetTotal:+0.000;-0.000}s，看一下日志。");
            }
            else if (res.Success)
            {
                res.Steps.Add($"✓ 时长对账：成片 {res.FilmSec:F3}s = 目标 {targetTotal:F3}s"
                              + $"（音频 {plan.AudioTotalSec:F3}s ＋ 帧对齐余量 "
                              + $"{targetTotal - plan.AudioTotalSec:F3}s）");
            }

            res.Steps.Add($"成片时长 {res.FilmSec:F3}s（{Fmt(res.FilmSec)}）· 耗时 {res.ElapsedSec:F1}s");
            res.Success = true;

            // ---- 5. 留痕：写一份"这片子是怎么拼出来的" ----
            res.NotePath = WriteNote(outputPath, plan, segments, opt, res);
            if (res.NotePath.Length > 0) res.Steps.Add("已写出片说明：" + res.NotePath);

            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
        finally
        {
            foreach (var f in tempFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            // 阶段一的临时块：整个目录删掉。不清理的话，几十上百 MB 会一直躺在 TEMP 里。
            if (conformDir.Length > 0)
            {
                try { if (Directory.Exists(conformDir)) Directory.Delete(conformDir, true); } catch { }
            }
        }
    }

    // ========================================================
    // 阶段一：单块 conform（一次调用只处理一块）
    // ========================================================
    //
    //   [0:v]setpts=PTS-STARTPTS,trim=end_frame=F,
    //        tpad=stop_mode=clone:stop_duration=T,trim=end_frame=F,
    //        setpts=PTS-STARTPTS,fps=30
    //
    // 【为什么 trim 两遍】
    //   第一遍切掉超出的尾部（画面偏长是常态：多了安全帧 + 整帧余量）；
    //   tpad 再补一段冻结帧（画面偏短的极少数情况）；
    //   最后一遍 trim 精确收到 F 帧 —— 无论原块偏长还是偏短，出来都一样长。
    //   两遍都很便宜，换来的是"绝对不可能短"。
    //
    // 【★ 用 end_frame 而不是 duration】
    //   目标时长是整帧的整数倍，写成秒就有浮点：k/30 的小数第 7 位舍入到 6 位时
    //   可能**向上**，那样 trim=duration 会多留一帧（实测踩过）。按帧数裁就没有歧义。
    //
    // 【-video_track_timescale 固定 15360】
    //   阶段二要用 concat 解复用器 -c:v copy 直拼，各段时间基必须一致。
    private static bool ConformOne(string ffmpeg, string src, string dst, int frames, double target,
                                   int fps, FilmAssembleOptions opt, string codec, out string err)
    {
        var a = new StringBuilder();
        a.Append("-hide_banner -y -nostdin -loglevel error ");
        a.Append("-i \"").Append(src).Append("\" ");
        a.Append("-filter_complex \"[0:v]setpts=PTS-STARTPTS,trim=end_frame=").Append(frames)
         .Append(",tpad=stop_mode=clone:stop_duration=").Append(F4(target))
         .Append(",trim=end_frame=").Append(frames)
         .Append(",setpts=PTS-STARTPTS,fps=").Append(fps).Append("[v]\" ");
        a.Append("-map \"[v]\" -an ");
        if (codec == "h264_nvenc")
            a.Append($"-c:v h264_nvenc -preset p4 -cq {opt.Quality} ");
        else
            a.Append($"-c:v libx264 -preset veryfast -crf {opt.Quality} ");
        a.Append("-pix_fmt yuv420p ");                  // 不加 QuickTime/微信打不开
        a.Append("-r ").Append(fps).Append(' ');
        a.Append("-video_track_timescale 15360 ");
        a.Append("-movflags +faststart ");
        a.Append("\"").Append(dst).Append("\"");

        FFmpegExporter.RunProcess(ffmpeg, a.ToString(), null, out string se, out int code);
        err = Tail(se, 600);
        return code == 0 && File.Exists(dst) && new FileInfo(dst).Length > 1024;
    }

    // ========================================================
    // 阶段二：音轨滤镜图（画面走 concat 解复用器，不在这里）
    // ========================================================
    //
    // 图长这样（srcInput = 源录音的输入序号；N 块）：
    //
    //   # 每块：把落在它窗口里的精剪区间裁出来、拼起来
    //   [1:a]atrim=start=S:end=E,asetpts=PTS-STARTPTS[a0];
    //   [a0][a1]concat=n=2:v=0:a=1[a2];
    //   # 末尾垫静音，补到这一块的目标时长
    //   [a2]apad,atrim=duration=T0,asetpts=PTS-STARTPTS[ap0];
    //   ...
    //   [ap0][ap1]...[apN]concat=n=N:v=0:a=1[cat];
    //   [cat]pan=stereo|c0=c0|c1=c0[aout]
    //
    // 【★ 音频全程零处理】不拉伸、不变调、不重采样 —— 只做"裁剪 + 拼接 + 末尾垫静音"。
    //   末尾那段静音是帧对齐用的（详见文件头）；内容一个采样都没动。
    //
    // 【为什么 apad 不带参数】apad 无参 = 无限补静音，再用 atrim 收到 T。
    //   这样不必自己算"该补多少秒"，也躲开 whole_dur / pad_dur 对新版本的依赖。
    //
    // 【为什么音轨还要在块内再 concat 一层】
    //   一块的 5~9 秒里可能横跨好几条精剪区间（块边界常常切在区间中间）。
    //   先把落在这块窗口里的每个子区间裁出来、拼成"这块的声音"，再垫静音 ——
    //   顺序反了（先垫再切）就对不上了。
    // ★ V1.10.15：videoAudioInputs = 插片原声用的额外输入表（产物 MP4 路径 → ffmpeg 输入序号）；
    //   为 null 或查不到该路径 = 那一段退回静音。
    private static string BuildAudioGraph(List<(FilmBlock B, double Target, int Frames)> targets,
                                          List<FilmSegment> segments, FilmAssembleOptions opt,
                                          int srcInput,
                                          IReadOnlyDictionary<string, int>? videoAudioInputs = null)
    {
        int sr = opt.SampleRate > 0 ? opt.SampleRate : 48000;
        string srcTag = "[" + srcInput + ":a]";

        // 各条精剪区间在**音频时间轴**上的位置（依序首尾相接；精剪挪过块的顺序，必须按成片顺序排）
        var film = new List<(double Start, double End, FilmSegment Seg)>();
        double cursor0 = 0;
        foreach (var s in segments)
        {
            film.Add((cursor0, cursor0 + s.LengthSec, s));
            cursor0 += s.LengthSec;
        }

        var sb = new StringBuilder();
        var aLabels = new List<string>();
        int k = 0;   // 音频片段节点编号

        for (int bi = 0; bi < targets.Count; bi++)
        {
            var (b, T, _) = targets[bi];
            double bs = b.DesignStartSec, be = b.DesignEndSec;
            if (be - bs <= 0.001) continue;

            var parts = new List<string>();

            // 这一段（插片/剪空的块）用静音补齐 len 秒。
            void AppendSilence(double len)
            {
                if (len <= 0.001) return;
                string lbl = "a" + (k++).ToString(CultureInfo.InvariantCulture);
                sb.Append("anullsrc=channel_layout=mono:sample_rate=").Append(sr)
                  .Append(",atrim=start=0:end=").Append(F4(len))
                  .Append(",asetpts=PTS-STARTPTS[").Append(lbl).Append("];");
                parts.Add(lbl);
            }

            foreach (var (fs, fe, seg) in film)
            {
                double s0 = Math.Max(bs, fs), s1 = Math.Min(be, fe);
                if (s1 - s0 <= 0.0015) continue;          // 落在块外的、太短的碎片不要

                // ★ 插片落在这一块里 → 这一段就是**静音**（没有对应的源录音可裁）
                if (seg.IsSilence)
                {
                    AppendSilence(s1 - s0);
                    continue;
                }

                // ---- ★ 插片原声：从该镜产物的音轨里取这一段（V1.10.15 / T-25）----
                //
                //  【为什么用 aformat 强转单声道】
                //    源录音是单声道，后面那条大 concat 要求所有输入声道布局一致；
                //    产物音轨是 aac 立体声 48k —— 不统一，concat 会直接报错。
                //    顺手把采样率也统一（源可能是 44.1k，产物固定 48k）。
                if (seg.VideoAudioPath.Length > 0)
                {
                    if (videoAudioInputs == null ||
                        !videoAudioInputs.TryGetValue(seg.VideoAudioPath, out int vin))
                    {
                        // 查不到输入（产物被删了之类）→ 退回静音。
                        // 宁可这一段哑掉，也不能让音轨短一截 —— 那会让整条时间轴错位。
                        AppendSilence(s1 - s0);
                        continue;
                    }

                    double a0 = Math.Max(0, seg.VideoAudioStartSec + (s0 - fs));
                    double a1 = seg.VideoAudioStartSec + (s1 - fs);
                    string vlab = "a" + (k++).ToString(CultureInfo.InvariantCulture);
                    sb.Append('[').Append(vin.ToString(CultureInfo.InvariantCulture))
                      .Append(":a]atrim=start=").Append(F4(a0))
                      .Append(":end=").Append(F4(a1))
                      .Append(",asetpts=PTS-STARTPTS")
                      .Append(",aformat=channel_layouts=mono:sample_rates=")
                      .Append(sr.ToString(CultureInfo.InvariantCulture));
                    if (seg.Gain != 1.0)
                        sb.Append(",volume=").Append(seg.Gain.ToString("F6", CultureInfo.InvariantCulture));
                    sb.Append('[').Append(vlab).Append("];");
                    parts.Add(vlab);
                    continue;
                }

                // 音频时间轴 → 源素材时间
                double srcA = seg.SourceStartSec + (s0 - fs);
                double srcB = seg.SourceStartSec + (s1 - fs);

                var chain = new StringBuilder();
                chain.Append(srcTag).Append("atrim=start=").Append(F4(srcA))
                     .Append(":end=").Append(F4(srcB))
                     .Append(",asetpts=PTS-STARTPTS");

                if (seg.Gain != 1.0)
                    chain.Append(",volume=").Append(seg.Gain.ToString("F6", CultureInfo.InvariantCulture));

                // 淡化只在该段真正的两端生效 —— 一段被块边界切开时，
                // 切口那边不该出现淡入淡出（那会听出一次"喘气"）。
                if (seg.FadeInSec > 0.001 && s0 - fs < 0.0015)
                    chain.Append(",afade=t=in:st=0:d=").Append(F4(seg.FadeInSec));
                if (seg.FadeOutSec > 0.001 && fe - s1 < 0.0015)
                {
                    double st = Math.Max(0, seg.LengthSec - seg.FadeOutSec - (s0 - fs));
                    chain.Append(",afade=t=out:st=").Append(F4(st))
                         .Append(":d=").Append(F4(seg.FadeOutSec));
                }

                string lab = "a" + (k++).ToString(CultureInfo.InvariantCulture);
                chain.Append('[').Append(lab).Append("];");
                sb.Append(chain);
                parts.Add(lab);
            }

            string raw;
            if (parts.Count == 0)
            {
                // 这一块的音频被剪光了 → 整块静音（别让整条音轨短一截）
                raw = "a" + (k++).ToString(CultureInfo.InvariantCulture);
                sb.Append("anullsrc=channel_layout=mono:sample_rate=").Append(sr)
                  .Append(",atrim=start=0:end=").Append(F4(be - bs))
                  .Append(",asetpts=PTS-STARTPTS[").Append(raw).Append("];");
            }
            else if (parts.Count == 1)
            {
                raw = parts[0];
            }
            else
            {
                raw = "a" + (k++).ToString(CultureInfo.InvariantCulture);
                foreach (var p in parts) sb.Append('[').Append(p).Append(']');
                sb.Append("concat=n=").Append(parts.Count).Append(":v=0:a=1[")
                  .Append(raw).Append("];");
            }

            // ---- 末尾垫静音补到目标时长（音频内容一个采样都没动）----
            string fin = "ap" + bi;
            sb.Append('[').Append(raw).Append("]apad,atrim=duration=")
              .Append(T.ToString("F6", CultureInfo.InvariantCulture))
              .Append(",asetpts=PTS-STARTPTS[").Append(fin).Append("];");
            aLabels.Add("[" + fin + "]");
        }

        if (aLabels.Count == 0)
        {
            sb.Append("anullsrc=channel_layout=mono:sample_rate=").Append(sr)
              .Append(":duration=0.1,pan=stereo|c0=c0|c1=c0[aout]");
            return sb.ToString();
        }

        foreach (var l in aLabels) sb.Append(l);
        sb.Append("concat=n=").Append(aLabels.Count).Append(":v=0:a=1[cat];");

        // ★ 单位增益复制，不是下混 —— 下混会偷偷把整体压低 3dB
        sb.Append("[cat]pan=stereo|c0=c0|c1=c0[aout]");
        return sb.ToString();
    }

    // ========================================================
    // 留痕：成片_说明.json
    // ========================================================
    //
    // 几个月后回头看这支片子，要能答出"当时用的是哪份决策、多少块、
    // 音画差多少、拉伸过没有"。只留一个 mp4 是答不出来的。
    private static string WriteNote(string outputPath, FilmPlan plan, List<FilmSegment> segments,
                                    FilmAssembleOptions opt, FilmAssembleResult res)
    {
        try
        {
            string p = Path.Combine(Path.GetDirectoryName(outputPath) ?? "",
                                    Path.GetFileNameWithoutExtension(outputPath) + "_说明.json");
            var note = new
            {
                成片 = Path.GetFileName(outputPath),
                生成时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                成片时长秒 = Math.Round(res.FilmSec, 3),
                出片耗时秒 = Math.Round(res.ElapsedSec, 1),
                画面 = new
                {
                    来源 = "第④步渲染产物（逐块 conform 到各块目标时长后拼接）",
                    块数 = plan.Blocks.Count,
                    设计总长秒 = Math.Round(plan.DesignTotalSec, 3),
                    实测总长秒 = Math.Round(plan.ActualTotalSec, 3),
                    目标总长秒 = Math.Round(plan.TargetTotalSec, 3),
                    整帧累积差秒 = Math.Round(plan.DriftSec, 3),
                    各块 = plan.Blocks.OrderBy(b => b.Index).Select(b => new
                    {
                        镜号 = b.Index,
                        文件 = Path.GetFileName(b.VideoPath),
                        设计秒 = Math.Round(b.DesignSec, 3),
                        实测秒 = Math.Round(b.ActualSec, 3),
                        目标秒 = Math.Round(TargetSecOf(b, opt.Fps > 0 ? opt.Fps : 30), 3),
                    }).ToList(),
                },
                音轨 = new
                {
                    来源 = Path.GetFileName(opt.SourcePath),
                    段数 = segments.Count,
                    总长秒 = Math.Round(plan.AudioTotalSec, 3),
                    处理 = "零处理（不拉伸、不变调、不重采样）",
                    帧对齐静音秒 = Math.Round(plan.TargetTotalSec - plan.AudioTotalSec, 3),
                    说明 = "画面 conform 到各块音频段长度；音频末尾垫不到一帧的静音做帧对齐",
                },
                音画差秒 = Math.Round(plan.TargetTotalSec - plan.AudioTotalSec, 3),
                警告 = plan.Warnings,
            };
            File.WriteAllText(p, JsonSerializer.Serialize(note,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            return p;
        }
        catch
        {
            return "";   // 说明写不出来不该让出片算失败
        }
    }

    // ========================================================
    // 小工具
    // ========================================================

    private static string TempFile(string tag, string ext) =>
        Path.Combine(Path.GetTempPath(),
                     "litecut_" + tag + "_" + Guid.NewGuid().ToString("N")[..8] + ext);

    /// <summary>concat 清单里的路径转义：引号要转义，反斜杠统一成正斜杠更稳。</summary>
    private static string ConcatEscape(string path) =>
        path.Replace('\\', '/').Replace("'", @"'\''");

    private static string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    private static string Tail(string s, int n) => s.Length > n ? s[^n..] : s;

    /// <summary>"383.77 秒" → "6分23.8秒"，日志里好读。</summary>
    public static string Fmt(double sec)
    {
        if (sec < 60) return $"{sec:F1} 秒";
        int m = (int)(sec / 60);
        return $"{m} 分 {sec - m * 60:F1} 秒";
    }
}
