// ============================================================
// TranscriptAligner.cs - 转写稿到底对不对得上这条音频？
// ============================================================
//
// 【为什么必须有这一层】
//
//   转写稿不是从音频里"算"出来的唯一路子 —— 我们允许它由外部 Agent 生成后导入。
//   这就带来一个波形状永远不会有的风险：
//
//     **时间码可能是编的。**
//
//   大语言模型填 JSON 时，"第几句在第几秒"这种信息它不知道，
//   但它会填一个看起来合理的数字（比如按字数平均到全片）。
//   这种稿子**格式完全合法、JSON 校验全过、内容读起来也对**，
//   唯独每一句的时间都是错的。
//
//   如果直接拿它下刀，结果是：字幕错位、删掉的是旁边那句话。
//   而且很难发现 —— 用户得听完整片才察觉"好像剪错地方了"。
//
//   所以必须在动手之前先问一句：**这份稿子跟这条音频对得上吗？**
//
// 【怎么验 —— 不用再跑一次转写】
//
//   答案就在手边：我们有**包络**（每 5 毫秒一个音量值）。
//   既然稿子说"第 10 到第 13 秒在说话"，那就去看这 3 秒的音量 ——
//   如果全程静音，那这句的时间码就是错的。
//
//   一句话：**文字负责说"那会儿在说话"，波形负责说"那会儿到底有没有声音"。**
//   两者对不上，就是稿子有问题。
//
// 【验出来之后怎么办 —— 三级处理】
//
//   对齐很好    → 正常走"波形 + 语义"双轨
//   有整体漂移  → 估算出一个统一的偏移量，整体校正后再用
//   对得很差    → 自动降级到"纯波形模式"，并在界面上明确告诉用户为什么
//
//   第三级是关键：**宁可少剪，不可错剪。**
//   这也是为什么整个工作台不依赖 whisper 也能跑 —— 少一层能力，不是不能用。
//
// ============================================================

using System;
using System.Collections.Generic;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>一份转写稿与音频的对齐质量报告。</summary>
public sealed class AlignmentReport
{
    /// <summary>综合评分（0~1）。1 = 完全吻合。</summary>
    public double Score { get; set; }

    /// <summary>这份稿子能不能用在语义层。false = 建议降级为纯波形。</summary>
    public bool Usable { get; set; }

    /// <summary>总共检查了多少句。</summary>
    public int SegmentCount { get; set; }

    /// <summary>其中"确实对应着人声"的句数。</summary>
    public int VoicedCount { get; set; }

    /// <summary>其中"说是人在说话、实际是静音"的句数（最可疑的一类）。</summary>
    public int SilentCount { get; set; }

    /// <summary>有人声的占比（VoicedCount / SegmentCount）。</summary>
    public double VoicedRatio { get; set; }

    /// <summary>
    /// 估算的整体时间偏移（秒）。
    /// 正数 = 稿子的时间普遍比实际**晚**（要把稿子往前移才对得上）。
    /// </summary>
    public double GlobalShiftSec { get; set; }

    /// <summary>是否建议应用这个偏移量来校正。</summary>
    public bool ShouldApplyShift { get; set; }

    /// <summary>
    /// 偏移量是否撞到了搜索上限。
    /// 撞上限 = "连偏移了多少都测不准" —— 这时即使校正了也不该用。
    /// </summary>
    public bool ShiftSaturated { get; set; }

    /// <summary>具体的问题清单（界面上直接展示给用户看）。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>人类可读的一句话摘要。</summary>
    public string Summary =>
        Usable
            ? $"对齐良好（{VoicedRatio:P0} 的句子对得上人声{(Math.Abs(GlobalShiftSec) > 0.25 ? $"，整体偏移 {GlobalShiftSec:F2}s 已校正" : "")}）"
            : $"对齐不可靠（{VoicedRatio:P0} 的句子对得上人声）→ 已自动降级为纯波形粗剪";
}

/// <summary>转写稿对齐校验。</summary>
public static class TranscriptAligner
{
    /// <summary>低于这个分数就认为稿子不可用（默认 0.60）。</summary>
    public static double DefaultMinScore { get; set; } = 0.60;

