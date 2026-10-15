// ============================================================
// AutoCutEngine.cs - 自动粗剪引擎（把两条轨合起来，产出 CutPlan）
// ============================================================
//
// 【它就是流水线第 3 环：粗剪决策】
//
//   输入：解码后的音频（PcmData） + 可选的转写稿（Transcript）
//   输出：CutPlan（契约里那份 JSON）
//
// 【两条轨，不是一个】
//
//   ┌─────────────── 波形轨（永远跑）─────────────────┐
//   │  第 1 层  静音/气口   看音量，纯数学，零误伤风险      │
//   │  第 2 层  波形级重复   比包络形状，不需要转写         │
//   └───────────────────────────────────────────────┘
//   ┌─────────────── 文本轨（有转写稿才跑）─────────────┐
//   │  第 3 层  填充词       "嗯""啊""这个"              │
//   │  第 4 层  半截重说     说了半句卡住又重说           │
//   │  第 5 层  词级口吃     几个字紧跟着又说了一遍        │
//   │  第 6 层  整句重复     隔几句又把同一句说了一遍      │
//   └───────────────────────────────────────────────┘
//
//   **没有转写稿，波形轨照样跑完整个流程。**
//   这句话不是"技术上能跑"，而是产品上的硬要求：
//   whisper 可能没装、可能被卸载、可能明天换个新引擎、可能将来在 Mac 上没有 ——
//   任何一种情况都不能让流程停摆。
//
// 【两轨怎么配合 —— 不是简单相加】
//
//   真正的配合有三处，比"各剪各的"强很多：
//
//   ① 校验：转写稿进来先过一遍 TranscriptAligner。
//      时间码对不上音频的稿子会被拒绝（自动降级），而不是照着错的数据下刀。
//
//   ② 定点：文本层说"大概在第 12.3 秒"，误差可能有 ±0.2 秒。
//      于是拿波形在这附近找真正安静的那一瞬间作为下刀点。
//      —— 文字负责"删什么"，波形负责"在哪下刀"。
//
//   ③ 背书：每一刀落地之前，都检查这个区间是不是真有人声。
//      如果文字说"这里说了嗯"，波形说"这里是静音" → 不动手。
//
// 【为什么输出的是"决策"而不是"新音频"】
//
//   因为它要卡在**人工审核**前面。
//   工作台拿到 CutPlan 之后：
//     ① 在波形上把要剪的地方标出来（还带文字，用户看得懂）
//     ② 你勾掉不同意的
//     ③ 确认后才生成时间轴、才去设计镜头
//
//   这样 AI 剪错的代价是"你多点一下"，而不是"整段素材废了"。
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>自动粗剪引擎。</summary>
public static class AutoCutEngine
{
    /// <summary>
    /// 包络帧长（秒）。5 毫秒一帧 —— 与 ffmpeg silencedetect 对齐，
    /// 这样才能复刻你已经听过并认可的那一版激进粗剪。
    /// </summary>
    public const double FrameSec = 0.005;

