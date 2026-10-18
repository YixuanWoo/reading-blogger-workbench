// ============================================================
// VoiceMaster.cs - 录音台 → 下游的交接器（V1.17.0）
// ============================================================
//
// 【一句话】
//   把「素材\录音\takes\t001.wav …」按稿子顺序拼成一条母带 vo.wav，
//   同时算出**转写稿**和**剪辑铺排**，一起交给精剪。
//
// 【为什么要拼成一条母带，而不是让精剪直接读一堆 wav】
//   精剪页（EditorView）的世界观是"一个源文件 + 若干块"。它的波形、播放、
//   裁剪、淡入淡出全是按"源文件第几秒"写的。给它一堆文件等于重写半个编辑器。
//   拼成一条之后：**一块 = 你录的一条音**，跟录音台一一对应，
//   拖块边界就是调气口 —— 正是用户要的那个手感。
//
// 【为什么不在这里剪气口】
//   气口留不留是"判断"，不是"算法"：同一个人同一段稿子，快节奏和慢节奏
//   需要的呼吸长度不一样。所以这里**一个采样都不动**（不裁、不淡入淡出、
//   不重采样增益），留白原样留着，让用户在精剪里自己拖。
//
// 【★ 转写稿 = 稿子 + 算术，不是 ASR】
//   用户是照着自己写的 script.md 念的，字是确定的（比任何 ASR 都准）。
//   这里只需要回答一件事：**每一句在母带里占第几秒到第几秒**。
//     一条录音正好一句      → 精确到毫秒（录音的起止就是句子的起止）
//     一条录音覆盖好几句    → 按字数占比分摊（估算，够分镜用）
//   所以 Engine 字段写 "script"，不要冒充实测转写 —— 以后要核对
//   "我实际念的和稿子一不一样"，那是「对稿」（T-28）的事，不是这份文件。
//
// 【落到哪】
//   <项目>\素材\录音\vo.wav                 ← 母带（每次覆盖重建）
//   <项目>\stages\01-transcript\transcript.json ← 转写稿
//   project.json 的 Cut.Plan                ← 剪辑铺排（一条录音 = 一个 Keep 段）
//
// 【为什么母带不堆版本】
//   vo-001 / vo-002 会让项目夹越滚越大，而且"用哪一版"这件事没人记得住。
//   母带是**派生品**（源是 takes\*.wav，一个字节都没丢），随时能重建，
//   所以固定一个名字、每次覆盖，最不容易出错。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteCut.Core.Contracts;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiteCut.Workbench.RecordingDesk;

/// <summary>一条录音在母带里的位置。</summary>
public sealed class VoicePiece
{
    public RecordingTake Take { get; init; } = new();

    /// <summary>在母带里的起点（秒）。</summary>
    public double StartSec { get; set; }

    /// <summary>在母带里的终点（秒）。</summary>
    public double EndSec { get; set; }

    public double DurSec => EndSec - StartSec;
}

/// <summary>拼母带的结果。</summary>
public sealed class VoiceMasterResult
{
    public string WavPath { get; set; } = "";

    public double DurationSec { get; set; }

    /// <summary>按稿子顺序排好的段（跟 takes 一一对应，只含真的读到了文件的）。</summary>
    public List<VoicePiece> Pieces { get; set; } = new();

    /// <summary>读不出来的 wav（文件缺失 / 损坏）。不为空时界面要如实说。</summary>
    public List<string> Skipped { get; set; } = new();
}

/// <summary>
/// 母带拼接 + 转写稿 + 剪辑铺排。
/// </summary>
public static class VoiceMaster
{
    /// <summary>母带文件名（固定，每次覆盖重建）。</summary>
    public const string FileName = "vo.wav";

    /// <summary>母带格式：跟录音台录制时完全一致，避免任何一次重采样。</summary>
    private const int SampleRate = 44100;
    private const int Channels = 1;

    // ========================================================
    // ① 拼母带
    // ========================================================