    /// <summary>有多少比例的句子"完全落在音频之外"就直接否决（默认 0.20）。</summary>
    public static double MaxOutOfRangeRatio { get; set; } = 0.20;

    /// <summary>整体偏移的搜索上限（默认 30 秒）。</summary>
    public static double MaxShiftSec { get; set; } = 30.0;

    /// <summary>偏移超过多少秒才值得校正（默认 0.25 秒）。低于此不管，误差在可接受范围内。</summary>
    public static double DefaultShiftApplyThreshold { get; set; } = 0.25;

    /// <summary>
    /// 校验一份转写稿跟当前音频是否对得上。
    /// </summary>
    /// <param name="transcript">外部拿来的转写稿</param>
    /// <param name="env">音频包络（dBFS）</param>
    /// <param name="frameSec">包络每帧多少秒</param>
    /// <param name="noiseDb">低于多少 dB 算没声音</param>
    /// <param name="audioDurationSec">音频实际总时长</param>
    /// <param name="minScore">低于这个分数判定为不可用</param>
    public static AlignmentReport Validate(
        Transcript? transcript,
        double[] env,
        double frameSec,
        double noiseDb,
        double audioDurationSec,
        double minScore = -1)
    {
        double threshold = minScore > 0 ? minScore : DefaultMinScore;
        var report = new AlignmentReport();

        if (transcript == null || transcript.Segments == null || transcript.Segments.Count == 0)
        {
            report.Warnings.Add("没有转写稿可用");
            report.Usable = false;
            return report;
        }

        report.SegmentCount = transcript.Segments.Count;

        // ---- 检查 1：稿子标注的音频时长，跟实际差多少 ----
        if (transcript.AudioDurationSec > 0.1)
        {
            double durErr = Math.Abs(transcript.AudioDurationSec - audioDurationSec);
            if (durErr > Math.Max(1.0, audioDurationSec * 0.02))
            {
                report.Warnings.Add(
                    $"稿子标注的总时长 {transcript.AudioDurationSec:F1}s，实际音频 {audioDurationSec:F1}s（差 {durErr:F1}s）——可能配错了文件");
            }
        }

        // ---- 检查 2：有多少句的时间完全落在音频之外 ----
        //
        // 这是最硬的证据，跟后面的"分数"无关：
        // 一句"在第 60 秒说的话"，而音频只有 55 秒 —— 这不是误差，这是编的。
        // 实测过一个典型案例：外部 Agent 交回来的稿子，60% 的句子都在音频长度之外，
        // 但"能对得上人声的比例"恰好等于阈值，差点被放行。
        // 所以这里单独设一道否决，不参与打分。
        int outOfRange = 0;
        foreach (var seg in transcript.Segments)
        {
            if (seg.StartSec >= audioDurationSec - 0.01 || seg.EndSec <= 0.01) outOfRange++;
        }
        double outOfRangeRatio = (double)outOfRange / report.SegmentCount;

        if (outOfRange > 0)
            report.Warnings.Add($"有 {outOfRange} 句的时间完全落在音频之外（音频只有 {audioDurationSec:F1}s）");

        if (outOfRangeRatio > MaxOutOfRangeRatio)
        {
            report.Warnings.Add(
                $"超过 {MaxOutOfRangeRatio:P0} 的句子时间落在音频之外 → 判定这份稿子配不上这条音频");
            report.Score = 0;
            report.VoicedRatio = 0;
            report.SilentCount = outOfRange;
            report.Usable = false;
            return report;
        }

        // ---- 检查 3：先校正整体偏移，再评分 ----
        //
        // 顺序很重要，这是实测改出来的：
        // 如果先拿"没校正的稿子"去算每段有没有人声，整体平移的稿子会被判成
        // "大部分都对不上"；但那种稿子其实只要平移一下就完全正确了。
        // 所以必须**先校正、后评估**。
        double shift = EstimateGlobalShift(transcript, env, frameSec, noiseDb, audioDurationSec);
        report.GlobalShiftSec = Math.Round(shift, 3);
        report.ShiftSaturated = false;

        if (Math.Abs(shift) >= DefaultShiftApplyThreshold)
        {
            report.ShouldApplyShift = true;
            report.Warnings.Add($"检测到整体时间偏移约 {shift:F2}s —— 已自动校正");
        }

        // 偏移量撞到搜索边界 → 说明真实偏移可能比这还大，这次校正不可信
        if (Math.Abs(shift) >= MaxShiftSec - 0.01)
        {
            report.ShiftSaturated = true;
            report.Warnings.Add($"偏移量已达到搜索上限 {MaxShiftSec}s，真实偏移可能更大");
        }

        Transcript working = ShouldShift(report)
            ? ApplyShift(transcript, report.GlobalShiftSec)
            : transcript;

        // ---- 检查 4：用校正后的稿子，逐句看"这句的时间里有没有声音" ----
        foreach (var seg in working.Segments)
        {
            double a = seg.StartSec, b = seg.EndSec;
            if (b <= a) { report.SilentCount++; continue; }
            if (a >= audioDurationSec || b <= 0) { report.SilentCount++; continue; }

            double voiced = VoicedFrameRatio(env, frameSec, Math.Max(0, a), Math.Min(audioDurationSec, b), noiseDb);
            if (voiced >= 0.25) report.VoicedCount++;
            else report.SilentCount++;
        }

        report.VoicedRatio = report.SegmentCount > 0 ? (double)report.VoicedCount / report.SegmentCount : 0;

        if (report.SilentCount > 0 && report.VoicedCount > 0)
        {
            report.Warnings.Add($"有 {report.SilentCount} 句的时间码与音频对不上（这些句子将被跳过，由波形层处理）");
        }

        // ---- 综合打分 ----
        double score = report.VoicedRatio;

        // 偏移量饱和是个明确的坏信号 —— 它意味着"我连偏移多少都测不准"
        if (report.ShiftSaturated) score *= 0.5;

        report.Score = Math.Round(score, 3);
        report.Usable = score >= threshold && report.VoicedCount >= 2 && !report.ShiftSaturated;

        if (!report.Usable)
        {
            report.Warnings.Add($"对齐分数 {score:P0} 低于阈值 {threshold:P0}，语义层已关闭，仅使用波形层");
        }

        return report;
    }