    /// <summary>
    /// 跑一遍粗剪，产出决策。
    /// </summary>
    /// <param name="pcm">解码后的音频</param>
    /// <param name="opt">参数（用 AutoCutOptions.Aggressive() 等预设）</param>
    /// <param name="transcript">
    /// 转写稿，**可空**。
    /// 给 null 就走纯波形（等价于"没转写、也没有 whisper"的情况）；
    /// 给了就先校验，通过才进入双轨模式。
    /// </param>
    /// <param name="projectId">项目 ID</param>
    /// <param name="sourcePath">源音频路径（写进决策里，防止日后配错文件）</param>
    /// <param name="scriptMarkdown">
    /// ★ 稿子正文（script.md 的内容），**可空**（T-28「对稿」）。
    ///
    /// 给了它 → 多一层"对稿"：拿你写的稿子当标准答案对一遍录音，
    ///          重录判得更准，同时产出一份"稿子没录的 / 临场发挥的"偏差清单。
    /// 不给   → 完全等价于老行为，一个字都不变。
    ///
    /// 【空串和 null 一律等同"没给"】—— 界面读不到 script.md 时就是空串。
    /// </param>
    public static CutPlan Analyze(
        PcmData pcm,
        AutoCutOptions opt,
        Transcript? transcript = null,
        string projectId = "",
        string sourcePath = "",
        string? scriptMarkdown = null)
    {
        // ---- 第 0 步：算包络 ----
        var env = Envelope.Build(pcm, FrameSec);

        // ============================================================
        // 阶段 A：转写稿校验（决定这一轮走单轨还是双轨）
        // ============================================================
        var tcheck = new TranscriptCheck();
        var mode = CutMode.WaveformOnly;
        Transcript? effective = null;   // 校正之后真正拿来用的稿子

        bool hasTranscript = transcript != null
                             && transcript.Segments != null
                             && transcript.Segments.Count > 0;

        tcheck.Available = hasTranscript;

        if (hasTranscript)
        {
            tcheck.Source = string.IsNullOrEmpty(transcript!.Engine) ? "external" : transcript.Engine;

            var report = TranscriptAligner.Validate(
                transcript, env, FrameSec, opt.SilenceNoiseDb, pcm.DurationSec);

            tcheck.Score = report.Score;
            tcheck.VoicedRatio = Math.Round(report.VoicedRatio, 3);
            tcheck.GlobalShiftSec = report.GlobalShiftSec;
            tcheck.Warnings.AddRange(report.Warnings);

            if (report.Usable && opt.Text.Enabled)
            {
                effective = report.ShouldApplyShift
                    ? TranscriptAligner.ApplyShift(transcript, report.GlobalShiftSec)
                    : transcript;
                tcheck.Used = true;
                mode = CutMode.WaveformAndText;
            }
            else if (!report.Usable)
            {
                // 自动降级：不影响结果，只是少了一层能力
                tcheck.Warnings.Add("已自动退回纯波形粗剪（语义层未启用）");
            }
        }
        else
        {
            tcheck.Warnings.Add("没有转写稿 → 本次仅使用波形层");
        }

        // ============================================================
        // 阶段 B：波形轨
        // ============================================================
        var drops = new List<Drop>();

        var silences = SilenceDetector.Detect(env, FrameSec, opt.SilenceNoiseDb, opt.SilenceMinDurSec);
        var runs = SilenceDetector.VoicedRuns(silences, pcm.DurationSec);

        foreach (var s in silences)
        {
            double cutStart = s.StartSec + opt.BreathKeepSec + opt.LeadPadSec;
            double cutEnd = s.EndSec - opt.TailPadSec;
            if (cutEnd - cutStart <= 0.01) continue;   // 留的比整段还长，等于不剪

            // 置信度看这段有多安静：越安静越确定是静音
            double conf = s.MeanDb <= -45 ? 0.95 : (s.MeanDb <= -40 ? 0.85 : 0.70);
            drops.Add(new Drop(cutStart, cutEnd, CutReason.Silence, conf, $"静音 {s.DurationSec:F2}s / {s.MeanDb:F1}dB"));
        }

        var repeats = opt.DetectRepeats
            ? RepeatDetector.Detect(runs, env, FrameSec, opt)
            : new List<RepeatPair>();

        foreach (var p in repeats)
        {
            if (p.CutTarget == null) continue;                       // 拿不准，只提示
            if (p.Confidence < opt.RepeatAutoCutConfidence) continue; // 把握不够，不动手
            var t = p.CutTarget.Value;
            drops.Add(new Drop(t.StartSec, t.EndSec, CutReason.Repeat, p.Confidence,
                               $"与 {p.Second.StartSec:F2}s 处重复（波形相似度 {p.Similarity:F2}）"));
        }

        // ============================================================
        // 阶段 C：文本轨（只有双轨模式才跑）
        // ============================================================
        var suggestions = new List<CutSuggestion>();

        // ---- 对稿概况（T-28）----
        // 有稿子就填 Available = true；真正"用没用上"要看对齐质量，由 ScriptAligner 判。
        // 没有稿子时也保留这个对象，界面据此说明"对稿未启用"。
        var scriptCheck = new ScriptCheck { Available = !string.IsNullOrWhiteSpace(scriptMarkdown) };
        if (!scriptCheck.Available)
            scriptCheck.Warnings.Add("没有稿子（script.md）—— 对稿未启用");

        if (mode == CutMode.WaveformAndText && effective != null)
        {
            // ---- 先对稿：拿 script.md 当标准答案 ----
            ScriptAlignResult? align = null;
            if (scriptCheck.Available)
            {
                align = ScriptAligner.Align(effective, scriptMarkdown, opt.Text);
                scriptCheck = align.Check;
            }

            // 对稿可信 → 重录交给对稿判（"稿子里这句只写了一次"这条判据，比纯文本稳），
            // 纯文本重录检测就不再跑，免得两条路重复下刀。
            bool useScriptRetakes = align != null && align.Usable;
            var issues = SpeechIssueDetector.Detect(effective, opt.Text, includeRetakes: !useScriptRetakes);
            if (useScriptRetakes) issues.AddRange(align!.RetakeIssues);

            var textDrops = new List<Drop>();

            foreach (var it in issues)
            {
                double a = Math.Clamp(it.StartSec, 0, pcm.DurationSec);
                double b = Math.Clamp(it.EndSec, 0, pcm.DurationSec);
                if (b - a < opt.Text.MinIssueSec) continue;

                // ---- 波形背书：他说这里有人声，波形说这里没声音 → 不动手 ----
                //
                // 这是防止"时间码错 a 词条被误删"的最后一道检查。
                // 语义层可以慢一点、可以漏，但不能删错地方。
                double voiced = VoicedRatioIn(env, FrameSec, a, b, opt.SilenceNoiseDb);
                if (voiced < 0.15)
                {
                    // 该位置实际是静音 —— 说明这份稿子在这一点上不可信。
                    // 不删，也不进建议列表（免得拿错误信息骚扰用户）。
                    continue;
                }

                // ---- 定点：文字说大概在哪，波形来精修到最近的安静点 ----
                //
                // 注意方向都朝里 —— 起点往后收、终点往前收，
                // 保证"纠偏"绝不会变成"多删"。具体原因见 SnapInward 的注释。
                if (opt.SnapTextCuts)
                {
                    a = TranscriptAligner.SnapInward(a, env, FrameSec, inward: true);
                    b = TranscriptAligner.SnapInward(b, env, FrameSec, inward: false);
                    if (b - a < opt.Text.MinIssueSec) continue;
                }

                if (it.AutoCut)
                {
                    textDrops.Add(new Drop(a, b, it.Reason, it.Confidence, it.Note));
                }
                else
                {
                    // 把握不足 → 放进建议清单，等用户在界面上点头
                    suggestions.Add(new CutSuggestion
                    {
                        SourceStartSec = Math.Round(a, 3),
                        SourceEndSec = Math.Round(b, 3),
                        Reason = it.Reason,
                        Confidence = Math.Round(it.Confidence, 2),
                        Evidence = it.Evidence,
                        Note = it.Note,
                    });
                }
            }

            // ---- 护栏：语义层最多剪掉全片的一定比例 ----
            double textBudget = pcm.DurationSec * opt.Text.MaxTextDropRatio;
            double textTotal = TotalSec(textDrops);
            if (textTotal > textBudget)
            {
                // 超了就按把握从低到高丢弃，保留最确定的那些
                textDrops.Sort((x, y) => y.Confidence.CompareTo(x.Confidence));  // 高把握在前
                var kept = new List<Drop>();
                double sum = 0;
                foreach (var d in textDrops)
                {
                    double len = d.End - d.Start;
                    if (sum + len > textBudget) continue;   // 放不下了，跳过
                    kept.Add(d);
                    sum += len;
                }
                tcheck.Warnings.Add(
                    $"语义层判定超出安全额度（{textTotal:F1}s > {textBudget:F1}s），已只保留把握最高的部分");
                textDrops = kept;
            }

            drops.AddRange(textDrops);
        }

        // ============================================================
        // 阶段 D：合并 + 总护栏
        // ============================================================
        drops.Sort((x, y) => x.Start.CompareTo(y.Start));
        var merged = MergeOverlaps(drops);

        // 两层叠加之后可能剪过头 → 最后一道总闸
        double maxTotal = pcm.DurationSec * opt.MaxTotalDropRatio;
        double dropTotal = TotalSec(merged);
        bool overBudget = false;
        if (dropTotal > maxTotal)
        {
            overBudget = true;
            // 丢掉把握最低的那些，直到总量合规
            var order = new List<Drop>(merged);
            order.Sort((x, y) => x.Confidence.CompareTo(y.Confidence));   // 低把握在前
            var removed = new HashSet<int>();
            double excess = dropTotal - maxTotal;
            for (int i = 0; i < order.Count && excess > 0; i++)
            {
                removed.Add(i);
                excess -= order[i].End - order[i].Start;
            }
            var keptList = new List<Drop>();
            for (int i = 0; i < order.Count; i++)
                if (!removed.Contains(i)) keptList.Add(order[i]);

            keptList.Sort((x, y) => x.Start.CompareTo(y.Start));
            merged = MergeOverlaps(keptList);
        }

        // ============================================================
        // 阶段 E：铺满全片，生成 Segments
        // ============================================================
        var plan = new CutPlan
        {
            SchemaVersion = "1.0",
            ProjectId = projectId,
            Mode = mode,
            Source = new SourceInfo
            {
                Path = sourcePath,
                DurationSec = Math.Round(pcm.DurationSec, 3),
                SampleRate = pcm.SampleRate,
                Channels = 1,
            },
            Params = new CutParams
            {
                SilenceMinSec = opt.SilenceMinDurSec,
                FillerMaxSec = opt.SilenceMinDurSec,
                BreathKeepSec = opt.BreathKeepSec,
                RepeatMinCount = 2,
                RepeatWindowSec = opt.RepeatWindowSec,
                LeadPadSec = opt.LeadPadSec,
                TailPadSec = opt.TailPadSec,
            },
            Transcript = tcheck,
            Script = scriptCheck,
            Suggestions = suggestions,
            GeneratedAt = DateTimeOffset.Now.ToString("o"),
            GeneratedBy = BuildStamp(mode, opt),
        };

        double cursor = 0;
        int idx = 0;
        foreach (var d in merged)
        {
            if (d.Start > cursor + 1e-6)
                plan.Segments.Add(MakeSeg(idx++, cursor, d.Start, CutAction.Keep, CutReason.Normal, 1.0, "", effective));

            plan.Segments.Add(MakeSeg(idx++, Math.Max(cursor, d.Start), d.End, CutAction.Drop, d.Reason, d.Confidence, d.Note, effective));
            cursor = Math.Max(cursor, d.End);
        }
        if (cursor < pcm.DurationSec - 1e-6)
            plan.Segments.Add(MakeSeg(idx++, cursor, pcm.DurationSec, CutAction.Keep, CutReason.Normal, 1.0, "", effective));

        // ---- 统计 ----
        var stats = new CutStats
        {
            OriginalSec = Math.Round(pcm.DurationSec, 2),
            FinalSec = Math.Round(plan.FinalDurationSec, 2),
        };
        stats.RemovedSec = Math.Round(stats.OriginalSec - stats.FinalSec, 2);
        stats.SegmentCount = plan.Segments.Count;
        foreach (var s in plan.Segments)
        {
            if (s.Action == CutAction.Keep) stats.KeepCount++;
            else
            {
                stats.DropCount++;
                string key = s.Reason.ToString().ToLowerInvariant();
                stats.DropByReason.TryGetValue(key, out int c);
                stats.DropByReason[key] = c + 1;
            }
        }
        plan.Stats = stats;

        if (overBudget)
            plan.Transcript.Warnings.Add($"剪掉的总量超出安全额度（上限 {opt.MaxTotalDropRatio:P0}），已自动收敛");

        return plan;
    }

