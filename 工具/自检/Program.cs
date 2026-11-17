using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;
using LiteCut.Core.Timeline;

namespace LiteCut.SelfCheck;

/// <summary>
/// 工作台自检：一组确定性的纯逻辑断言。
///
/// 设计原则（为什么长得这么朴素）：
///   · 不引入任何第三方测试框架（本项目红线），自己收集通过/失败；
///   · 全部用内存数据 —— 只有「契约落盘」那两条会写文件，而且只写 %TEMP%，
///     绝不碰 D:\工作台 下的真实项目；
///   · 每条断言都给出「期望什么、实际什么」，失败时不用再翻代码猜。
///
/// 退出码：0 = 全过；1 = 有失败。健康检查脚本就靠这个判过没过。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failed = new();

    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 个别宿主不允许改，忽略 */ }

        Console.WriteLine("工作台自检 · LiteCut.Core 纯逻辑");

        Section("文本相似度（转写去重的四把尺子）", TextSimilarityChecks);
        Section("时间轴碰撞（合并 / 空档）", TimelineChecks);
        Section("静音检测与有声段", SilenceChecks);
        Section("音频包络（增益 × 淡入 × 淡出）", EnvelopeChecks);
        Section("转写稿导入（标准格式 / 空 / 坏）", TranscriptImportChecks);
        Section("契约读写（序列化 + 落盘无 BOM）", ContractChecks);

        Console.WriteLine();
        Console.WriteLine($"自检结果：通过 {_passed} 项，失败 {Failed.Count} 项");
        foreach (string f in Failed) Console.WriteLine($"  失败：{f}");

        return Failed.Count == 0 ? 0 : 1;
    }

    // ============================================================
    // 断言工具
    // ============================================================

    private static void Section(string title, Action body)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
        body();
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [过] {name}");
            return;
        }

        string line = detail.Length == 0 ? name : $"{name}（实际：{detail}）";
        Failed.Add(line);
        Console.WriteLine($"  [败] {line}");
    }

    private static bool Near(double actual, double expected, double eps = 1e-6)
        => Math.Abs(actual - expected) <= eps;

    private static string Fmt(double v) => v.ToString("0.######");

    // ============================================================
    // 一、文本相似度
    // ============================================================

    private static void TextSimilarityChecks()
    {
        string sample = "其实呢， 我们就是说 —— 唐代的美学。";
        string normalized = TextSimilarity.Normalize(sample);
        Check("归一化：中文标点、空格、破折号全洗掉",
            normalized == "其实呢我们就是说唐代的美学", normalized);

        Check("归一化：null 变空串", TextSimilarity.Normalize(null) == "");

        Check("归一化：半角大写转小写", TextSimilarity.Normalize("ABC") == "abc", TextSimilarity.Normalize("ABC"));

        // 已知小瑕疵（本单只登记、不顺手修，见接管说明书「已知问题」）：
        //   归一化里「全角转半角」是 else-if 的第一个分支，转完不会再走「大写转小写」，
        //   所以全角大写 ＡＢＣ 会停在 ABC 而不是 abc。只有全角输入才会踩到，
        //   日常中文稿子不受影响，故登记不动。
        string fullWidth = TextSimilarity.Normalize("ＡＢＣ　１２３");
        Check("归一化：全角字母/数字转半角（现状保留原大小写）", fullWidth == "ABC123", fullWidth);

        string a = TextSimilarity.Normalize("我们要讲的是唐代的美学");
        string b = TextSimilarity.Normalize("我们要讲的是唐代的美学");
        Check("Dice：同一句 = 1.0", Near(TextSimilarity.Dice(a, b), 1.0), Fmt(TextSimilarity.Dice(a, b)));

        string c = TextSimilarity.Normalize("量子力学导论");
        Check("Dice：毫无共同字对 = 0", Near(TextSimilarity.Dice(a, c), 0.0), Fmt(TextSimilarity.Dice(a, c)));

        string half = TextSimilarity.Normalize("我们要讲的是唐代");
        double overlap = TextSimilarity.Overlap(half, a);
        Check("Overlap：半截句几乎被全句覆盖（>0.95）", overlap > 0.95, Fmt(overlap));

        Check("LcsRatio：完全相同 = 1.0", Near(TextSimilarity.LcsRatio(a, b), 1.0), Fmt(TextSimilarity.LcsRatio(a, b)));
        Check("PrefixRatio：前缀一致 = 1.0", Near(TextSimilarity.PrefixRatio(a, a + "的"), 1.0), Fmt(TextSimilarity.PrefixRatio(a, a + "的")));
        Check("PrefixRatio：空串 = 0", Near(TextSimilarity.PrefixRatio("", a), 0.0));
    }

    // ============================================================
    // 二、时间轴碰撞
    // ============================================================

    private static void TimelineChecks()
    {
        var empty = TimelineCollision.Merge(Array.Empty<TimeRange>());
        Check("Merge：空输入返回空列表", empty.Count == 0, empty.Count.ToString());

        var overlap = TimelineCollision.Merge(new[]
        {
            new TimeRange(0, 5),
            new TimeRange(3, 10),
        });
        Check("Merge：重叠两段合成一段 [0,10]",
            overlap.Count == 1 && Near(overlap[0].Start, 0) && Near(overlap[0].End, 10),
            string.Join(" ", overlap));

        var adjacent = TimelineCollision.Merge(new[]
        {
            new TimeRange(0, 5),
            new TimeRange(5, 10),
        });
        Check("Merge：首尾相接也算一段（容差内）",
            adjacent.Count == 1 && Near(adjacent[0].End, 10),
            string.Join(" ", adjacent));

        var unsorted = TimelineCollision.Merge(new[]
        {
            new TimeRange(8, 10),
            new TimeRange(0, 2),
        });
        Check("Merge：乱序输入按起点排序后仍两段",
            unsorted.Count == 2 && Near(unsorted[0].Start, 0) && Near(unsorted[1].Start, 8),
            string.Join(" ", unsorted));

        var withNaN = TimelineCollision.Merge(new[]
        {
            new TimeRange(double.NaN, 5),
            new TimeRange(1, 2),
        });
        Check("Merge：NaN 段被丢掉，不污染结果",
            withNaN.Count == 1 && Near(withNaN[0].Start, 1),
            string.Join(" ", withNaN));

        var corridors = TimelineCollision.Corridors(new[] { new TimeRange(2, 4) });
        Check("Corridors：单障碍挖出 [0,2] 与片尾无限长两段",
            corridors.Count == 2
            && Near(corridors[0].Start, 0) && Near(corridors[0].End, 2)
            && double.IsPositiveInfinity(corridors[1].End),
            string.Join(" ", corridors));

        var zeroGap = TimelineCollision.Corridors(new[] { new TimeRange(0, 2) });
        Check("Corridors：贴边时保留零长空档（表示塞不下）",
            zeroGap.Count == 2 && Near(zeroGap[0].Length, 0),
            string.Join(" ", zeroGap));
    }

    // ============================================================
    // 三、静音检测
    // ============================================================

    private static void SilenceChecks()
    {
        var allQuiet = new double[10];
        for (int i = 0; i < allQuiet.Length; i++) allQuiet[i] = -60;

        var spans = SilenceDetector.Detect(allQuiet, frameSec: 0.1, noiseDb: -40, minDurSec: 0.5);
        Check("Detect：整段静音 = 1 段，起止 0~1.0s",
            spans.Count == 1 && Near(spans[0].StartSec, 0) && Near(spans[0].EndSec, 1.0),
            string.Join(" ", spans));

        // 只有 2 帧（0.2s）低于阈值，短于 minDurSec(0.5) → 不算静音
        var brief = new double[] { 0, -60, -60, 0, 0 };
        var briefSpans = SilenceDetector.Detect(brief, frameSec: 0.1, noiseDb: -40, minDurSec: 0.5);
        Check("Detect：短于下限的停顿被忽略", briefSpans.Count == 0, briefSpans.Count.ToString());

        var voiced = SilenceDetector.VoicedRuns(
            new List<SilenceSpan> { new SilenceSpan { StartSec = 2, EndSec = 4 } }, totalSec: 10);
        Check("VoicedRuns：一段静音切出 [0,2] 与 [4,10] 两段有声",
            voiced.Count == 2
            && Near(voiced[0].EndSec, 2) && Near(voiced[1].StartSec, 4) && Near(voiced[1].EndSec, 10),
            string.Join(" ", voiced));
    }

    // ============================================================
    // 四、音频包络
    // ============================================================

    private static void EnvelopeChecks()
    {
        // 1000 个样本，淡入 100、淡出 100，增益 0.8
        Check("EnvelopeAt：淡入第一个样本 = 0", Near(AudioPatchLayout.EnvelopeAt(0, 1000, 100, 100, 0.8), 0.0),
            Fmt(AudioPatchLayout.EnvelopeAt(0, 1000, 100, 100, 0.8)));

        Check("EnvelopeAt：中段就是增益本身",
            Near(AudioPatchLayout.EnvelopeAt(500, 1000, 100, 100, 0.8), 0.8),
            Fmt(AudioPatchLayout.EnvelopeAt(500, 1000, 100, 100, 0.8)));

        Check("EnvelopeAt：淡出最后一个样本 ≈ 0.008",
            Near(AudioPatchLayout.EnvelopeAt(999, 1000, 100, 100, 0.8), 0.8 * 1 / 100, 1e-9),
            Fmt(AudioPatchLayout.EnvelopeAt(999, 1000, 100, 100, 0.8)));

        Check("EnvelopeAt：无淡入淡出时处处等于增益",
            Near(AudioPatchLayout.EnvelopeAt(0, 100, 0, 0, 0.5), 0.5));
    }

    // ============================================================
    // 五、转写稿导入
    // ============================================================

    private const string StandardTranscriptJson = """
    {
      "schemaVersion": "1.0",
      "language": "zh",
      "engine": "selftest",
      "audioPath": "selftest.wav",
      "audioDurationSec": 10.0,
      "fullText": "第一句。第二句。",
      "segments": [
        { "index": 0, "startSec": 0.0, "endSec": 5.0, "text": "第一句。", "words": [] },
        { "index": 1, "startSec": 5.0, "endSec": 10.0, "text": "第二句。", "words": [] }
      ]
    }
    """;

    private static void TranscriptImportChecks()
    {
        var ok = TranscriptImporter.Load(StandardTranscriptJson);
        Check("Load：标准契约 = 成功且 2 句",
            ok.Ok && ok.Transcript != null && ok.Transcript.Segments.Count == 2,
            ok.Ok ? $"segments={ok.Transcript!.Segments.Count}" : string.Join("；", ok.Notes));

        var blank = TranscriptImporter.Load("   ");
        Check("Load：空白输入 = 失败且给出说明", !blank.Ok && blank.Notes.Count > 0);

        var broken = TranscriptImporter.Load("{ 这不是 JSON");
        Check("Load：坏 JSON = 失败且不抛异常", !broken.Ok && broken.Notes.Count > 0);
    }

    // ============================================================
    // 六、契约读写
    // ============================================================

    private static void ContractChecks()
    {
        var original = TranscriptImporter.Load(StandardTranscriptJson).Transcript;
        if (original == null)
        {
            Check("Serialize：前置条件（标准稿能读出来）", false, "上面那条已失败");
            return;
        }

        string json = ContractJson.Serialize(original);
        var back = ContractJson.Deserialize<Transcript>(json);
        Check("Serialize/Deserialize：段落数与全文一致",
            back != null && back.Segments.Count == original.Segments.Count && back.FullText == original.FullText,
            back == null ? "反序列化为 null" : $"segments={back.Segments.Count}");

        string tempDir = Path.Combine(Path.GetTempPath(), "workbench-selfcheck-" + Guid.NewGuid().ToString("N"));
        string tempFile = Path.Combine(tempDir, "transcript.json");
        try
        {
            Directory.CreateDirectory(tempDir);
            ContractJson.WriteFile(tempFile, original);

            byte[] head = new byte[3];
            using (var fs = File.OpenRead(tempFile))
            {
                int read = fs.Read(head, 0, 3);
                if (read < 3) head = new byte[3];
            }
            bool hasBom = head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            Check("WriteFile：落盘不带 BOM（外部解析器才读得动）", !hasBom);

            var reread = ContractJson.ReadFile<Transcript>(tempFile);
            Check("ReadFile：读回来还是 2 句",
                reread != null && reread.Segments.Count == 2,
                reread == null ? "读出 null" : $"segments={reread.Segments.Count}");
        }
        catch (Exception ex)
        {
            Check("WriteFile/ReadFile：临时目录往返", false, ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }
}