    /// <summary>是否应该应用偏移校正。</summary>
    private static bool ShouldShift(AlignmentReport r) => r.ShouldApplyShift && !r.ShiftSaturated;

    /// <summary>某个时间区间内，有多少比例的帧是有声音的。</summary>
    private static double VoicedFrameRatio(double[] env, double frameSec, double fromSec, double toSec, double noiseDb)
    {
        int i0 = Math.Clamp((int)(fromSec / frameSec), 0, env.Length);
        int i1 = Math.Clamp((int)(toSec / frameSec), 0, env.Length);
        int count = i1 - i0;
        if (count <= 0) return 0;

        int voiced = 0;
        for (int i = i0; i < i1; i++)
            if (env[i] > noiseDb) voiced++;

        return (double)voiced / count;
    }

    /// <summary>
    /// 估算转写稿整体比音频晚/早了多少秒。
    ///
    /// 做法：拿"稿子说这里有人声"的掩码 和 "波形说这里有声"的掩码做互相关，
    ///      找重合度最高的那个位移。
    ///
    /// 为什么这一步值得写：
    ///   外部 Agent 常见的一种错不是"每句都错"，而是**整体平移** ——
    ///   比如片头有 3 秒环境音没算进去，导致每句话都比实际早 3 秒。
    ///   这种错误看起来"每句都差一点"，但整体校一下就全对了。
    /// </summary>
    public static double EstimateGlobalShift(
        Transcript transcript,
        double[] env,
        double frameSec,
        double noiseDb,
        double audioDurationSec,
        double maxShiftSec = -1)
    {
        double maxShift = maxShiftSec > 0 ? maxShiftSec : MaxShiftSec;
        if (env.Length == 0) return 0;

        // 把"稿子说有话"和"波形说有声"都离散成帧级布尔数组
        var ideal = new bool[env.Length];
        foreach (var seg in transcript.Segments)
        {
            int i0 = Math.Clamp((int)(seg.StartSec / frameSec), 0, env.Length);
            int i1 = Math.Clamp((int)(seg.EndSec / frameSec), 0, env.Length);
            for (int i = i0; i < i1; i++) ideal[i] = true;
        }

        var actual = new bool[env.Length];
        for (int i = 0; i < env.Length; i++) actual[i] = env[i] > noiseDb;

        // ---- 两阶段搜索 ----
        //
        // 为什么要分两阶段：外部 Agent 犯的错幅度可能很大（实测过整体平移 20 秒的）。
        // 如果一开始就用细步长（0.02s）在大范围里穷举，光校对就要跑上亿次。
        // 所以先粗后精：
        //   粗筛：0.25 秒一步，覆盖 ±maxShift（30 秒只要约 240 步）
        //   细化：在粗筛的最佳位置附近 ±0.4 秒，用 0.02 秒的步长精找
        // 精度不变，速度快两个数量级。
        double coarse = CoarseSearch(ideal, actual, env.Length, frameSec, maxShift, 0.25);
        double fine = CoarseSearch(ideal, actual, env.Length, frameSec, maxShift, 0.02,
                                   coarse - 0.4, coarse + 0.4);
        return fine;
    }