    /// <summary>
    /// 向后兼容的重载：不传转写稿 = 纯波形模式。
    /// 现有的调用方一行都不用改。
    /// </summary>
    public static CutPlan Analyze(PcmData pcm, AutoCutOptions opt, string projectId, string sourcePath)
        => Analyze(pcm, opt, null, projectId, sourcePath);

    /// <summary>单独跑重复检测（界面上"查重复"按钮用，只提示不改决策）。</summary>
    public static List<RepeatPair> FindRepeats(PcmData pcm, AutoCutOptions opt)
    {
        var env = Envelope.Build(pcm, FrameSec);
        var silences = SilenceDetector.Detect(env, FrameSec, opt.SilenceNoiseDb, opt.SilenceMinDurSec);
        var runs = SilenceDetector.VoicedRuns(silences, pcm.DurationSec);
        return RepeatDetector.Detect(runs, env, FrameSec, opt);
    }

    // ============================================================
    // 内部
    // ============================================================

    /// <summary>一段待剪区间。</summary>
    private readonly record struct Drop(
        double Start,
        double End,
        CutReason Reason,
        double Confidence,
        string Note);

    private static string BuildStamp(CutMode mode, AutoCutOptions opt)
    {
        string base_ = $"LiteCut.AutoCut/2.0 (noise={opt.SilenceNoiseDb}dB, keep={opt.BreathKeepSec}s)";
        return mode == CutMode.WaveformAndText ? base_ + " +text" : base_;
    }

