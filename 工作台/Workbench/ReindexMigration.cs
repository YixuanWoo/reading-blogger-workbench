// ============================================================
// ReindexMigration.cs - 镜号重排时把磁盘上的产物一起搬过去（V1.1.0 / T-16）
// ============================================================
//
// 【它解决的是什么问题】
//
//   把一个镜头拆成两镜之后，「镜号」从拆点往后全部 +1；
//   可磁盘上那 44 个 `shot_0NN.mp4` 还待在原地 ——
//   于是第 9 位去读 shot_009.mp4，读到的是**原第 9 镜的画面**，
//   从拆点起每一个镜头都拿到前面那个镜头的画面，**而且不报错**。
//
//   这段代码就是把"内存里的重排"和"磁盘上的改名"焊成同一件事。
//
// 【为什么不干脆重渲，非要改名】
//   拆一镜之后，除了拆点那一两镜，**其余 40 多个镜头的画面内容一模一样**
//   （第 5 位装的就是原第 4 镜的画面，一帧都没变）。
//   所以根本不用重渲 —— rename 是毫秒级，重渲 52 镜是十几分钟 + 电费。
//
// 【★ 三条必须做对的细节（这才是"算法"里最容易翻车的地方）】
//
//   1. **改名绝不能自己覆盖自己** → 一律走"双缓冲"：
//        第一遍：所有要动的文件先改成临时名（谁都不占原来的位置了）
//        第二遍：临时名 → 正式名
//      这样 `shot_008 → shot_009` 永远不会盖掉还没搬的 shot_009。
//      （用"倒序搬"也能躲开，但拆 +1 / 合 -1 方向相反、混着来就容易写反；
//        双缓冲一个写法通吃，且永不出错。）
//
//   2. **被合并掉的那个镜头，产物要先"停放"再搬**。
//      把 7、8 合成一镜：第 8 个位置马上会被"原第 9 镜"的文件占用，
//      原 shot_008.mp4 如果不先挪走就会被覆盖 —— 那是用户已经渲好的东西。
//      所以先把它挪进同目录的 `_已废弃\`（不删，防手滑）。
//
//   3. **只改"驱动流程"的文件，不动"留痕"的文件**。
//      改：shot_NNN.* 各种产物、shot-overrides.json、render-tasks.json
//      不动：agent-response.json、prompt.md、apply-preview.json、render-verify.md
//            —— 它们是"当时发生了什么"的记录，改了就是篡改。
//            留痕里带上 ID 就够了：镜号后来变了，靠 ID 照样对得上。
//      （实现方式：这里用的是**文件名模板白名单**，只认 shot_NNN.xxx 这种，
//        留痕文件的名字根本不在表里，想改也改不到。）
//
// 【可回滚】
//   计划完整落盘到 stages/03-shot/reindex-log-<时间>.json，执行失败按它反向搬回。
//   日志同时也是"事后能查为什么这个文件叫这个名"的唯一依据。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteCut.Core.Contracts;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench;

/// <summary>一次文件搬迁（一个文件一条）。</summary>
public sealed class ReindexMove
{
    /// <summary>属于哪个镜头 ID（对账用）。</summary>
    public string Id { get; set; } = "";

    /// <summary>文件种类（mp4 / prev / html / body / thumb / park）。只为人看。</summary>
    public string Kind { get; set; } = "";

    public string From { get; set; } = "";
    public string To { get; set; } = "";

    /// <summary>执行时用的中转名（双缓冲第一遍落这儿）。</summary>
    public string Temp { get; set; } = "";

    /// <summary>这一步到底做没做（源文件不存在就跳过，不算失败）。</summary>
    public bool Done { get; set; }

    public string Error { get; set; } = "";
}

