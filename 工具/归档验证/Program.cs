// ============================================================
// 工具\归档验证\Program.cs - 「归档落点同名时不许静默覆盖」的无头断言
// ============================================================
//
// 用法（在工程根 D:\工作台工程\工作台 下）：
//     $env:LITECUT_DATA_DIR = "$env:TEMP\wb-archiveverify"      # 可选：不设本程序会自己钉一个临时目录
//     dotnet run --project .\工具\归档验证\归档验证.csproj -c Release
//
// 断言什么（对照派卡验收）：
//   ① 首次归档 A 成功：Prepare.Ok / Execute.Ok，落点里 归档清单.json 的 项目Id == guid-aaaa
//   ② 采集 A 落盘指纹（相对路径 → SHA256 + 长度）
//   ③ 同名但不同 id 的 B：Prepare 拒绝（理由含落点路径 + 「已有另一个项目」），
//      Execute 拿「预演时还通过」的那份 plan 再喂一次也必须被拒（= 二次判断生效）
//   ④ 被拒之后，A 落盘的全部文件逐条指纹不变
//   ⑤ 同一个项目（同 id）重新归档仍能覆盖 —— 旧行为不许改坏
//   ⑥ 落点存在但读不到清单 → 拒绝（保守口径）
//
// 为什么打「通过 N 项，失败 M 项」这一行：
//   和 工具\自检 / 工具\隔离验证 / 工具\根目录守卫验证 同一口径；
//   以后要接进 健康检查.ps1 门禁，正则吃的就是这一行（本卡不改门禁脚本，由工程岗决定接不接）。
//
// 为什么所有虚拟项目都必须落在 %TEMP%：
//   归档语义是「复制 → 校验 → 删本地」，Execute 会真的删本地文件。
//   本程序跑之前先把所有项目根打印一遍再断言它们在 Path.GetTempPath() 下 ——
//   宁可断言失败，也不许 %TEMP% 之外的任何东西被删。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteCut;
using LiteCut.Core.Contracts;
using LiteCut.Workbench;
using LiteCut.Workbench.Project;