    /// <summary>
    /// 合并重叠区间。
    ///
    /// 【⚠️ 合并时"保留哪个原因"是有讲究的 —— 这里踩过坑】
    ///
    ///   第一版写成"保留先到的"（last.Reason），结果出过一次荒谬的事：
    ///   语义层明明正确识别出一段"半截重说"，但它前面紧挨着一小段静音，
    ///   两者一合并，这段 reason 就只剩下"silence"了。
    ///   界面上看起来是"删了段静音"，实际底下埋着一次重要的语义判断 ——
    ///   用户完全没机会知道"这里其实是 AI 觉得你说错了重来"。
    ///
    ///   根因：不同 reason 的**信息量**天差地别。
    ///   "silence"只陈述"这里没声音"这种人尽皆知的事实；
    ///   "falseStart"说的是"我认为你这句没说完然后重说了"。
    ///   显然是后者重要。所以合并时取信息量大的那个，不是先到先得。
    /// </summary>
    private static List<Drop> MergeOverlaps(List<Drop> drops)
    {
        var merged = new List<Drop>();
        foreach (var d in drops)
        {
            if (merged.Count > 0 && d.Start <= merged[^1].End + 1e-6)
            {
                var last = merged[^1];

                // 保留信息量更大的那个原因
                CutReason keepReason = ReasonPriority(last.Reason) >= ReasonPriority(d.Reason)
                    ? last.Reason : d.Reason;

                string note = string.IsNullOrEmpty(last.Note) ? d.Note
                            : (string.IsNullOrEmpty(d.Note) ? last.Note : $"{last.Note}；{d.Note}");

                // 置信度取两者**较低**的那个 —— 合并意味着"这一段混合了两种判断"，
                // 用最高的会掩盖掉不确定性，保守起见按最弱环节算。
                merged[^1] = new Drop(last.Start, Math.Max(last.End, d.End), keepReason,
                                      Math.Min(last.Confidence, d.Confidence), note);
            }
            else merged.Add(d);
        }
        return merged;
    }