/// <summary>一次迁移的完整记录（也是落盘的回滚依据）。</summary>
public sealed class ReindexLog
{
    public string At { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public string OutputDir { get; set; } = "";

    /// <summary>"planned" / "done" / "failed" / "rolledback"</summary>
    public string Status { get; set; } = "planned";
    public string Reason { get; set; } = "";

    /// <summary>交接前的镜号（按 ID）。</summary>
    public Dictionary<string, int> OldIndexById { get; set; } = new();

    /// <summary>交接后的镜号（按 ID）。</summary>
    public Dictionary<string, int> NewIndexById { get; set; } = new();

    /// <summary>这次交接里被合并掉、不再存在的镜头 ID。</summary>
    public List<string> RemovedIds { get; set; } = new();

    public List<ReindexMove> Moves { get; set; } = new();

    /// <summary>人话备注（跳过了哪个目录、为什么）。</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>迁移计划与执行。</summary>
public static class ReindexMigration
{
    // ---- 文件名模板白名单 ----
    //
    // 【为什么用白名单而不是"扫全目录"】
    //   扫全目录会把历史批次（_bodies_旧219镜/、旧219镜/、thumbs_219bak/）
    //   里的同名文件一起改了 —— 那是篡改历史。
    //   白名单 = 只有这几类**驱动流程**的文件会被搬，留痕文件想改都改不到。
    private static readonly string[] NameTemplates =
    {
        "shot_{n}.mp4",
        "shot_{n}.prev.mp4",
        "shot_{n}.html",
        "shot_{n}.prev.html",
        "shot_{n}.body.html",
        "shot_{n}.prev.body.html",
        "t_{n}.jpg",
        "t_{n}.prev.jpg",
    };

    private const string KindTemplates = "mp4|prevMp4|html|prevHtml|bodyHtml|prevBodyHtml|thumb|prevThumb";

    // ========================================================
    // 目录白名单
    // ========================================================

    /// <summary>
    /// 会被搬的目录（全部是"这一步正在用的"目录，历史批次一个不碰）。
    /// 每个目录**只扫一层**，不递归 —— 递归就会掉进 旧219镜/ 那种坑。
    /// </summary>
    private static List<string> DirsOf(ProjectRef project, string outputDir, List<string> notes)
    {
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            if (!Directory.Exists(d)) return;
            string full;
            try { full = Path.GetFullPath(d); } catch { return; }
            if (!dirs.Contains(full, StringComparer.OrdinalIgnoreCase)) dirs.Add(full);
        }

        // ① 渲染产物（shot_NNN.mp4 就在这儿）
        Add(outputDir);
        Add(RenderSpec.RenderOutputDirOf(project.Dir));

        // ② 渲染工作区的实现（工作台生成的底稿 / HyperFrames 的真实实现）
        string work = Path.Combine(project.Dir, "渲染工作区");
        string blocks = Path.Combine(work, "shots-blocks");
        Add(work);
        Add(Path.Combine(work, "bodies"));
        Add(blocks);

        // ③ 旧的 shots/ 目录：**只在没有 shots-blocks 的时候才动它**
        //    （块级成片之后 shots/ 里躺的是上一批 219 镜的同名文件，
        //      再改名就是篡改历史；而 shots-blocks 才是现在真正在用的那份）
        bool blocksIsLive = Directory.Exists(blocks) &&
                            Directory.EnumerateFiles(blocks, "shot_*.html").Any();
        if (blocksIsLive)
            notes.Add("跳过 渲染工作区\\shots\\ —— 那里面是上一批的旧实现（当前主力是 shots-blocks\\）");
        else
            Add(Path.Combine(work, "shots"));

        // ④ 缩略图
        Add(Path.Combine(project.RenderStageDir, "thumbs"));

        return dirs;
    }

    // ========================================================
    // 算计划
    // ========================================================

    /// <summary>
    /// 算出这次交接要搬哪些文件。
    /// committed = 现在的已交接版；draft = 即将交接的草稿。两边靠 **ID** 对账。
    /// </summary>
    public static ReindexLog Plan(ProjectRef project, string outputDir,
                                  IReadOnlyList<ShotRow> committed, IReadOnlyList<ShotRow> draft)
    {
        var log = new ReindexLog
        {
            At = DateTime.Now.ToString("o"),
            ProjectDir = project.Dir,
            OutputDir = outputDir,
        };

        foreach (var r in committed) log.OldIndexById[r.Id] = r.Index;
        foreach (var r in draft) log.NewIndexById[r.Id] = r.Index;

        // ① 被合并掉 / 被删掉的镜头 → 产物先"停放"，免得第 8 位被后来者覆盖
        foreach (var r in committed)
        {
            if (log.NewIndexById.ContainsKey(r.Id)) continue;
            log.RemovedIds.Add(r.Id);
        }

        var dirs = DirsOf(project, outputDir, log.Notes);

        // ② 停放的产物
        foreach (var id in log.RemovedIds)
        {
            if (!log.OldIndexById.TryGetValue(id, out int oldIdx)) continue;
            foreach (var d in dirs)
                foreach (var (tpl, kind) in Templates())
                {
                    string from = Path.Combine(d, tpl.Replace("{n}", oldIdx.ToString("D3")));
                    if (!File.Exists(from)) continue;
                    string parkDir = ParkDirOf(d);
                    string to = Path.Combine(parkDir, Path.GetFileName(from));
                    log.Moves.Add(new ReindexMove
                    {
                        Id = id, Kind = "park-" + kind, From = from, To = UniqueName(to),
                    });
                }
        }

        // ③ 还在的镜头：镜号变了就搬
        int changed = 0;
        foreach (var kv in log.NewIndexById)
        {
            if (!log.OldIndexById.TryGetValue(kv.Key, out int oldIdx)) continue;   // 新镜，没有产物
            int newIdx = kv.Value;
            if (oldIdx == newIdx) continue;
            changed++;

            foreach (var d in dirs)
                foreach (var (tpl, kind) in Templates())
                {
                    string from = Path.Combine(d, tpl.Replace("{n}", oldIdx.ToString("D3")));
                    if (!File.Exists(from)) continue;
                    string to = Path.Combine(d, tpl.Replace("{n}", newIdx.ToString("D3")));
                    log.Moves.Add(new ReindexMove
                    {
                        Id = kv.Key, Kind = kind, From = from, To = to,
                    });
                }
        }

        log.Notes.Insert(0, $"共 {log.Moves.Count} 个文件要搬（{changed} 个镜头的镜号变了，" +
                            $"{log.RemovedIds.Count} 个镜头被合并掉）");
        if (log.Moves.Count == 0) log.Notes.Add("没有任何产物需要搬 —— 项目可能还没渲染过");
        return log;
    }

    private static IEnumerable<(string tpl, string kind)> Templates()
    {
        var kinds = KindTemplates.Split('|');
        for (int i = 0; i < NameTemplates.Length; i++) yield return (NameTemplates[i], kinds[i]);
    }

    /// <summary>停放目录（同层的一个 _已废弃\，不删、防手滑）。</summary>
    private static string ParkDirOf(string dir) => Path.Combine(dir, "_已废弃");

    /// <summary>目标名已被占用就加时间戳后缀（停放目录里同名多次时会遇到）。</summary>
    private static string UniqueName(string path)
    {
        if (!File.Exists(path)) return path;
        string d = Path.GetDirectoryName(path)!;
        string n = Path.GetFileNameWithoutExtension(path);
        string e = Path.GetExtension(path);
        for (int i = 1; i < 1000; i++)
        {
            string t = Path.Combine(d, $"{n}.废弃{i}{e}");
            if (!File.Exists(t)) return t;
        }
        return path;
    }

    // ========================================================
    // 执行
    // ========================================================

    /// <summary>
    /// 执行搬迁。返回 (是否全成功, 人话说明)。
    /// 任何一步失败 → **自动按日志反向回滚**，尽量恢复原状。
    /// </summary>
    public static (bool ok, string msg) Execute(ProjectRef project, ReindexLog log)
    {
        // ★ 双缓冲：第一遍全部改成临时名，第二遍再改成正式名。
        //   这样"shot_008 → shot_009"绝不会盖掉还没搬的 shot_009。
        string token = Guid.NewGuid().ToString("N")[..8];

        foreach (var m in log.Moves)
        {
            try
            {
                if (!File.Exists(m.From)) continue;      // 源文件不在 = 本来就没产物，跳过
                Directory.CreateDirectory(Path.GetDirectoryName(m.To)!);
                m.Temp = m.From + ".reindex-" + token + ".tmp";
                File.Move(m.From, m.Temp);
                m.Done = true;
            }
            catch (Exception ex)
            {
                m.Error = ex.Message;
                log.Status = "failed";
                log.Reason = $"第一遍（腾位置）失败：{ex.Message}";
                Rollback(project, log);
                return (false, log.Reason);
            }
        }

        foreach (var m in log.Moves)
        {
            if (!m.Done) continue;
            try
            {
                File.Move(m.Temp, m.To, overwrite: true);
                m.Temp = "";
            }
            catch (Exception ex)
            {
                m.Error = ex.Message;
                log.Status = "failed";
                log.Reason = $"第二遍（落正式名）失败：{ex.Message}";
                Rollback(project, log);
                return (false, log.Reason);
            }
        }

        // ---- 驱动器之外的收尾：两份"账本"跟着改名 ----
        try { RemapOverrides(project, log); }
        catch (Exception ex) { log.Notes.Add("shot-overrides.json 改名失败（不影响出片）：" + ex.Message); }

        try
        {
            var map = RenderManifest.Load(log.OutputDir);
            if (map.Count > 0)
            {
                RenderManifest.Remap(map, log.NewIndexById, log.RemovedIds);
                RenderManifest.Save(log.OutputDir, map);
            }
        }
        catch (Exception ex) { log.Notes.Add("渲染清单改名失败（不影响出片）：" + ex.Message); }

        log.Status = "done";
        log.Reason = $"搬了 {log.Moves.Count(m => m.Done)} 个文件";
        WriteLog(project, log);
        return (true, log.Reason);
    }

    /// <summary>
    /// 反向搬回去（执行失败自动调，或用户点「撤销这次拆合」时调）。
    /// 同样是双缓冲。
    /// </summary>
    public static (bool ok, string msg) Rollback(ProjectRef project, ReindexLog log)
    {
        string token = Guid.NewGuid().ToString("N")[..8];
        int back = 0;

        // 只有"已经搬到 To"的才需要回滚（第一遍失败时文件还躺在 Temp 里）
        foreach (var m in log.Moves.Where(x => x.Done))
        {
            string cur = File.Exists(m.To) ? m.To : (File.Exists(m.Temp) ? m.Temp : "");
            if (cur.Length == 0) continue;
            try
            {
                string t = m.From + ".rollback-" + token + ".tmp";
                File.Move(cur, t);
                m.Temp = t;
            }
            catch (Exception ex) { m.Error = "回滚第一遍：" + ex.Message; }
        }

        foreach (var m in log.Moves.Where(x => x.Done && x.Temp.Length > 0))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(m.From)!);
                File.Move(m.Temp, m.From, overwrite: true);
                m.Temp = "";
                m.Done = false;
                back++;
            }
            catch (Exception ex) { m.Error = "回滚第二遍：" + ex.Message; }
        }

        try { RemapOverridesReverse(project, log); } catch { }

        log.Status = "rolledback";
        log.Reason = $"已搬回 {back} 个文件";
        WriteLog(project, log);
        return (true, log.Reason);
    }

    // ========================================================
    // shot-overrides.json 的备注键（按镜号存的）也要跟着改
    // ========================================================

    private static void RemapOverrides(ProjectRef project, ReindexLog log) =>
        RemapOverridesCore(Path.Combine(project.RenderStageDir, "shot-overrides.json"),
                           log.OldIndexById, log.NewIndexById);

    private static void RemapOverridesReverse(ProjectRef project, ReindexLog log) =>
        RemapOverridesCore(Path.Combine(project.RenderStageDir, "shot-overrides.json"),
                           log.NewIndexById, log.OldIndexById);

    /// <summary>
    /// 把 notes 字典的键从"旧镜号"换成"新镜号"。
    ///
    /// 【为什么这份也要改，而 agent-response.json 不用改】
    ///   它是**驱动流程**的：单镜重渲时按镜号把"这一镜要改什么"交给 Agent。
    ///   键不跟着改，那句修改意见就会挂到隔壁镜头上去。
    ///   而 agent-response.json / prompt.md 是"当时 Agent 返回了什么"的留痕，改了就是篡改。
    /// </summary>
    private static void RemapOverridesCore(string path, Dictionary<string, int> fromIdx, Dictionary<string, int> toIdx)
    {
        if (!File.Exists(path)) return;

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = doc.RootElement;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in fromIdx)
            if (toIdx.TryGetValue(kv.Key, out int n)) map[kv.Value.ToString()] = n.ToString();

        var notes = new System.Text.Json.Nodes.JsonObject();
        if (root.TryGetProperty("notes", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in nEl.EnumerateObject())
                notes[map.TryGetValue(p.Name, out var to) ? to : p.Name] = p.Value.GetString() ?? "";

        var outObj = new System.Text.Json.Nodes.JsonObject
        {
            ["updatedAt"] = DateTime.Now.ToString("o"),
            ["note"] = "渲染页「镜头审阅台」里用户写的单镜修改意见（按镜号）。不是分镜设计的一部分，是「这一镜要改什么」的指令，重渲时原样交给 Agent。",
            ["notes"] = notes,
        };
        // ★ 无 BOM（跟 Step3RenderView 里同一份文件的写法保持一致）。
        File.WriteAllText(path, outObj.ToJsonString(ContractJson.Options), ContractJson.Utf8NoBom);
    }

    // ========================================================
    // 日志
    // ========================================================

    public static string LogPathOf(ProjectRef project, ReindexLog log)
    {
        string stamp;
        try { stamp = DateTime.Parse(log.At).ToString("yyyyMMdd-HHmmss"); }
        catch { stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss"); }
        return Path.Combine(project.ShotDir, $"reindex-log-{stamp}.json");
    }

    private static void WriteLog(ProjectRef project, ReindexLog log)
    {
        try
        {
            Directory.CreateDirectory(project.ShotDir);
            ContractJson.WriteFile(LogPathOf(project, log), log);
        }
        catch { }
    }

    /// <summary>从磁盘读回一份迁移日志（撤销时用）。</summary>
    public static ReindexLog? LoadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return ContractJson.Deserialize<ReindexLog>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch { return null; }
    }

    /// <summary>
    /// 人话描述这次交接的代价（框 B 里那几行字）。
    /// </summary>
    public static string Describe(ReindexLog log)
    {
        int moved = log.Moves.Count(m => !m.Kind.StartsWith("park-", StringComparison.Ordinal));
        int parked = log.Moves.Count - moved;
        var parts = new List<string>();
        if (log.NewIndexById.Count != log.OldIndexById.Count)
            parts.Add($"镜头表 {log.OldIndexById.Count} 镜 → {log.NewIndexById.Count} 镜");
        if (moved > 0) parts.Add($"改名 {moved} 个渲染产物/实现文件");
        if (parked > 0) parts.Add($"停放 {parked} 个被合并镜头的产物到 _已废弃\\");
        if (parts.Count == 0) parts.Add("没有产物需要搬");
        return string.Join("，", parts);
    }
}
