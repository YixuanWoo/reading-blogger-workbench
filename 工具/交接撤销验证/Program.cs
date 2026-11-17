// ============================================================
// 工具\交接撤销验证\Program.cs - 「撤销这次拆合之后，渲染清单跟得上吗」
// ============================================================
//
// 这张卡要修的病（T-6a72ab31）：
//   正向交接（Execute）会把渲染清单里的 File 改成**新**镜号，
//   撤销（Rollback）却只把文件搬回旧名、没把清单改回去
//   → 渲染页拿 shot_<新号>.mp4 去对当前镜头表的镜号，对不上，
//     整页标「产物过期（镜号已对不上）」—— 不丢数据，但白白重渲一整页。
//
// 本程序做的就是把这三种状态按真实 API 走一遍并逐条断言：
//   ① 基线     清单每一条 File == shot_{Shot:000}.mp4，且文件都在盘上
//   ② 正向      Execute 之后清单每条都指向**新**镜号（证明没把正向改坏）
//   ③ 撤销      Rollback 之后：文件回旧名 / 清单每条对得上真文件 /
//               「镜号已对不上」计数 == 0 / shot-overrides.json 键回旧镜号
//   ④ 幂等      对同一 log 再撤销一次：不抛异常、不把东西搬乱、清单仍自洽
//   ⑤ 执行失败  Execute 第二遍落正式名失败 → 自动回滚后清单仍指向磁盘真文件
//
// 全部数据在 %TEMP% 下新建，跑完删掉（工程根下的东西一个字节都不碰）。
// 证据（撤销前后两份清单原样 + 全部断言输出）写到 --evidence 指定的目录。
//
// 为什么断言要打「通过 N 项，失败 M 项」：
//   与 工具\自检 同一口径，将来接进 健康检查.ps1 就不用改正则。
//
// 为什么用例 ③ 的过期计数要照抄渲染页的判据：
//   「产物过期」这四个字是**渲染页**说的（Step3RenderView.RefreshOneStatus）。
//   自己另发明一条判据，可能测出一条"我自己觉得对"的绿，页面照样一片橙。
//   所以这里只借两样东西：RenderManifest.Mp4NameOf(镜号) 和"逐条拿 File 跟它比"。
//
// 为什么 log 优先用真 API（ReindexMigration.Plan）而不是手搓：
//   OldIndexById / NewIndexById / RemovedIds / Moves 这四样只要有一处跟真实流程
//   对不上，回滚断言就等于在测我自己编的数据。Plan 只依赖项目目录结构
//   （project.Dir / 渲染产物 / stages\04-render），不读配置、不联网，虚拟项目能直接跑。
//   ★ 卡面写的 Plan(project, outputDir, notes) 是三个参数，实际签名是
//     Plan(project, outputDir, committed, draft)（两张镜头表靠 ID 对账）——
//     按实际签名调用，报告里已写明这处偏差。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteCut.Workbench;
using LiteCut.Workbench.Project;

