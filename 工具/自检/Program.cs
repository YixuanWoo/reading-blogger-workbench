using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;
using LiteCut.Core.Paths;
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
        Section("原子写（写坏也不毁原文件）", AtomicWriteChecks);
        Section("数据目录隔离（LITECUT_DATA_DIR，纯函数）", AppDataPathsChecks);

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

    // ============================================================
    // 七、原子写（写坏也不毁原文件）
    // ============================================================
    //
    // 要证明的只有一件事：**保存失败时，原文件必须一字未动**。
    // 这条如果不成，用户的数据就是「写一次错一次、越存越少」。
    //
    // 手法：把目标文件用 FileShare.None 独占打开（模拟「网盘同步 / 杀毒 / Obsidian 正占着它」），
    // 再调 WriteFile 写新内容 —— 应当抛异常（File.Replace 拿不到目标文件），
    // 且抛完之后：原文件哈希不变、修改时间不变、同目录没有残留 .tmp。
    // 全部落在 %TEMP% 下的一次性目录里，绝不碰 D:\工作台。

    private static void AtomicWriteChecks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            "workbench-atomic-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(tempDir, "account-review.json");
        string tmp = target + ".tmp";

        try
        {
            Directory.CreateDirectory(tempDir);

            // 先落一份"用户已有的账本"，这就是后面要比对的"原文件"
            var before = TranscriptImporter.Load(StandardTranscriptJson).Transcript;
            ContractJson.WriteFile(target, before);

            byte[] hashBefore = Sha256Of(target);
            DateTime mtimeBefore = File.GetLastWriteTimeUtc(target);

            // ---- ① 目标被独占锁定时，保存必须抛异常（不许悄悄返回） ----
            bool threw = false;
            string threwDetail = "";
            try
            {
                using (var hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try
                    {
                        // 写的是"另一份内容"，万一它真写进去了，下面的哈希比对就会抓到
                        ContractJson.WriteFile(target, AnotherTranscript());
                    }
                    catch (Exception ex)
                    {
                        threw = true;
                        threwDetail = ex.GetType().Name + "：" + ex.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                threwDetail = "锁定失败（前置条件没成立）：" + ex.Message;
            }

            Check("WriteFile：目标被独占锁定时必须抛异常（不许当成保存成功）",
                threw, threw ? threwDetail : "没有抛异常 —— 保存失败会被当成成功，界面永远不知道");

            // ---- ② 抛完之后，原文件逐字节不变 ----
            byte[] hashAfter = Sha256Of(target);
            Check("保存失败后原文件逐字节不变（SHA256 与失败前一致）",
                hashBefore.Length == hashAfter.Length && BytesEqual(hashBefore, hashAfter),
                "失败前 " + Hex(hashBefore) + " / 失败后 " + Hex(hashAfter));

            // ---- ③ 原文件的修改时间也不变（没被"偷偷重写一遍"） ----
            DateTime mtimeAfter = File.GetLastWriteTimeUtc(target);
            Check("保存失败后原文件修改时间不变（LastWriteTimeUtc 一致）",
                mtimeBefore == mtimeAfter,
                $"{mtimeBefore:yyyy-MM-dd HH:mm:ss.fffffff} → {mtimeAfter:yyyy-MM-dd HH:mm:ss.fffffff}");

            // ---- ④ 失败路径把自己的 .tmp 收拾干净 ----
            Check("保存失败后同目录没有残留 .tmp（下次写入不会被陈旧 tmp 干扰）",
                !File.Exists(tmp), File.Exists(tmp) ? "残留：" + tmp : "");

            // ---- ⑤ 1MB 文本往返：长度与内容完全一致（没有半截），且不带 BOM ----
            string bigPath = Path.Combine(tempDir, "big.md");
            var sb = new StringBuilder();
            while (sb.Length < 1024 * 1024)
                sb.Append("第 ").Append(sb.Length).Append(" 字起：这一行是为了把文件撑到 1MB 以上，")
                  .Append("用来证明大文件也是一次写完整的，不会留下半截。\n");
            string bigText = sb.ToString();

            ContractJson.WriteTextAtomic(bigPath, bigText);
            string bigBack = File.ReadAllText(bigPath, ContractJson.Utf8NoBom);

            Check("WriteTextAtomic：1MB 文本往返长度与内容完全一致（无半截）",
                bigBack.Length == bigText.Length,
                $"写入 {bigText.Length} 字符 / 读回 {bigBack.Length} 字符");

            Check("WriteTextAtomic：大文件读回内容与写入逐字相同",
                string.Equals(bigBack, bigText, StringComparison.Ordinal),
                bigBack.Length == bigText.Length ? "长度相同但内容不同" : "");

            Check("WriteTextAtomic：文件头 3 字节不是 BOM（外部 python/node 读得动）",
                !StartsWithBom(bigPath), StartsWithBom(bigPath) ? "EF BB BF" : "");

            // ---- ⑥ 目标已存在时覆盖写：换成新内容、旧内容不残留、无 .tmp ----
            string coverPath = Path.Combine(tempDir, "cover.md");
            ContractJson.WriteTextAtomic(coverPath, "旧内容：这一行马上要被换掉。");
            ContractJson.WriteTextAtomic(coverPath, "新内容：文件里只应该剩下这一行。");

            string coverBack = File.ReadAllText(coverPath, ContractJson.Utf8NoBom);
            Check("覆盖已存在文件：内容确实换成新的，旧内容不残留",
                coverBack == "新内容：文件里只应该剩下这一行。", coverBack);

            Check("覆盖已存在文件：同目录没有残留 .tmp",
                !File.Exists(coverPath + ".tmp"), "残留：" + coverPath + ".tmp");

            // ---- ⑦ 新写法的字节必须和"旧的裸 File.WriteAllText"一模一样 ----
            //   这一条盯的是"顺手把格式改了"：原子写换的是写盘手法，不是文件内容格式。
            //   左边 = 旧实现（裸写 + Utf8NoBom），右边 = 新实现（tmp + Replace），逐字节比。
            string legacyPath = Path.Combine(tempDir, "legacy.json");
            string atomicPath = Path.Combine(tempDir, "atomic.json");
            File.WriteAllText(legacyPath, ContractJson.Serialize(before), ContractJson.Utf8NoBom);
            ContractJson.WriteFile(atomicPath, before);

            byte[] legacyBytes = File.ReadAllBytes(legacyPath);
            byte[] atomicBytes = File.ReadAllBytes(atomicPath);
            Check("格式没变：WriteFile 的字节与旧的裸 File.WriteAllText 逐字节一致",
                BytesEqual(legacyBytes, atomicBytes),
                $"旧 {legacyBytes.Length} 字节 / 新 {atomicBytes.Length} 字节");
        }
        catch (Exception ex)
        {
            Check("原子写：临时目录里的往返（前置条件）", false, ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    // ============================================================
    // 八、数据目录隔离（T-4f1f38d8）
    // ============================================================
    //
    // 这一组盯的是「开发版别污染用户真实配置」那条规矩的**口径**：
    //   · 不设 LITECUT_DATA_DIR → 落点必须和改造前**逐字相同**（一个字节都不变）；
    //   · 设了 → 配置、日志、单实例锁三样一起搬走，且同一个路径永远同一个锁名。
    // 全部是纯字符串断言，不建目录、不写文件（真落盘留给 工具\隔离验证 --write）。

    /// <summary>替身 %APPDATA%（只用字符串拼，不要求这台机器上真有这个目录）。</summary>
    private const string FakeRoaming = @"C:\Users\tester\AppData\Roaming";

    /// <summary>替身 %LOCALAPPDATA%。</summary>
    private const string FakeLocal = @"C:\Users\tester\AppData\Local";

    private static void AppDataPathsChecks()
    {
        // ---- ① 什么算"设了" ----
        Check("隔离判定：null = 没设", !AppDataPaths.IsIsolated(null));
        Check("隔离判定：空串与纯空格 = 没设（不许变成名叫“ ”的目录）",
            !AppDataPaths.IsIsolated("") && !AppDataPaths.IsIsolated("   ") && !AppDataPaths.IsIsolated("\t"));
        Check("隔离判定：有值 = 隔离", AppDataPaths.IsIsolated(@"D:\temp\wb-dev"));

        // ---- ② 未隔离：落点与改造前逐字相同 ----
        string legacy = AppDataPaths.ResolveDataDir(null, FakeRoaming);
        Check("未隔离：配置落点 = %APPDATA%\\翊瑄的工作台（与改造前逐字相同）",
            legacy == Path.Combine(FakeRoaming, "翊瑄的工作台"), legacy);

        // ---- ③ 已隔离：绝对 / 相对 / 结尾分隔符 ----
        string abs = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wb-dev"));
        Check("已隔离：绝对路径原样采用（只做规范化）",
            AppDataPaths.ResolveDataDir(abs, FakeRoaming) == abs,
            AppDataPaths.ResolveDataDir(abs, FakeRoaming));

        string relBase = Path.GetTempPath();
        Check("已隔离：相对路径按 appDataRoot 展开（不吃当前工作目录）",
            AppDataPaths.ResolveDataDir("wb-dev", relBase) == Path.Combine(Path.GetFullPath(relBase), "wb-dev"),
            AppDataPaths.ResolveDataDir("wb-dev", relBase));

        Check("已隔离：结尾多余的分隔符被去掉（同一个目录只能有一个写法）",
            AppDataPaths.ResolveDataDir(abs + Path.DirectorySeparatorChar, FakeRoaming) == abs,
            AppDataPaths.ResolveDataDir(abs + Path.DirectorySeparatorChar, FakeRoaming));

        // ---- ④ 单实例锁的名字 ----
        Check("互斥名：未隔离 = 历史写死的那个名字，一字不差",
            AppDataPaths.MutexName(null, FakeRoaming) == @"Local\LiteCut_Workbench_SingleInstance_A7F3",
            AppDataPaths.MutexName(null, FakeRoaming));

        string m1 = AppDataPaths.MutexName(@"D:\temp\wb-dev", FakeRoaming);
        string m2 = AppDataPaths.MutexName(@"D:\temp\wb-dev", FakeRoaming);
        Check("互斥名：隔离后与正式版不同名（两边的锁不能互相挡）",
            m1 != AppDataPaths.BaseMutexName, m1);
        Check("互斥名：同一目录算两次必须同名（否则重启一次就多一把锁）",
            m1 == m2, m1 + " vs " + m2);
        Check("互斥名：路径大小写不同仍是同一把锁（Windows 路径不区分大小写）",
            AppDataPaths.MutexName(@"d:\TEMP\wb-dev", FakeRoaming) == m1,
            AppDataPaths.MutexName(@"d:\TEMP\wb-dev", FakeRoaming));
        Check("互斥名：不同隔离目录不同名（两个开发版可以各开各的）",
            AppDataPaths.MutexName(@"D:\temp\wb-dev2", FakeRoaming) != m1,
            AppDataPaths.MutexName(@"D:\temp\wb-dev2", FakeRoaming));

        // ---- ⑤ 崩溃日志跟着走 ----
        Check("日志目录：未隔离 = %LOCALAPPDATA%\\翊瑄的工作台（与改造前逐字相同）",
            AppDataPaths.ResolveLogDir(null, FakeRoaming, FakeLocal) == Path.Combine(FakeLocal, "翊瑄的工作台"),
            AppDataPaths.ResolveLogDir(null, FakeRoaming, FakeLocal));

        Check("日志目录：已隔离 = 隔离目录（开发版的崩溃日志不混进用户那份）",
            AppDataPaths.ResolveLogDir(@"D:\temp\wb-dev", FakeRoaming, FakeLocal) == @"D:\temp\wb-dev",
            AppDataPaths.ResolveLogDir(@"D:\temp\wb-dev", FakeRoaming, FakeLocal));

        // ---- ⑥ 来源说明（排障时界面/报告里要讲人话）----
        Check("来源说明：未隔离说“默认位置”，隔离说环境变量名",
            AppDataPaths.DescribeSource(null).Contains("默认位置")
            && AppDataPaths.DescribeSource(@"D:\temp\wb-dev").Contains(AppDataPaths.DataDirEnvVar),
            AppDataPaths.DescribeSource(null) + " / " + AppDataPaths.DescribeSource(@"D:\temp\wb-dev"));
    }

    /// <summary>给"锁定失败"那条断言准备的替身内容（结构相同、内容不同）。</summary>
    private static Transcript AnotherTranscript()
    {
        var t = TranscriptImporter.Load(StandardTranscriptJson).Transcript ?? new Transcript();
        t.FullText = "这份内容本来不该写进磁盘（目标文件被占着）。";
        return t;
    }

    private static byte[] Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(fs);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private static bool StartsWithBom(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] head = new byte[3];
        int read = fs.Read(head, 0, 3);
        return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }
}