namespace LiteCut.ArchiveVerify;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failed = new();

    /// <summary>打一条断言。[过] / [不信]（不信 = 没通过，语义比「败」轻一点：是"这条证据我不认"）。</summary>
    private static void Check(string name, bool ok, string? detail = null)
    {
        string tip = detail == null ? "" : "（" + detail + "）";
        if (ok)
        {
            _passed++;
            Console.WriteLine("  [过] " + name + tip);
        }
        else
        {
            Failed.Add(name + tip);
            Console.WriteLine("  [不信] " + name + tip);
        }
    }

    [STAThread]
    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 个别宿主不让改，忽略 */ }

        // ---- 每次运行新建一个干净的临时根（路径里带时间戳，绝不复用上一次的残留）----
        string tempRoot = Path.Combine(Path.GetTempPath(), "归档验证-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        // ---- 数据目录隔离：没设 LITECUT_DATA_DIR 就自己钉到临时根下 ----
        //   Execute 第一次归档会写 config.json（记卷序列号）；不钉住就是写进用户的真实配置。
        //   必须在任何 AppConfig 访问之前设好（AppConfig 的落点是进程内只算一次的静态值）。
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LITECUT_DATA_DIR")))
            Environment.SetEnvironmentVariable("LITECUT_DATA_DIR", Path.Combine(tempRoot, "数据"));

        Console.WriteLine("归档验证 · 落点同名时不许静默覆盖");
        Console.WriteLine("临时根：" + tempRoot);
        Console.WriteLine("隔离数据目录：" + AppConfig.DataDir);
        Console.WriteLine();

        // ---- 安全闸 1：数据目录真的隔离了 ----
        string realAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "翊瑄的工作台");
        Check("数据目录已隔离（不是 %APPDATA%\\翊瑄的工作台）",
              !string.Equals(AppConfig.DataDir, realAppData, StringComparison.OrdinalIgnoreCase),
              AppConfig.DataDir);

        // ---- 界面资源要加载起来才建得了 WorkbenchWindow 的静态状态（不 Show 任何窗口）----
        var app = new App();
        app.InitializeComponent();

        // ---- 假硬盘：落点根目录 ----
        string archiveRoot = Path.Combine(tempRoot, "假硬盘", "归档");
        Directory.CreateDirectory(archiveRoot);

        var cfg = WorkbenchWindow.State.Config;
        cfg.ArchiveRoot = archiveRoot;
        cfg.ArchiveVolumeSerial = "";        // 走「首用」分支：第一次归档不校验卷序列号
        cfg.ArchiveVolumeLabel = "";

        // ---- 两个同名项目（名字一样、id 不同）+ 一个用于「读不到清单」的项目 ----
        string localRoot = Path.Combine(tempRoot, "本地");
        var a = MakeProject(Path.Combine(localRoot, "同名测试A"), "同名测试", "guid-aaaa");
        var b = MakeProject(Path.Combine(localRoot, "同名测试B"), "同名测试", "guid-bbbb");
        var d = MakeProject(Path.Combine(localRoot, "读不到清单"), "读不到清单", "guid-cccc");

        // ---- 安全闸 2：所有虚拟项目都在 %TEMP% 下（Execute 会删本地文件）----
        Console.WriteLine("虚拟项目根（Execute 会删这些目录里的本地大件，所以必须全在 %TEMP% 下）：");
        foreach (var r in new[] { a, b, d }) Console.WriteLine("  · " + r.Dir);
        Console.WriteLine("落点根（假硬盘）：" + archiveRoot);
        Console.WriteLine();

        string tempFull = Path.GetFullPath(Path.GetTempPath());
        Check("安全闸：三个虚拟项目都在 %TEMP% 下",
              new[] { a.Dir, b.Dir, d.Dir }.All(p =>
                  Path.GetFullPath(p).StartsWith(tempFull, StringComparison.OrdinalIgnoreCase)),
              tempFull);
        Check("安全闸：假硬盘（落点根）也在 %TEMP% 下",
              Path.GetFullPath(archiveRoot).StartsWith(tempFull, StringComparison.OrdinalIgnoreCase),
              archiveRoot);

        // ========================================================
        // ③-a 先算一份 B 的「预演」（此刻落点还不存在 → 必须通过）
        //      这张预演是后面验「Execute 二次判断」的唯一道具：
        //      如果只拿 Prepare 已经拒掉的 plan 去喂 Execute，Execute 会在第一行
        //      （if (!plan.Ok) return ...）就返回，根本走不到真正的二次判断。
        // ========================================================
        Console.WriteLine("== ① B 的预演（落点还不存在时应当通过） ==");
        var planBStale = ArchiveService.Prepare(b);
        Check("B 的预演通过（落点不存在 → 放行）", planBStale.Ok, planBStale.Reason);
        Check("B 的落点 = 归档根\\同名测试", planBStale.TargetDir == Path.Combine(archiveRoot, "同名测试"),
              planBStale.TargetDir);

        // ========================================================
        // ① 首次归档 A：成功
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ① 首次归档 A（同名测试 / guid-aaaa） ==");
        var planA = ArchiveService.Prepare(a);
        Check("Prepare(A) 通过", planA.Ok, planA.Reason);
        var execA = ArchiveService.Execute(a, planA);
        Check("Execute(A) 成功", execA.Ok, execA.Message);
        Check("落点目录已建立", Directory.Exists(planA.TargetDir), planA.TargetDir);

        var mfA = ReadManifest(planA.TargetDir);
        Check("落点里 _归档\\归档清单.json 的 项目Id == guid-aaaa",
              mfA != null && mfA.项目Id == "guid-aaaa", mfA == null ? "(读不到清单)" : mfA.项目Id);
        Check("落点里的归档清单记的是 A 的名字", mfA != null && mfA.项目名 == "同名测试",
              mfA?.项目名 ?? "(读不到)");

        // ========================================================
        // ② 采集 A 落盘指纹
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ② 采集 A 落盘指纹（相对路径 → SHA256 + 长度） ==");
        var snapshot1 = Snapshot(planA.TargetDir);
        Check("指纹已采集（落点里文件数 > 0）", snapshot1.Count > 0, snapshot1.Count + " 个文件");
        foreach (var kv in snapshot1.OrderBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine("    " + kv.Key + "　" + kv.Value.Size + " B　" + kv.Value.Sha.Substring(0, 16) + "…");

        // ========================================================
        // ③ 归档 B（同名不同 id）必须被拒：Prepare 拒 + Execute 拒
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ③ 归档 B（同名、不同 id）必须被拒 ==");
        var planB = ArchiveService.Prepare(b);
        Check("Prepare(B) 被拒（Ok == false）", !planB.Ok, planB.Ok ? "竟然通过了" : "符合预期");
        Check("拒绝理由含落点全路径", planB.Reason.Contains(planB.TargetDir), FirstLine(planB.Reason));
        Check("拒绝理由写明「已有另一个项目：同名测试（id guid-aaaa）」",
              planB.Reason.Contains("已有另一个项目：同名测试（id guid-aaaa）"), FirstLine(planB.Reason));
        Check("拒绝理由给出三条出路（改名 / 换归档目录 / 自己挪走）",
              planB.Reason.Contains("改个名字再归档") &&
              planB.Reason.Contains("归档落点根目录") &&
              planB.Reason.Contains("挪走"),
              FirstLine(planB.Reason));

        var execBStale = ArchiveService.Execute(b, planBStale);
        Check("Execute 喂「预演时通过」的那份 plan，仍必须被拒（二次判断生效）",
              !execBStale.Ok, execBStale.Message.Split('\n')[0]);
        Check("二次拒绝的理由也含落点全路径 + 「已有另一个项目」",
              execBStale.Message.Contains(planBStale.TargetDir) &&
              execBStale.Message.Contains("已有另一个项目"),
              FirstLine(execBStale.Message));
        Check("B 自己的项目目录没被动过（本地文件都还在）",
              File.Exists(Path.Combine(b.Dir, "script.md")), b.Dir);

        // ========================================================
        // ④ A 落盘的文件一个没变
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ④ 被拒之后：A 落盘文件逐条指纹比对 ==");
        var snapshot2 = Snapshot(planA.TargetDir);
        var diffs = Diff(snapshot1, snapshot2);
        foreach (string line in diffs) Console.WriteLine("    [差异] " + line);
        Check("A 落盘文件一个没变（路径集合 / 长度 / SHA256 逐条一致）",
              diffs.Count == 0, diffs.Count == 0 ? $"{snapshot2.Count} 个文件逐条一致" : $"{diffs.Count} 处差异");

        // ========================================================
        // ⑤ 同一个项目（同 id）重新归档仍能覆盖
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ⑤ 同一个项目重新归档（同 id 必须还能覆盖） ==");
        const string newScript = "# 稿子 · 同名测试（改过一版：验证覆盖照旧生效）\n";
        File.WriteAllText(Path.Combine(a.Dir, "script.md"), newScript, ContractJson.Utf8NoBom);

        var planA2 = ArchiveService.Prepare(a);
        Check("Prepare(A) 第二次仍通过（同 id 允许覆盖）", planA2.Ok, planA2.Reason);
        var execA2 = ArchiveService.Execute(a, planA2);
        Check("Execute(A) 第二次成功", execA2.Ok, execA2.Message);

        string targetScript = Path.Combine(planA.TargetDir, "script.md");
        bool overwritten = File.Exists(targetScript) &&
                           File.ReadAllText(targetScript, Encoding.UTF8).Contains("改过一版");
        Check("落点上 script.md 的内容已变成新内容（覆盖照旧生效）", overwritten, targetScript);

        // ========================================================
        // ⑥ 落点存在但读不到清单 → 拒绝（保守口径）
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ⑥ 落点存在但读不到清单 → 拒绝 ==");
        string target6 = Path.Combine(archiveRoot, "读不到清单");
        Directory.CreateDirectory(target6);        // 有同名目录，但里面没有 _归档\归档清单.json

        var planD = ArchiveService.Prepare(d);
        Check("Prepare(读不到清单) 被拒", !planD.Ok, planD.Ok ? "竟然通过了" : "符合预期");
        Check("拒绝理由含「读不到落点的归档清单，无法确认是不是同一个项目」",
              planD.Reason.Contains("读不到落点的归档清单，无法确认是不是同一个项目"), FirstLine(planD.Reason));
        Check("拒绝理由含落点全路径", planD.Reason.Contains(planD.TargetDir), FirstLine(planD.Reason));
        Check("这一条没有把落点里的东西挪走/新建（目录还是空的）",
              Directory.EnumerateFileSystemEntries(target6).Count() == 0,
              Directory.EnumerateFileSystemEntries(target6).Count() + " 个条目");

        // ========================================================
        // ⑦ 附带一条：清单读得动、但 项目Id 是空的 → 也拒绝（不是「大概是它」）
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ⑦ 附带：清单在、但 项目Id 为空 → 也拒绝 ==");
        var e = MakeProject(Path.Combine(localRoot, "清单没id"), "清单没id", "guid-dddd");
        string target7 = Path.Combine(archiveRoot, "清单没id");
        Directory.CreateDirectory(Path.Combine(target7, "_归档"));
        ContractJson.WriteFile(Path.Combine(target7, "_归档", "归档清单.json"),
                               new ArchiveManifest { 项目名 = "别的项目", 项目Id = "" });

        var planE = ArchiveService.Prepare(e);
        Check("Prepare 被拒（清单里没有项目 id）", !planE.Ok, planE.Ok ? "竟然通过了" : "符合预期");
        Check("拒绝理由含「无法确认是不是同一个项目」并报出落点里的是谁",
              planE.Reason.Contains("无法确认是不是同一个项目") && planE.Reason.Contains("别的项目"),
              FirstLine(planE.Reason));

        // ========================================================
        // ⑧ 文案原文（照实打印，报告要逐字引用「用户看到的是什么」）
        // ========================================================
        Console.WriteLine();
        Console.WriteLine("== ⑧ 拒绝时的文案原文（用户看到的就是这些字） ==");
        Console.WriteLine();
        Console.WriteLine("---- 情形一：落点上已有另一个项目（Prepare(B) 的 plan.Reason）----");
        Console.WriteLine(planB.Reason);
        Console.WriteLine();
        Console.WriteLine("---- 情形二：落点存在但读不到清单（Prepare(读不到清单) 的 plan.Reason）----");
        Console.WriteLine(planD.Reason);
        Console.WriteLine();
        Console.WriteLine("---- 情形三：Execute 二次判断命中（ArchiveResult.Message）----");
        Console.WriteLine(execBStale.Message);

        // ========================================================
        // 收尾
        // ========================================================
        Console.WriteLine();
        Console.WriteLine($"归档验证结果：通过 {_passed} 项，失败 {Failed.Count} 项");
        if (Failed.Count > 0)
        {
            foreach (string f in Failed) Console.WriteLine("    · " + f);
        }

        // 本次临时根路径写明（留着给人复查；不自动删，便于出问题时看现场）
        Console.WriteLine("临时根（可整目录删掉）：" + tempRoot);
        return Failed.Count == 0 ? 0 : 1;
    }

    // ========================================================
    // 造一个虚拟项目
    // ========================================================

    /// <summary>
    /// 造一个「已完成」的虚拟项目：project.json + 几个小文件（都小于 2MB，
    /// 按归档的分类规则属于"本地小桩"——搬到硬盘、本地也留，不会被删）。
    /// </summary>
    /// <param name="dir">项目目录（必须在 %TEMP% 下）</param>
    /// <param name="name">项目名（两个项目用同一个名字，才能撞出同名落点）</param>
    /// <param name="id">项目 id（归档清单里"这块地是谁的"就靠它）</param>
    private static ProjectRef MakeProject(string dir, string name, string id)
    {
        var model = new ProjectModel
        {
            Id = id,
            Name = name,
            Channel = "归档验证",
            Note = "虚拟项目，只存在于 %TEMP%",
            Status = ProjectStatus.Active,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            // 归档门槛看标签、不看有没有视频：手动标成「已完成」即可
            Label = new LabelPart
            {
                Manual = ProjectLabels.Done,
                ManualSetAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            },
        };

        var r = new ProjectRef { Dir = dir, Model = model };
        Directory.CreateDirectory(r.Dir);
        Directory.CreateDirectory(r.MediaDir);
        ProjectStore.Save(r);          // 写 project.json（走产品自己的存档入口）

        File.WriteAllText(Path.Combine(dir, "script.md"), $"# 稿子 · {name}\n", ContractJson.Utf8NoBom);

        string shotDir = Path.Combine(dir, "stages", "03-shot");
        Directory.CreateDirectory(shotDir);
        File.WriteAllText(Path.Combine(shotDir, "镜头表.json"), "{\n  \"rows\": []\n}\n", ContractJson.Utf8NoBom);

        string assembleDir = Path.Combine(dir, "stages", "05-assemble");
        Directory.CreateDirectory(assembleDir);
        File.WriteAllText(Path.Combine(assembleDir, "对稿说明.txt"), "虚拟产物，没有视频\n", ContractJson.Utf8NoBom);

        return r;
    }

    // ========================================================
    // 小工具
    // ========================================================

    /// <summary>读落点里的归档清单（和生产代码同一个读法：ContractJson.ReadFile）。</summary>
    private static ArchiveManifest? ReadManifest(string target)
        => ContractJson.ReadFile<ArchiveManifest>(Path.Combine(target, "_归档", "归档清单.json"));

    /// <summary>落盘指纹：相对路径 → (SHA256, 长度)。</summary>
    private static Dictionary<string, (string Sha, long Size)> Snapshot(string root)
    {
        var map = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, f);
            map[rel] = (Sha256(f), new FileInfo(f).Length);
        }
        return map;
    }

    /// <summary>逐条比对两份指纹，返回人话差异（空 = 完全一致）。</summary>
    private static List<string> Diff(Dictionary<string, (string Sha, long Size)> before,
                                    Dictionary<string, (string Sha, long Size)> after)
    {
        var lines = new List<string>();
        foreach (var kv in before)
        {
            if (!after.TryGetValue(kv.Key, out var now)) { lines.Add($"没了：{kv.Key}"); continue; }
            if (now.Size != kv.Value.Size)
                lines.Add($"长度变了：{kv.Key}（{kv.Value.Size} → {now.Size}）");
            if (!string.Equals(now.Sha, kv.Value.Sha, StringComparison.OrdinalIgnoreCase))
                lines.Add($"内容变了：{kv.Key}");
        }
        foreach (string key in after.Keys)
            if (!before.ContainsKey(key)) lines.Add($"多出来：{key}");

        return lines;
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static string FirstLine(string text)
    {
        string s = (text ?? "").Trim();
        int n = s.IndexOf('\n');
        if (n > 0) s = s.Substring(0, n);
        return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }
}