    /// <summary>
    /// 按给定顺序把各条录音首尾相接写成一条 wav。
    ///
    /// 【为什么不直接拼字节】
    ///   理论上每条都是 44.1k/16bit/单声道，拷 data 块就行。但只要有一条是
    ///   别处来的（手工替换过、旧版本录的），直接拼字节就会得到一条**变速**的母带，
    ///   而且听起来只是"有点怪"，很难发现。所以统一走 NAudio 的采样流：
    ///   声道/采样率不一样就当场转，最后的母带格式恒定。
    /// </summary>
    public static VoiceMasterResult Build(string recordingDir, IReadOnlyList<RecordingTake> takes)
    {
        var result = new VoiceMasterResult
        {
            WavPath = Path.Combine(recordingDir, FileName),
        };

        Directory.CreateDirectory(recordingDir);

        string tmp = result.WavPath + ".tmp";
        var fmt = new WaveFormat(SampleRate, 16, Channels);

        long totalSamples = 0;

        using (var writer = new WaveFileWriter(tmp, fmt))
        {
            foreach (var t in takes)
            {
                string path = Path.Combine(recordingDir,
                    (t.File ?? "").Replace('/', Path.DirectorySeparatorChar));

                if (string.IsNullOrWhiteSpace(t.File) || !File.Exists(path))
                {
                    result.Skipped.Add(t.Id + "（文件不见了）");
                    continue;
                }

                double start = (double)totalSamples / SampleRate;

                try
                {
                    using var reader = new AudioFileReader(path);

                    ISampleProvider sp = reader;
                    if (sp.WaveFormat.Channels != Channels)
                    {
                        if (sp.WaveFormat.Channels == 2)
                            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
                        else
                            throw new InvalidOperationException($"声道数 {sp.WaveFormat.Channels} 不支持");
                    }
                    if (sp.WaveFormat.SampleRate != SampleRate)
                        sp = new WdlResamplingSampleProvider(sp, SampleRate);

                    var buf = new float[16384];
                    int read;
                    while ((read = sp.Read(buf, 0, buf.Length)) > 0)
                    {
                        writer.WriteSamples(buf, 0, read);
                        totalSamples += read;
                    }
                }
                catch (Exception ex)
                {
                    // 一条坏掉不该让整批作废 —— 记下来，界面如实报
                    result.Skipped.Add(t.Id + "（" + ex.Message + "）");
                }

                double end = (double)totalSamples / SampleRate;
                if (end - start > 0.001)
                    result.Pieces.Add(new VoicePiece { Take = t, StartSec = start, EndSec = end });
            }

            writer.Flush();
        }

        // 原子替换：写到一半断电也不会毁掉上一版母带
        try
        {
            if (File.Exists(result.WavPath))
                File.Replace(tmp, result.WavPath, null);
            else
                File.Move(tmp, result.WavPath);
        }
        catch
        {
            try { File.Copy(tmp, result.WavPath, true); File.Delete(tmp); }
            catch (Exception ex)
            {
                throw new IOException(
                    "母带写不进去（多半是精剪页还在播放这个文件）—— 回精剪停掉播放再试。" +
                    "\n\n原始错误：" + ex.Message, ex);
            }
        }

        result.DurationSec = (double)totalSamples / SampleRate;
        return result;
    }

    // ========================================================
    // ② 转写稿：每一句在母带里第几秒
    // ========================================================

    /// <summary>
    /// 按母带时间码写出转写稿。
    ///
    /// 一条录音＝一句 → 录音的起止就是句子的起止（精确）。
    /// 一条录音＝多句 → 按**字数占比**分摊（估算）；跨段落的句子照常分摊，
    /// 因为录音本来就是一口气念过去的，段落只是稿子的排版。
    /// </summary>
    public static Transcript MakeTranscript(
        ScriptDocument doc, VoiceMasterResult master, string wavPath)
    {
        var t = new Transcript
        {
            SchemaVersion = "1.0",
            Language = "zh",
            Engine = "script",
            Model = "script.md + takes",
            AudioPath = wavPath,
            AudioDurationSec = master.DurationSec,
        };

        int idx = 0;
        foreach (var p in master.Pieces)
        {
            int a = Math.Min(p.Take.A, p.Take.B);
            int b = Math.Max(p.Take.A, p.Take.B);

            // 超出稿子范围（稿子后来改短了）—— 夹住，不要让下游拿到不存在的句子
            a = Math.Clamp(a, 0, Math.Max(0, doc.Sentences.Count - 1));
            b = Math.Clamp(b, 0, Math.Max(0, doc.Sentences.Count - 1));
            if (doc.Sentences.Count == 0) break;

            int totalChars = 0;
            for (int i = a; i <= b; i++) totalChars += Math.Max(1, doc.Sentences[i].Chars);
            if (totalChars <= 0) totalChars = b - a + 1;

            double cursor = p.StartSec;
            double span = p.EndSec - p.StartSec;

            for (int i = a; i <= b; i++)
            {
                double share = Math.Max(1, doc.Sentences[i].Chars) / (double)totalChars;
                double dur = (i == b) ? (p.EndSec - cursor) : span * share;
                if (dur < 0.001) dur = 0.001;

                t.Segments.Add(new TranscriptSegment
                {
                    Index = idx++,
                    StartSec = Math.Round(cursor, 3),
                    EndSec = Math.Round(cursor + dur, 3),
                    Text = doc.Sentences[i].Text,
                    // ★ 不给词级时间码：我们只知道句子的起止。
                    //   编造每字的时间码会让下游"精确到字地剪"，那是假的精确。
                    Words = new List<TranscriptWord>(),
                });

                cursor += dur;
            }
        }

        t.FullText = string.Concat(t.Segments.Select(s => s.Text));
        return t;
    }