    /// <summary>原因的信息量优先级。数字越大越重要。</summary>
    private static int ReasonPriority(CutReason r) => r switch
    {
        CutReason.FalseStart => 6,   // "你这句没说完又重说了" —— 最需要用户知道
        CutReason.Retake => 6,       // "这句录了好几遍" —— 同样是用户最该看到的
        CutReason.Stutter => 5,
        CutReason.Repeat => 4,       // "这句说过了"
        CutReason.Filler => 3,       // "这是口水词"
        CutReason.OffTopic => 3,
        CutReason.Manual => 3,
        CutReason.Silence => 1,      // "这里没声音" —— 人人看得出来，最不重要
        _ => 0,
    };

    private static double TotalSec(List<Drop> drops)
    {
        double sum = 0;
        foreach (var d in drops) sum += Math.Max(0, d.End - d.Start);
        return sum;
    }

    /// <summary>某区间内有多少比例的帧是有声音的。</summary>
    private static double VoicedRatioIn(double[] env, double frameSec, double fromSec, double toSec, double noiseDb)
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

    private static CutSegment MakeSeg(int idx, double a, double b, CutAction action, CutReason reason,
                                      double conf, string note, Transcript? transcript)
    {
        return new CutSegment
        {
            Id = $"s{idx:0000}",
            SourceStartSec = Math.Round(a, 3),
            SourceEndSec = Math.Round(b, 3),
            Action = action,
            Reason = reason,
            Confidence = Math.Round(conf, 2),
            Note = string.IsNullOrEmpty(note) ? null : note,
            Text = TextInRange(transcript, a, b),
        };
    }