    /// <summary>
    /// 在给定范围内按指定步长做互相关，返回匹配率最高的位移（秒）。
    /// </summary>
    private static double CoarseSearch(bool[] ideal, bool[] actual, int len, double frameSec,
                                       double maxShiftSec, double stepSec,
                                       double? loSec = null, double? hiSec = null)
    {
        double loBound = loSec ?? -maxShiftSec;
        double hiBound = hiSec ?? maxShiftSec;

        int step = Math.Max(1, (int)Math.Round(stepSec / frameSec));
        double stepActual = step * frameSec;

        int loSteps = (int)Math.Ceiling(loBound / stepActual);
        int hiSteps = (int)Math.Floor(hiBound / stepActual);

        // 抽稀比对：我们要找的是宏观位移，不是帧级精度
        const int sampleEvery = 4;
        double minCount = len / (double)sampleEvery * 0.5;

        int bestShiftSteps = 0;
        double bestScore = double.MinValue;

        for (int s = loSteps; s <= hiSteps; s++)
        {
            int shift = s * step;
            double hit = 0;
            double count = 0;

            for (int i = 0; i < len; i += sampleEvery)
            {
                int j = i + shift;
                if (j < 0 || j >= len) continue;
                count++;
                if (ideal[i] == actual[j]) hit++;
            }
            if (count < minCount) continue;   // 平移太多导致样本不足，跳过

            double score = hit / count;
            if (score > bestScore) { bestScore = score; bestShiftSteps = shift; }
        }

        return bestShiftSteps * frameSec;
    }

    /// <summary>
    /// 把整个稿子的时间平移一段。ShouldApplyShift 为 true 时用。
    /// 返回一个**新的** Transcript，不动原对象（便于对照）。
    /// </summary>
    public static Transcript ApplyShift(Transcript transcript, double shiftSec)
    {
        if (Math.Abs(shiftSec) < 1e-6) return transcript;

        var copy = new Transcript
        {
            SchemaVersion = transcript.SchemaVersion,
            Language = transcript.Language,
            Engine = transcript.Engine,
            Model = transcript.Model,
            AudioPath = transcript.AudioPath,
            AudioDurationSec = transcript.AudioDurationSec,
            FullText = transcript.FullText,
        };

        foreach (var seg in transcript.Segments)
        {
            var ns = new TranscriptSegment
            {
                Index = seg.Index,
                StartSec = Math.Max(0, seg.StartSec + shiftSec),
                EndSec = Math.Max(0, seg.EndSec + shiftSec),
                Text = seg.Text,
            };
            foreach (var w in seg.Words)
            {
                ns.Words.Add(new TranscriptWord
                {
                    Text = w.Text,
                    StartSec = Math.Max(0, w.StartSec + shiftSec),
                    EndSec = Math.Max(0, w.EndSec + shiftSec),
                    Confidence = w.Confidence,
                });
            }
            copy.Segments.Add(ns);
        }

        return copy;
    }

    // ============================================================
    // 切点吸附
    // ============================================================