namespace LiteCut.HandoffRevertVerify;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failed = new();
    private static readonly StringBuilder Buf = new();
    private static string? _evidenceDir;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 个别宿主不让改，忽略 */ }

        string? keepArg = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--evidence", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                _evidenceDir = args[++i];
            else if (string.Equals(args[i], "--keep", StringComparison.OrdinalIgnoreCase))
                keepArg = "keep";
            else
            {
                Out("用法：HandoffRevertVerify [--evidence <证据目录>] [--keep]");
                return 2;
            }
        }
        if (_evidenceDir != null)
        {
            try { Directory.CreateDirectory(_evidenceDir); }
            catch (Exception ex) { Console.WriteLine("证据目录建不出来：" + ex.Message); return 2; }
        }

        string root = Path.Combine(Path.GetTempPath(),
            "撤销验证-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-ff") + "-" + Guid.NewGuid().ToString("N")[..6]);

        Out("工作台交接撤销验证");
        Out("临时根：" + root);
        Out("工程里的东西一个字节都不写；这个目录跑完就删（--keep 可留）。");
        Out("");

        try
        {
            Directory.CreateDirectory(root);
            RunCaseBaselineForwardRollback(root);
            RunCaseIdempotent(root);
            RunCaseExecuteFailure(root);
        }
        catch (Exception ex)
        {
            Failed.Add("整个流程抛异常：" + ex.Message);
            Out("[不信] 整个流程抛异常 — " + ex);
        }

        Out("");
        Out($"通过 {_passed} 项，失败 {Failed.Count} 项");
        foreach (string f in Failed) Out($"  失败：{f}");
        Out("临时根" + (keepArg == "keep" ? "保留在 " : "已删除：") + root);

        if (keepArg != "keep")
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (Exception ex) { Out("（临时目录没删干净，请手工清理：" + ex.Message + "）"); }
        }

        if (_evidenceDir != null)
        {
            string txt = Path.Combine(_evidenceDir, "卡B-撤销验证输出.txt");
            File.WriteAllText(txt, Buf.ToString(), new UTF8Encoding(false));
            Console.WriteLine("证据已写入：" + txt);
        }

        return Failed.Count == 0 ? 0 : 1;
    }

    // ========================================================
    // 用例 ①②③：基线 → 正向交接 → 撤销
    // ========================================================

    private static void RunCaseBaselineForwardRollback(string root)
    {
        Out("══════ 用例 ① 基线 / ② 正向未破 / ③ 撤销 ══════");
        var env = BuildVirtualProject(Path.Combine(root, "主场景"), 8, mergedId: "s04");
        Out($"虚拟项目：{env.Project.Dir}");
        Out($"镜头表：交接前 {env.Committed.Count} 镜 → 交接后 {env.Draft.Count} 镜（被合并掉：{string.Join("、", env.Log.RemovedIds)}）");
        Out($"迁移计划：{env.Log.Moves.Count} 条搬迁（{string.Join("；", env.Log.Notes)}）");
        Out("");

        // ---- ① 基线 ----
        var baseMap = RenderManifest.Load(env.OutputDir);
        Check("① 基线：清单有 8 条", baseMap.Count == env.Committed.Count, $"实际 {baseMap.Count} 条");
        int baseBad = 0;
        foreach (var kv in baseMap)
        {
            string expected = RenderManifest.Mp4NameOf(kv.Value.Shot);
            bool fileOk = File.Exists(Path.Combine(env.OutputDir, kv.Value.File));
            if (!string.Equals(kv.Value.File, expected, StringComparison.OrdinalIgnoreCase) || !fileOk) baseBad++;
        }
        Check("① 基线：每条 File == shot_{Shot:000}.mp4 且文件在盘上", baseBad == 0, $"不合口径 {baseBad} 条");

        // ---- ② 正向交接 ----
        var (ok, msg) = ReindexMigration.Execute(env.Project, env.Log);
        Check("② 正向：Execute 返回 true", ok, msg);
        Check("② 正向：log 状态 done", env.Log.Status == "done", "Status=" + env.Log.Status);

        var afterExec = RenderManifest.Load(env.OutputDir);
        int fwdBad = 0;
        foreach (var kv in env.Log.NewIndexById)
        {
            if (!afterExec.TryGetValue(kv.Key, out var e)) { fwdBad++; continue; }
            string expected = RenderManifest.Mp4NameOf(kv.Value);
            bool fileOk = File.Exists(Path.Combine(env.OutputDir, expected));
            if (!string.Equals(e.File, expected, StringComparison.OrdinalIgnoreCase) ||
                e.Shot != kv.Value || !fileOk) fwdBad++;
        }
        Check("② 正向：清单每条指向**新**镜号且磁盘按新名存在", fwdBad == 0, $"不合口径 {fwdBad} 条");
        foreach (string removed in env.Log.RemovedIds)
            Check($"② 正向：被合并掉的 {removed} 条目已从清单移除", !afterExec.ContainsKey(removed));
        foreach (string removed in env.Log.RemovedIds)
        {
            var park = env.Log.Moves.FirstOrDefault(m => m.Id == removed && m.Kind.StartsWith("park-", StringComparison.Ordinal));
            bool parked = park != null && File.Exists(park.To);
            Check($"② 正向：{removed} 的产物停在 _已废弃\\（没删）", parked, park?.To ?? "计划里没有停放记录");
        }
        Out("   正向交接后的清单（原样）：");
        foreach (var kv in afterExec.OrderBy(k => k.Key, StringComparer.Ordinal))
            Out($"     {kv.Key}  Shot={kv.Value.Shot}  File={kv.Value.File}  " +
                $"磁盘={(File.Exists(Path.Combine(env.OutputDir, kv.Value.File)) ? "有" : "无")}");

        // 撤销前清单 dump（原样字节，不是我再序列化一遍）
        CopyManifest(env.OutputDir, "卡B-撤销前清单.json");

        // ---- ③ 撤销 ----
        var (rok, rmsg) = ReindexMigration.Rollback(env.Project, env.Log);
        Check("③ 撤销：Rollback 返回 true", rok, rmsg);
        Check("③ 撤销：log 状态 rolledback", env.Log.Status == "rolledback", "Status=" + env.Log.Status);

        // A 文件回到旧名
        int fileBackBad = 0;
        foreach (var row in env.Committed)
            if (!File.Exists(Path.Combine(env.OutputDir, RenderManifest.Mp4NameOf(row.Index)))) fileBackBad++;
        Check("③-A 磁盘上每个 mp4 的文件名回到旧镜号", fileBackBad == 0, $"缺 {fileBackBad} 个");

        var afterRollback = RenderManifest.Load(env.OutputDir);
        CopyManifest(env.OutputDir, "卡B-撤销后清单.json");

        // B 清单每条 File 都对应磁盘真文件
        Out("   逐条比对（撤销后）：");
        Out("     id | Shot | File | 磁盘是否存在");
        int notExist = 0;
        foreach (var kv in afterRollback.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            bool exist = File.Exists(Path.Combine(env.OutputDir, kv.Value.File));
            if (!exist) notExist++;
            Out($"     {kv.Key} | {kv.Value.Shot} | {kv.Value.File} | {(exist ? "是" : "否")}");
        }
        Check("③-B 清单里每一条 File 都对应磁盘上真实存在的文件", notExist == 0, $"对不上 {notExist} 条");

        // C 用渲染页同一判据数「镜号已对不上」
        int staleIdx = 0;
        foreach (var kv in afterRollback)
        {
            var row = env.Committed.FirstOrDefault(r => r.Id == kv.Key);
            if (row == null) continue;   // 撤销后镜头表里没有的 id 不参与渲染页判断
            string expected = RenderManifest.Mp4NameOf(row.Index);
            if (!string.Equals(kv.Value.File, expected, StringComparison.OrdinalIgnoreCase))
            {
                staleIdx++;
                Out($"     [对不上] {kv.Key}：清单 {kv.Value.File} ≠ 镜号 {row.Index} 应有的 {expected}");
            }
        }
        Check("③-C 「产物过期（镜号已对不上）」计数为 0", staleIdx == 0, $"计数 {staleIdx}");
        var noEntry = env.Committed.Where(r => !afterRollback.ContainsKey(r.Id)).Select(r => r.Id).ToList();
        Out($"   （撤销后没有清单条目的镜头：{(noEntry.Count == 0 ? "无" : string.Join("、", noEntry))}" +
            " —— 走渲染页「没有清单条目 → 只看实现是否比产物新」那条分支，不参与上面的计数）");
        Check("③-C' 被合并掉的镜头没有清单条目（正因为正向已 RemovedIds 删过）", noEntry.Count == 1 && noEntry[0] == env.Log.RemovedIds[0]);

        // D overrides 键回旧镜号
        string ovPath = Path.Combine(env.Project.RenderStageDir, "shot-overrides.json");
        var ovKeys = ReadOverrideKeys(ovPath).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        string[] expectKeys = env.Committed.Where(r => r.Id == "s05" || r.Id == "s06")
                                           .Select(r => r.Index.ToString())
                                           .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Check("③-D shot-overrides.json 的键回到旧镜号", ovKeys.SequenceEqual(expectKeys),
            "实际 [" + string.Join(",", ovKeys) + "] 期望 [" + string.Join(",", expectKeys) + "]");

        // 撤销前后清单差异表
        Out("");
        Out("   撤销前后清单差异（撤销前 = Execute 刚交接完；撤销后 = Rollback 之后）：");
        Out("     id | 撤销前 Shot/File | 撤销后 Shot/File | 磁盘实际");
        foreach (string id in env.Committed.Select(r => r.Id).Concat(afterExec.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            string before = afterExec.TryGetValue(id, out var b) ? $"Shot={b.Shot} File={b.File}" : "（无条目）";
            string after = afterRollback.TryGetValue(id, out var a) ? $"Shot={a.Shot} File={a.File}" : "（无条目）";
            string disk = afterRollback.TryGetValue(id, out var a2)
                ? (File.Exists(Path.Combine(env.OutputDir, a2.File)) ? a2.File + " 在" : a2.File + " 不在") : "—";
            Out($"     {id} | {before} | {after} | {disk}");
        }
        Out("");

        // 残留临时文件
        int tmpLeft = Directory.EnumerateFiles(env.OutputDir, "*.tmp", SearchOption.AllDirectories).Count();
        Check("③ 撤销后没有残留的中转临时文件", tmpLeft == 0, $"残留 {tmpLeft} 个");
    }

    // ========================================================
    // 用例 ④：对同一 log 再撤销一次
    // ========================================================

    private static void RunCaseIdempotent(string root)
    {
        Out("══════ 用例 ④ 幂等（对同一 log 再撤销一次）══════");
        var env = BuildVirtualProject(Path.Combine(root, "幂等场景"), 8, mergedId: "s04");

        var exec = ReindexMigration.Execute(env.Project, env.Log);
        var (_, rm1) = exec.ok
            ? ReindexMigration.Rollback(env.Project, env.Log)
            : (false, "正向都没成功，撤销无从谈起");
        // 上面这两步就是把 ①②③ 走完，这里只关心"再来一次"
        var (ok2, msg2) = (true, "");
        try { (ok2, msg2) = ReindexMigration.Rollback(env.Project, env.Log); }
        catch (Exception ex) { ok2 = false; msg2 = "抛异常：" + ex.Message; }
        Check("④ 再撤销一次：不抛异常且返回 true", ok2, msg2);

        int fileBackBad = 0;
        foreach (var row in env.Committed)
            if (!File.Exists(Path.Combine(env.OutputDir, RenderManifest.Mp4NameOf(row.Index)))) fileBackBad++;
        Check("④ 再撤销一次：文件仍在旧名、没有搬乱", fileBackBad == 0, $"缺 {fileBackBad} 个（首次撤销 {rm1}）");

        var map = RenderManifest.Load(env.OutputDir);
        int stale = 0, missing = 0;
        foreach (var kv in map)
        {
            if (!File.Exists(Path.Combine(env.OutputDir, kv.Value.File))) missing++;
            var row = env.Committed.FirstOrDefault(r => r.Id == kv.Key);
            if (row == null) continue;
            if (!string.Equals(kv.Value.File, RenderManifest.Mp4NameOf(row.Index), StringComparison.OrdinalIgnoreCase)) stale++;
        }
        Check("④ 再撤销一次：清单仍自洽（每条 File 都有真文件、镜号对不上 = 0）",
            missing == 0 && stale == 0, $"缺文件 {missing} 条 / 镜号对不上 {stale} 条");

        // 观察（不作为断言 —— shot-overrides.json 的逻辑按卡面不许动）
        string ovPath = Path.Combine(env.Project.RenderStageDir, "shot-overrides.json");
        Out("   [观察] 两次撤销后 shot-overrides.json 的键 = [" +
            string.Join(",", ReadOverrideKeys(ovPath).OrderBy(k => k, StringComparer.Ordinal)) +
            "]（首次撤销后应为 [5,6]；既有实现会给已还原的键再映射一次 —— 属本卡范围外的既有问题）");
        Out("");
    }

    // ========================================================
    // 用例 ⑤：Execute 第二遍落正式名失败 → 自动回滚
    // ========================================================

    private static void RunCaseExecuteFailure(string root)
    {
        Out("══════ 用例 ⑤ 执行失败自动回滚（第二遍落正式名必失败）══════");
        var env = BuildVirtualProject(Path.Combine(root, "失败场景"), 8, mergedId: "s04");

        // 造一个"第二遍一定失败"的场景：把某个**目标名**先腾空、再占成一个目录。
        //   · 第一遍只动 From（改成 *.tmp），完全不碰这个目录 —— 不会被提前拦下；
        //   · 第二遍 File.Move(tmp, 目录名, overwrite:true) 必然 IOException。
        //   （不用 ACL 改权限、不用独占句柄，失败点确定、对系统零污染。）
        string victim = Path.Combine(env.OutputDir, RenderManifest.Mp4NameOf(4));
        string hold = Path.Combine(env.OutputDir, "sabotage-hold.mp4");
        string victimShot = env.Log.RemovedIds[0];
        File.Move(victim, hold);                    // 原 shot_004.mp4（被合并掉那镜的产物）先挪开
        Directory.CreateDirectory(victim);          // 目标名占成目录
        Out($"   人为占位：{victim}（目录）—— 它原本是 {victimShot} 的产物，已临时挪到 {Path.GetFileName(hold)}");
        Out("");

        var (ok, msg) = ReindexMigration.Execute(env.Project, env.Log);
        int realMoves = env.Log.Moves.Count(m => !m.Kind.StartsWith("park-", StringComparison.Ordinal));
        Check("⑤ Execute 返回 false", !ok, msg);
        // 失败到底发生在第几遍？Execute 的返回消息会被紧随其后的 Rollback 覆盖
        // （Rollback 把 log.Reason 改写成"已搬回 N 个文件"）—— 这是既有缺陷，本卡不改，
        // 但正好可以从 N 反推：第一遍若中途失败，N 只会小于真实搬迁数。
        Check("⑤ 第一遍（腾位置）全成，失败发生在第二遍（落正式名）",
            msg.Contains($"已搬回 {realMoves} 个文件", StringComparison.Ordinal),
            $"返回消息「{msg}」，真实搬迁 {realMoves} 条 → 第一遍 {realMoves} 条全成、第二遍失败");
        string moveErr = env.Log.Moves.FirstOrDefault(m => m.Error.Length > 0)?.Error ?? "（没有记录单条错误）";
        Out("   失败摘录（Move.Error / 返回消息）：" + moveErr + " / " + msg);
        Out("   ★ 登记不改：Execute 自动回滚后返回的消息是「已搬回 N 个」，失败原因（第几遍、哪个文件）" +
            "被 Rollback 覆盖掉了 —— 属既有缺陷，不在本卡范围（边界：只补反向 Remap）。");
        Check("⑤ 失败后已自动回滚（log 状态 rolledback）", env.Log.Status == "rolledback", "Status=" + env.Log.Status);

        // 自动回滚之后：清单还是旧镜号（正向 Remap 没来得及执行）→ 不应有一堆"过期货"
        var map = RenderManifest.Load(env.OutputDir);
        int stale = 0, missing = 0;
        foreach (var kv in map)
        {
            var row = env.Committed.FirstOrDefault(r => r.Id == kv.Key);
            if (row == null) continue;
            string expected = RenderManifest.Mp4NameOf(row.Index);
            if (!string.Equals(kv.Value.File, expected, StringComparison.OrdinalIgnoreCase)) stale++;
            // 被本次人为占位的那个目标名不算「接线错」——它是人为破坏，下面单独说明
            if (!killPathsOk(Path.Combine(env.OutputDir, kv.Value.File))) missing++;
        }
        bool killPathsOk(string p) => File.Exists(p) || p == victim;
        Check("⑤ 自动回滚后清单的 File 仍等于旧镜号（没有变成一堆过期货）", stale == 0, $"镜号对不上 {stale} 条");
        Check("⑤ 自动回滚后除去人为占位那一条，其余 File 都指向真文件", missing == 0, $"缺 {missing} 条");

        int tmpLeft = Directory.EnumerateFiles(env.OutputDir, "*.tmp", SearchOption.AllDirectories).Count();
        Check("⑤ 自动回滚后没有残留的中转临时文件", tmpLeft == 0, $"残留 {tmpLeft} 个");

        int filesBack = 0;
        foreach (var row in env.Committed)
            if (row.Id != victimShot && File.Exists(Path.Combine(env.OutputDir, RenderManifest.Mp4NameOf(row.Index)))) filesBack++;
        Check("⑤ 自动回滚后其余镜头产物都在旧名下", filesBack == env.Committed.Count - 1, $"回来 {filesBack} 个，应为 {env.Committed.Count - 1} 个");

        // 撤掉人为占位，还原现场，再验一次"清单每条 File 都真有文件"
        Directory.Delete(victim);
        File.Move(hold, victim);
        int miss2 = 0;
        foreach (var kv in map)
            if (!File.Exists(Path.Combine(env.OutputDir, kv.Value.File))) miss2++;
        Check("⑤ 撤掉人为占位后，清单每条 File 都指向真文件", miss2 == 0, $"缺 {miss2} 条");
        Out("");
    }

    // ========================================================
    // 虚拟项目
    // ========================================================

    private sealed class CaseEnv
    {
        public ProjectRef Project = null!;
        public string OutputDir = "";
        public List<ShotRow> Committed = new();
        public List<ShotRow> Draft = new();
        public ReindexLog Log = null!;
    }

    /// <summary>
    /// 造一个虚拟项目：N 镜、每个镜都有 shot_NNN.mp4、一份清单、两条 shot-overrides 备注；
    /// 再按「把 mergedId 那镜合并掉」生成草稿表，用真 API ReindexMigration.Plan 算计划。
    /// </summary>
    private static CaseEnv BuildVirtualProject(string dir, int shotCount, string mergedId)
    {
        var project = new ProjectRef { Dir = dir, Model = new ProjectModel { Name = Path.GetFileName(dir) } };
        string outputDir = project.RenderOutputDir;
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(project.RenderStageDir);
        Directory.CreateDirectory(project.ShotDir);

        var committed = new List<ShotRow>();
        for (int i = 1; i <= shotCount; i++)
        {
            committed.Add(new ShotRow
            {
                Id = $"s{i:D2}",
                Index = i,
                StartSec = (i - 1) * 3.0,
                EndSec = i * 3.0,
                Text = $"第 {i} 镜的口播",
                Description = $"第 {i} 镜的画面构思",
                Motion = $"第 {i} 镜的动效",
            });
            File.WriteAllText(Path.Combine(outputDir, RenderManifest.Mp4NameOf(i)),
                              $"虚拟产物 {i}\n", new UTF8Encoding(false));
        }

        // 清单：走真 API Record（指纹与程序自己算的一致），时间故意设在过去
        var map = new Dictionary<string, RenderManifestEntry>(StringComparer.Ordinal);
        DateTime renderedAt = DateTime.Now.AddMinutes(-10);
        foreach (var row in committed) RenderManifest.Record(map, row, RenderManifest.Mp4NameOf(row.Index), renderedAt);
        RenderManifest.Save(outputDir, map);

        // 两条单镜修改意见（键=旧镜号），照 RemapOverridesCore 认的形状写
        string ovPath = Path.Combine(project.RenderStageDir, "shot-overrides.json");
        var notes = new JsonObject
        {
            ["5"] = "第五条镜头意见：把字幕压到底部",
            ["6"] = "第六条镜头意见：结尾留 0.5 秒黑",
        };
        var ov = new JsonObject
        {
            ["updatedAt"] = DateTime.Now.AddMinutes(-8).ToString("o"),
            ["note"] = "渲染页「镜头审阅台」里用户写的单镜修改意见（按镜号）。",
            ["notes"] = notes,
        };
        File.WriteAllText(ovPath, ov.ToJsonString(), new UTF8Encoding(false));

        // 草稿表：把 mergedId 那镜合并掉，后面的镜号依次 -1。
        // ★ 必须是**新对象**：committed 那几张表代表"交接前"，拿它就地改号
        //   会把基线也一起改了（Plan 读的就是这两个表，等于自己喂错数据）。
        var draft = new List<ShotRow>();
        int idx = 0;
        foreach (var row in committed)
        {
            if (row.Id == mergedId) continue;
            draft.Add(new ShotRow
            {
                Id = row.Id,
                Index = ++idx,
                StartSec = row.StartSec,
                EndSec = row.EndSec,
                Text = row.Text,
                Description = row.Description,
                Motion = row.Motion,
            });
        }

        var log = ReindexMigration.Plan(project, outputDir, committed, draft);

        return new CaseEnv
        {
            Project = project,
            OutputDir = outputDir,
            Committed = committed,
            Draft = draft,
            Log = log,
        };
    }

    // ========================================================
    // 小工具
    // ========================================================

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Out("[过] " + name + (detail.Length > 0 ? " — " + detail : "")); }
        else { Failed.Add(name); Out("[不信] " + name + (detail.Length > 0 ? " — " + detail : "")); }
    }

    private static void Out(string line)
    {
        Console.WriteLine(line);
        Buf.AppendLine(line);
    }

    /// <summary>把清单文件原样（字节级）复制成证据 —— 不是我再序列化一遍。</summary>
    private static void CopyManifest(string outputDir, string evidenceName)
    {
        if (_evidenceDir == null) return;
        string src = RenderManifest.PathOf(outputDir);
        string dst = Path.Combine(_evidenceDir, evidenceName);
        File.Copy(src, dst, overwrite: true);
        Out($"   证据：{dst}");
    }

    private static List<string> ReadOverrideKeys(string path)
    {
        var keys = new List<string>();
        if (!File.Exists(path)) return keys;
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (doc.RootElement.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Object)
            foreach (var p in n.EnumerateObject()) keys.Add(p.Name);
        return keys;
    }
}