    /// <summary>
    /// 取某个时间区间里说过的话。
    ///
    /// 【为什么被剪掉的段也要显示文字】
    ///   用户审核时想知道"这一刀会不会剪掉我要的内容"。
    ///   光看波形不知道内容，光看时间不知道位置 —— 带上文字才能秒判。
    ///   这是文本层除了"参与决策"之外的第二个用处：**让审核变得看得懂**。
    ///
    /// 【为什么要精确到词，而不是整句】
    ///
    ///   第一版是"只要句子跟这个区间有重叠，就把整句贴上去"。
    ///   实测闹过笑话：明明只剪掉了句首的"这个呃"两个字，
    ///   界面上却显示整句「这个呃转折很关键」——
    ///   用户第一反应是"AI 把这句全删了！"，白紧张一场。
    ///
    ///   所以这里在有词级时间码时逐词判断，只拼接真正被切到的那几个词。
    ///   没有词级时间码时才退回到整句（那时只能做到这个精度）。
    /// </summary>
    private static string TextInRange(Transcript? t, double a, double b)
    {
        if (t == null || t.Segments == null || t.Segments.Count == 0) return "";

        var sb = new StringBuilder();
        const double MinOverlap = 0.05;

        foreach (var seg in t.Segments)
        {
            // 跟这个区间毫无关系的句子直接跳过
            double segOverlap = Math.Min(b, seg.EndSec) - Math.Max(a, seg.StartSec);
            if (segOverlap <= MinOverlap) continue;

            if (seg.Words != null && seg.Words.Count > 0)
            {
                // 有词级时间码 —— 精确到词
                bool usedAny = false;
                foreach (var w in seg.Words)
                {
                    double ov = Math.Min(b, w.EndSec) - Math.Max(a, w.StartSec);
                    if (ov <= MinOverlap) continue;
                    sb.Append(w.Text);
                    usedAny = true;
                }

                // 一个词都没捞到（时间在句号边界抖动），退化到整句，
                // 总比什么都不显示强
                if (!usedAny) sb.Append(seg.Text.Trim());
            }
            else
            {
                // 只有句级时间码 —— 重叠过半才算"这句属于这个区间"
                double segLen = seg.EndSec - seg.StartSec;
                if (segLen > 0 && segOverlap / segLen >= 0.5)
                    sb.Append(seg.Text.Trim());
            }

            if (sb.Length > 240) break;   // 别把 JSON 撑爆，界面上也不显示这么长
        }
        return sb.ToString();
    }
}