    /// <summary>
    /// 把一个粗略的时间点，在附近的**单侧**范围内吸附到真正安静的位置。
    ///
    /// 【⚠️ 这个方法取代了原来的 Snap —— 原来的设计有坑】
    ///
    ///   前一版是在 ±window 的**双侧**窗口里找最静点。听起来合理，
    ///   但实测翻车了：合成素材上一处「半截重说」的起点在 18.17s，
    ///   而 18.05s 恰好是上一句说完后的静音 —— 于是切点"噌"地跳到了 18.05，
    ///   生生把删除范围往前扩了 0.12 秒，顺带吞掉了前一个 DocContents 的尾巴。
    ///
    ///   根因：双侧搜索允许切点**向外跑**，而向外跑就是"多删内容"。
    ///   这跟整套设计"拿不准就保留"的原则是反的。
    ///
    /// 【现在的做法：只向内收】
    ///
    ///   起点 a：只在 [a, a+window] 里找（只会让起点变晚 → 少删）
    ///   终点 b：只在 [b-window, b] 里找（只会让终点变早 → 少删）
    ///
    ///   两个方向都只会"少删"，永远不会多吃一个字。
    ///   牺牲一点点 Savings，换来"绝不会误伤相邻内容"这个硬保证 —— 值得。
    ///
    /// 【对视频翻译这块的另外一个好处】
    ///   Whisper 的时间码误差约 ±0.2 秒，向内收缩正好抵消它偏早的那部分。
    ///   即使找不到明显的安静点，也保持原位置（由 minGainDb 控制）。
    /// </summary>
    /// <param name="t">粗略时间点（秒）</param>
    /// <param name="env">包络</param>
    /// <param name="frameSec">每帧多少秒</param>
    /// <param name="inward">
    /// true = 向**后**找（用于区间起点）；false = 向**前**找（用于区间终点）。
    /// 两个方向都保证"只会少删"。
    /// </param>
    /// <param name="windowSec">向允许的方向找多远（默认 0.08 秒）</param>
    /// <param name="quietDb">
    /// 落脚点本身要低于多少 dB 才算"真的安静"（默认 -45）。
    ///
    /// 【这个值是被实测逼出来的，不是拍的】
    ///   第一版只要求"最静点比窗口边缘低 3dB"，结果翻车：
    ///   一个字的发音内部本来就有 9dB 左右的强弱起伏（音节感），
    ///   "低 3dB"轻松满足，于是切点被吸到了**音节之间的谷底** ——
    ///   一处 0.15 秒的"嗯"被压缩到 0.05 秒，接着触发"太短不值得剪"被整个丢弃。
    ///
    ///   真正的气口/词间隙在 -45 ~ -60dB，音节谷底只有 -33dB 左右。
    ///   所以这里两条都卡：
    ///     ① 最静点必须够静（低于 quietDb）  —— 排除音节起伏
    ///     ② 井要够深（minWellDepthDb）      —— 排除单调的平坦段
    /// </param>
    /// <param name="minWellDepthDb">窗口内最静点与最响点至少相差多少 dB（默认 10）。</param>
    /// <returns>真正该下刀的时刻</returns>
    public static double SnapInward(double t, double[] env, double frameSec, bool inward,
                                    double windowSec = 0.08,
                                    double quietDb = -45.0,
                                    double minWellDepthDb = 10.0)
    {
        if (env.Length == 0 || frameSec <= 0) return t;

        int center = (int)Math.Round(t / frameSec);
        int span = Math.Max(1, (int)Math.Round(windowSec / frameSec));

        int lo = inward ? center : Math.Clamp(center - span, 0, env.Length - 1);
        int hi = inward ? Math.Clamp(center + span, 0, env.Length - 1) : center;
        if (lo >= hi) return t;

        // 在窗口里同时找最静点和最响点
        int bestIdx = lo;
        double minVal = double.MaxValue;
        double maxVal = double.MinValue;
        for (int i = lo; i <= hi; i++)
        {
            if (env[i] < minVal) { minVal = env[i]; bestIdx = i; }
            if (env[i] > maxVal) maxVal = env[i];
        }

        // ① 最静的点都还不够安静 → 这个窗口里没有正经的落脚处，不吸附
        if (minVal > quietDb) return t;

        // ② 起伏太平缓 → 说明整段状态一致，谈不上"找到了更好的位置"，不吸附
        if (maxVal - minVal < minWellDepthDb) return t;

        return bestIdx * frameSec;
    }
}