    // ========================================================
    // ③ 剪辑铺排：一条录音 = 一个保留段
    // ========================================================

    /// <summary>
    /// 造一份"整条母带都保留"的 CutPlan，但**按录音切成一段一段**。
    ///
    /// 【为什么要切成段而不是一整段】
    ///   精剪页按 Keep 段铺时间轴块：一整段 = 一整条长块，用户没法"一块一句"
    ///   地调气口。切成 N 段，打开精剪就是 N 个块，一块正好一条录音。
    ///
    /// 【为什么全都 Keep】
    ///   录音阶段已经把卡壳当场重录掉了，这里没有任何"该剪的"；
    ///   真觉得某段多余，用户会在精剪里删块 —— 那才是判断发生的地方。
    /// </summary>
    public static CutPlan MakePlan(
        ScriptDocument doc, VoiceMasterResult master, string wavPath, string projectName)
    {
        var plan = new CutPlan
        {
            ProjectId = projectName,
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            GeneratedBy = "录音台（母带拼接）",
            Mode = CutMode.WaveformOnly,
        };

        plan.Source.Path = wavPath;
        plan.Source.DurationSec = Math.Round(master.DurationSec, 3);
        plan.Source.SampleRate = SampleRate;
        plan.Source.Channels = Channels;

        int no = 0;
        foreach (var p in master.Pieces)
        {
            no++;
            int a = Math.Clamp(Math.Min(p.Take.A, p.Take.B), 0, Math.Max(0, doc.Sentences.Count - 1));
            int b = Math.Clamp(Math.Max(p.Take.A, p.Take.B), 0, Math.Max(0, doc.Sentences.Count - 1));
            string text = doc.Sentences.Count > 0 ? doc.TextBetween(a, b) : "";

            plan.Segments.Add(new CutSegment
            {
                Id = "v" + no.ToString("000"),
                SourceStartSec = Math.Round(p.StartSec, 3),
                SourceEndSec = Math.Round(p.EndSec, 3),
                Action = CutAction.Keep,
                Reason = CutReason.Normal,
                Confidence = 1.0,
                Text = Clip(text, 160),
                Note = "录音 " + no + " · " + (b - a + 1) + " 句",
            });
        }

        plan.Stats.OriginalSec = plan.Source.DurationSec;
        plan.Stats.FinalSec = plan.Source.DurationSec;
        plan.Stats.RemovedSec = 0;
        plan.Stats.SegmentCount = plan.Segments.Count;
        plan.Stats.KeepCount = plan.Segments.Count;
        plan.Stats.DropCount = 0;

        // 这份决策没有"AI 判断"要审 —— 用户刚刚一句句录完，就是审核本身
        plan.Review.Status = ReviewStatus.Approved;
        plan.Review.ReviewedAt = plan.GeneratedAt;
        plan.Transcript.Available = true;
        plan.Transcript.Used = false;
        plan.Transcript.Source = "script";

        return plan;
    }

    /// <summary>转写稿落盘路径（stages\01-transcript\transcript.json）。</summary>
    public static string TranscriptPath(string projectDir)
        => Path.Combine(projectDir, "stages", "01-transcript", "transcript.json");

    /// <summary>写转写稿（无 BOM）。返回写到的路径。</summary>
    public static string SaveTranscript(string projectDir, Transcript t)
    {
        string p = TranscriptPath(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        ContractJson.WriteFile(p, t);
        return p;
    }

    private static string Clip(string s, int max)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
