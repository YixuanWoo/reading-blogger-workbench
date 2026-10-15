// ============================================================
// ArchiveService.cs - ★ V1.13.0「归档」：整包搬去移动硬盘，本地只留小桩
// ============================================================
//
// 【改造前是什么样】
//   原来的「归档」只改 project.json 里一个状态字段，一个文件都不动
//   （ProjectListView 注释原文："归档不会动任何文件，只是把它从「进行中」挪开"）。
//   结果是：项目越做越多，本地硬盘越来越满，"归档"两个字名不副实。
//
// 【现在是什么样】
//   点归档 = 真的把整个项目搬到移动硬盘，顺便把本地收拾干净：
//     · 整包搬到 <归档根>\<项目名>\（几乎整包 —— 见下面的分类规则）
//     · 本地只留小桩：project.json、script.md、stages 文本、封面、复盘数据
//     · "分镜渲染出来的画面"（逐镜 mp4 / 卡片图）本地和硬盘都不留
//   项目在工作台里**照旧能被读到**，只是读不到原视频、成片这些重件。
//
// 【三条不可动摇的顺序铁律】
//   ① 先复制 → ② 逐个校验指纹 → ③ 全部通过才删本地。
//   任何一步失败、或者硬盘中途掉了 → 立刻中止，**一个本地文件都不删**。
//   这跟"先删了再拷"的差别是：后者硬盘一掉线，素材就永久没了。
//
// 【怎么认盘（为什么不认盘符）】
//   移动硬盘的盘符会漂（E→F→G），照着老路径搬有"搬到别的盘上"的风险。
//   所以记**卷序列号**（分区格式化时写死，跟盘符无关），
//   盘换了、或者插的不是那块盘 → 拒绝执行，让用户自己确认。
//
// 【指纹算法跟文件总库台账保持一致】
//   H2M_ = MD5( 文件大小 + 文件头最多 2MB )，见 D:\文件总库\_工具\台账\ledger_lib.py。
//   用同一套算法，归档校验、台账入库、将来查重，三处说的是同一种"身份证"。
//
// 【★ V1.13.1：进度可见 + 可安全中止】
//   几百 MB 的搬运要跑好几秒到几十秒。原本进度只打在窗口底部状态栏
//   （字号小、位置偏，用户根本看不到），而且这期间界面完全没锁 ——
//   手快多点两下就可能撞上正在搬的项目文件。
//   现在改成：Execute / Restore 通过 <see cref="ArchiveProgress"/> 报告
//   「第几步 / 共几步 + 这一步干了多少 / 共多少 + 总百分比」，
//   界面弹一个**模态**进度窗（模态 = 主窗口所有按钮自动失效，锁死）。
//   同时支持 CancellationToken 安全中止：
//     取消点只在「复制」「校验」两个阶段 —— 那时本地一个文件都没删，
//     停下来最多是硬盘上留半份拷贝（下次归档会覆盖，无害）。
//     一旦进入「写留痕」「清理本地」，就不再响应取消（那几步必须做完）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench.Project;

/// <summary>归档 / 取回 的执行结果（给界面显示人话用）。</summary>
public sealed class ArchiveResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public int FileCount { get; init; }
    public long Bytes { get; init; }
    public string TargetDir { get; init; } = "";

    /// <summary>是不是用户自己点了「安全中止」（区别于出错）。</summary>
    public bool Cancelled { get; init; }
}

/// <summary>
/// 一次归档 / 取回的实时进度快照（进度窗拿它刷新界面）。
/// 每 Invoke 一次 = 报告一个新状态。字段全 <c>init</c>，跨线程只读，安全。
/// </summary>
public sealed class ArchiveProgress
{
    /// <summary>这一步在干什么的人话名（如「复制到硬盘」「校验落盘」）。</summary>
    public string Stage { get; init; } = "";

    /// <summary>第几步（1 起算）与一共几步。用于「第 2/4 步」。</summary>
    public int StageIndex { get; init; }
    public int StageCount { get; init; }

    /// <summary>当前这一步内的计数，用于「12 / 205」。没有细粒度时留 0。</summary>
    public int Done { get; init; }
    public int Total { get; init; }

    /// <summary>当前正在处理的文件（相对路径，太长会被界面截断）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// 总进度 0..100。**已经带阶段权重**（复制占大头、校验次之、收尾很快），
    /// 所以进度条直接用这个值就对了，不用自己再折算。
    /// </summary>
    public double Percent { get; init; }

    /// <summary>一行大字：「第 2/4 步 · 校验落盘」。</summary>
    public string Headline => StageCount > 0 ? $"第 {StageIndex}/{StageCount} 步 · {Stage}" : Stage;

    /// <summary>一行小字：「12 / 205　渲染产物\shots\shot_012.mp4」。</summary>
    public string Sub => Total > 0 ? $"{Done} / {Total}　{Detail}" : Detail;
}

/// <summary>一块磁盘的身份（卷序列号 + 卷标）。</summary>
public sealed class VolumeInfo
{
    public string Root { get; init; } = "";
    public bool Exists { get; init; }
    public string Serial { get; init; } = "";
    public string Label { get; init; } = "";
    public long FreeBytes { get; init; }

    /// <summary>人话描述，如 "E:\ (Elements, 序列号 A1B2C3D4, 可用 923 GB)"。</summary>
    public string Describe()
    {
        if (!Exists) return $"{Root}（没找到）";
        string lbl = string.IsNullOrWhiteSpace(Label) ? "无卷标" : Label;
        return $"{Root}（{lbl}，序列号 {Serial}，可用 {FreeBytes / 1073741824.0:F1} GB）";
    }
}

/// <summary>归档计划里的一条文件。</summary>
public sealed class ArchiveEntry
{
    public string Rel { get; init; } = "";      // 相对项目夹的路径，如 "渲染产物\shot_001.mp4"
    public long Size { get; init; }
    public bool ToDisk { get; init; }           // 要不要搬到移动硬盘
    public bool KeepLocal { get; init; }        // 本地保不保留
    public string Why { get; init; } = "";      // 人话：为什么这么处理
}

/// <summary>
/// 归档计划：由 <see cref="ArchiveService.Prepare"/> 算出来，先给用户看一眼再执行。
/// 它同时是"二次确认清单"的数据源 —— 清单上的每个数字都来自这里。
/// </summary>
public sealed class ArchivePlan
{
    public bool Ok { get; set; }
    public string Reason { get; set; } = "";
    public string TargetDir { get; set; } = "";

    public VolumeInfo Disk { get; set; } = new();
    public bool DiskFirstUse { get; set; }

    /// <summary>成片（相对路径）。归档门槛就是"至少有一个"。</summary>
    public List<string> Films { get; set; } = new();

    public List<ArchiveEntry> Entries { get; set; } = new();

    public int MoveCount => Entries.Count(e => e.ToDisk);
    public long MoveBytes => Entries.Where(e => e.ToDisk).Sum(e => e.Size);
    public int DeleteCount => Entries.Count(e => !e.KeepLocal);
    public long DeleteBytes => Entries.Where(e => !e.KeepLocal).Sum(e => e.Size);
    public int KeepCount => Entries.Count(e => e.KeepLocal);
    public long KeepBytes => Entries.Where(e => e.KeepLocal).Sum(e => e.Size);

    public static string Human(long bytes)
    {
        if (bytes >= 1073741824) return $"{bytes / 1073741824.0:F2} GB";
        if (bytes >= 1048576) return $"{bytes / 1048576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }
}

/// <summary>归档清单（落盘留痕，给人看也给机器看）。</summary>
public sealed class ArchiveManifest
{
    public string 项目名 { get; set; } = "";
    public string 项目Id { get; set; } = "";
    public string 归档时间 { get; set; } = "";
    public string 源目录 { get; set; } = "";
    public string 目标目录 { get; set; } = "";
    public string 卷序列号 { get; set; } = "";
    public string 卷标 { get; set; } = "";
    public int 搬到硬盘文件数 { get; set; }
    public long 搬到硬盘字节 { get; set; }
    public int 本地删除文件数 { get; set; }
    public long 本地删除字节 { get; set; }
    public int 本地保留文件数 { get; set; }
    public long 本地保留字节 { get; set; }
    public List<string> 成片 { get; set; } = new();
    public List<ManifestFile> 文件 { get; set; } = new();
}

public sealed class ManifestFile
{
    public string 路径 { get; set; } = "";
    public long 大小 { get; set; }
    public string 指纹 { get; set; } = "";
    public string 处理 { get; set; } = "";   // 搬走 / 删除 / 保留
}

/// <summary>归档 / 取回 的全部实现。界面只调这里，不自己碰文件。</summary>
public static class ArchiveService
{
    // ========================================================
    // 分类规则（★ 想改"什么留下、什么搬走"，只改这一段）
    // ========================================================
    //
    // 【一句话判据（用户原话）】
    //   "几乎整包搬运，就除了分镜渲染好的镜头不要。"
    //   "本地不留任何渲染出来的视频；成片要留；每个镜头渲染时的代码、封面、脚本要留。"
    //
    // 【怎么落成规则】
    //   ① 分镜渲染出来的画面（**渲染产物里的视频** + **渲染工作区\shots\ 下拼出来的逐镜片段**）
    //                                                          → 硬盘不留、本地也不留
    //   ② 卡片图（渲染产物下任意 images\ 目录，含历史批次的 <批次>\images\）→ 硬盘不留、本地也不留
    //   ③ 渲染器事务残留（.shot_xxx.hf-transaction-* 目录）      → 硬盘不留、本地也不留
    //   ④ 封面图（文件名带"封面"）                              → 强制留下（这是用户点名要留的）
    //   ⑤ 历史备份（路径里任一段以 _备份 / _backup 开头）        → 搬去硬盘，本地不留
    //   ⑥ 本地"小桩" = 非视频 + 不在上面那些目录 + 小于 2MB
    //        命中它的：project.json、script.md、stages 里的 json/md、
    //                  分镜的 .html/.py 代码、公共素材背景图、封面、review\
    //   ⑦ 其余（成片、插片原声、原始音频、wav 中间件、渲染工作区）→ 搬去硬盘，本地不留
    //
    //   ★ 判据宁可保守：**只有"明显是生成出来的逐镜片段"才当可再生的丢掉**。
    //     比如 渲染工作区\bodies\ 是镜头的真源（HTML，要留），
    //     万一里面混了一段素材视频，那属于不可再生 —— 必须搬走，
    //     不能因为"它在渲染工作区里"就删掉。
    //
    // 【为什么用"小于 2MB"这种看起来粗暴的判据】
    //   写白名单要逐个目录枚举，项目结构一改就漏；而"小文件留、大件搬"
    //   一句话能说清、也不会随项目结构变化而失效。实测效果：
    //   思辨与立场 456MB → 本地桩约 2MB。规则简单，结果也对。

    /// <summary>本地"小文件"的门槛：小于它才算值得留在本地的桩。</summary>
    private const long SmallFileLimit = 2 * 1024 * 1024;

    /// <summary>指纹只读文件头这么多字节（与文件总库台账一致）。</summary>
    private const int FingerprintHeadBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".flv", ".webm", ".mpg", ".mpeg", ".ts",
    };

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
    };

    // ========================================================
    // 认盘
    // ========================================================

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer, int nFileSystemNameSize);

    /// <summary>探一块盘：在不在、卷序列号是多少、还剩多少空间。</summary>
    public static VolumeInfo Probe(string dirOrRoot)
    {
        string root = "";
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(dirOrRoot)) ?? "";
            if (root.Length == 0) return new VolumeInfo();

            var di = new DriveInfo(root);
            if (!di.IsReady) return new VolumeInfo { Root = root, Exists = false };

            var label = new StringBuilder(261);
            bool ok = GetVolumeInformation(root, label, label.Capacity,
                                           out uint serial, out _, out _, null, 0);

            return new VolumeInfo
            {
                Root = root,
                Exists = true,
                Serial = ok ? serial.ToString("X8") : "",
                Label = ok ? label.ToString() : "",
                FreeBytes = di.AvailableFreeSpace,
            };
        }
        catch
        {
            // 盘不存在 / 没插 / 没权限 —— 一律当"不在"，由上层给人话提示
            return new VolumeInfo { Root = root, Exists = false };
        }
    }

    // ========================================================
    // 指纹（与文件总库台账同算法：H2M_MD5(大小 + 头 2MB)）
    // ========================================================

    public static string Fingerprint(string path)
    {
        try
        {
            long size = new FileInfo(path).Length;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            hash.AppendData(Encoding.ASCII.GetBytes(size.ToString()));

            using var fs = File.OpenRead(path);
            byte[] buf = new byte[1024 * 1024];
            int got = 0;
            while (got < FingerprintHeadBytes)
            {
                int want = Math.Min(buf.Length, FingerprintHeadBytes - got);
                int n = fs.Read(buf, 0, want);
                if (n <= 0) break;
                hash.AppendData(buf, 0, n);
                got += n;
            }

            return "H2M_" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    // ========================================================
    // ① 算计划（不碰任何文件，只读）
    // ========================================================

    /// <summary>
    /// 算出"点归档会发生什么"。**纯只读**，失败时 <see cref="ArchivePlan.Reason"/> 里是人话原因。
    /// 界面先拿它弹二次确认清单，用户点了才走 <see cref="Execute"/>。
    /// </summary>
    public static ArchivePlan Prepare(ProjectRef r)
    {
        var plan = new ArchivePlan();
        var cfg = WorkbenchWindow.State.Config;

        // ---- 门槛一：至少要有成片 ----
        plan.Films = FindFilms(r);
        if (plan.Films.Count == 0)
        {
            plan.Reason = "这个项目还没有成片（stages\\05-assemble 或 export 里没有视频）。\n\n" +
                          "归档是不可逆的（本地大件会被搬走删掉），所以至少走到出片这一步才允许归档。\n" +
                          "先把它做完再来。";
            return plan;
        }

        // ---- 门槛二：落点配了没 ----
        string root = cfg.ArchiveRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            plan.Reason = "还没设置归档落点（移动硬盘上的哪个目录）。";
            return plan;
        }

        // ---- 门槛三：盘在不在 / 是不是那块盘 ----
        var disk = Probe(root);
        if (!disk.Exists)
        {
            plan.Reason = $"移动硬盘没插上 —— 找不到 {root}。\n\n" +
                          "把硬盘插好再点归档。（不会排队等，因为「以为归档了其实没有」更危险。）";
            return plan;
        }

        bool firstUse = string.IsNullOrWhiteSpace(cfg.ArchiveVolumeSerial);
        plan.DiskFirstUse = firstUse;
        plan.Disk = disk;

        if (!firstUse && !string.Equals(disk.Serial, cfg.ArchiveVolumeSerial, StringComparison.OrdinalIgnoreCase))
        {
            plan.Reason = "现在插着的不是上次那块归档盘。\n\n" +
                          $"上次那块：序列号 {cfg.ArchiveVolumeSerial}\n" +
                          $"现在这块：序列号 {disk.Serial}（{disk.Root}）\n\n" +
                          "为避免把工程搬到错误的盘上，这里先停住。确认是换盘了再手动改设置。";
            return plan;
        }

        // ---- 扫描 + 分类 ----
        plan.TargetDir = Path.Combine(root.TrimEnd('\\', '/'), ProjectStore.SanitizeFolderName(r.Model.Name));
        try
        {
            plan.Entries = ScanAndClassify(r);
        }
        catch (Exception ex)
        {
            plan.Reason = "读项目文件时出错了（可能有文件被占用或读不动）：\n\n" + ex.Message;
            return plan;
        }

        // ---- 门槛四：空间够不够 ----
        long need = plan.MoveBytes;
        if (disk.FreeBytes > 0 && disk.FreeBytes < need + (100L * 1024 * 1024))   // 留 100MB 余量
        {
            plan.Reason = $"移动硬盘空间不够。\n\n需要 {ArchivePlan.Human(need)}，盘上只剩 {ArchivePlan.Human(disk.FreeBytes)}。";
            return plan;
        }

        plan.Ok = true;
        return plan;
    }

    /// <summary>找成片：stages\05-assemble 和 export 里的视频文件。</summary>
    private static List<string> FindFilms(ProjectRef r)
    {
        var list = new List<string>();
        foreach (string sub in new[] { r.AssembleDir, r.ExportDir })
        {
            try
            {
                if (!Directory.Exists(sub)) continue;
                foreach (string f in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    if (VideoExts.Contains(Path.GetExtension(f)))
                        list.Add(Path.GetRelativePath(r.Dir, f));
            }
            catch { /* 目录读不了就当没有 */ }
        }
        return list;
    }

    /// <summary>遍历项目夹，按上面的分类规则给每个文件定性。</summary>
    private static List<ArchiveEntry> ScanAndClassify(ProjectRef r)
    {
        var list = new List<ArchiveEntry>();

        foreach (string full in Directory.EnumerateFiles(r.Dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(r.Dir, full);
            string relSlash = rel.Replace('/', '\\');
            string ext = Path.GetExtension(full);
            string name = Path.GetFileName(full);

            long size;
            try { size = new FileInfo(full).Length; }
            catch { continue; }

            bool isVideo = VideoExts.Contains(ext);
            bool isImage = ImageExts.Contains(ext);

            bool inRenderOut = relSlash.StartsWith("渲染产物\\", StringComparison.OrdinalIgnoreCase);
            bool inRenderWork = relSlash.StartsWith("渲染工作区\\", StringComparison.OrdinalIgnoreCase);

            // ★ 卡片图：判据不能写成"渲染产物\images\"这一条死路径 ——
            //   实测项目里还存在"渲染产物\<某历史批次>\images\"（如 旧219镜\images\），
            //   只认死路径会把它们当普通小文件留在本地。
            //   正确判据 = 在渲染产物下 + 路径中有一段叫 images 的目录。
            bool inCardImages = inRenderOut &&
                                relSlash.Split('\\').Contains("images", StringComparer.OrdinalIgnoreCase);

            // ★ 历史备份目录：判据不能写成"_备份\"一条死前缀 ——
            //   实测项目里的备份夹叫"_backup_219句"（英文 + 带后缀），同样得认。
            //   正确判据 = 路径里任意一段以"_备份"或"_backup"开头。
            bool inBackup = IsBackupDir(relSlash);

            bool inReview = relSlash.StartsWith("review\\", StringComparison.OrdinalIgnoreCase);
            bool inArchiveNote = relSlash.StartsWith("_归档\\", StringComparison.OrdinalIgnoreCase);

            // ★ 渲染器（HyperFrames）的事务残留目录：.shot_xxx.hf-transaction-yyy，
            //   里面通常只有一个 owner.json。渲染正常结束时会自己清掉，残留下来就是纯垃圾。
            bool isRenderTemp = relSlash.Contains(".hf-transaction-", StringComparison.OrdinalIgnoreCase);

            // ★ "分镜渲染出来的画面"的**精确**定义：渲染产物里的所有视频 + 渲染工作区 shots\ 下拼出来的逐镜片段。
            //
            // 【为什么渲染工作区只认 shots\，不认整个目录】
            //   渲染工作区的主体（bodies\）是镜头的**真源**，是 HTML（要留）；
            //   但万一有人在里面放了段素材视频，那也属于"不可再生"，得搬走。
            //   一刀切按"渲染工作区里的视频"排除 = 把不可再生的东西当可再生的删了。
            //   保守一点：只有明显是"生成出来的逐镜片段"才排除。
            bool inShotClips = inRenderOut ||
                               relSlash.StartsWith("渲染工作区\\shots\\", StringComparison.OrdinalIgnoreCase);

            // 封面：用户点名要留在本地的"封面小图"
            bool isCover = isImage && name.Contains("封面", StringComparison.Ordinal);

            // ---- 硬盘那份：几乎整包，只刨掉"分镜渲染出来的画面" ----
            bool toDisk = !(inShotClips && isVideo) && !inCardImages && !isRenderTemp;

            // ---- 本地那份：只留小桩 ----
            bool keepLocal;
            string why;

            if (isRenderTemp)
            {
                keepLocal = false;
                why = "渲染器临时残留（.hf-transaction-*）：两边都不留";
            }
            else if (inCardImages)
            {
                keepLocal = false;
                why = "卡片图（渲染产物\\images）：渲染得出来，本地和硬盘都不留";
            }
            else if (inShotClips && isVideo)
            {
                keepLocal = false;
                why = "分镜渲染出来的画面：本地和硬盘都不留";
            }
            else if (inBackup)
            {
                keepLocal = false;
                why = "历史备份：搬去硬盘，本地不留";
            }
            else if (inReview || inArchiveNote)
            {
                keepLocal = true;
                why = "工作台的数据/留痕：始终留本地";
            }
            else if (isCover)
            {
                keepLocal = true;
                why = "封面小图：按约定留本地";
            }
            else if (isVideo)
            {
                keepLocal = false;
                why = "视频（成片/原声/素材）：搬去硬盘，本地不留任何渲染出来的视频";
            }
            else if (inRenderWork)
            {
                keepLocal = false;
                why = "渲染工作区（中间产物）：搬去硬盘，本地不留";
            }
            else if (size >= SmallFileLimit)
            {
                keepLocal = false;
                why = $"大文件（{ArchivePlan.Human(size)}）：搬去硬盘，本地不留";
            }
            else
            {
                keepLocal = true;
                why = "小文件（代码/文本/小图）：是本地桩的一部分";
            }

            list.Add(new ArchiveEntry
            {
                Rel = relSlash,
                Size = size,
                ToDisk = toDisk,
                KeepLocal = keepLocal,
                Why = why,
            });
        }

        return list;
    }

    /// <summary>
    /// 判断相对路径是否落在"历史备份"目录里。
    /// 认法：路径的任意一段以 <c>_备份</c> 或 <c>_backup</c> 开头（不区分大小写）。
    /// 这样 <c>_备份\</c>、<c>_backup_219句\</c>、<c>渲染产物\_backup\</c> 都能被认出来。
    /// </summary>
    private static bool IsBackupDir(string relSlash)
    {
        foreach (string seg in relSlash.Split('\\'))
        {
            if (seg.StartsWith("_备份", StringComparison.OrdinalIgnoreCase) ||
                seg.StartsWith("_backup", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ========================================================
    // ② 执行归档（复制 → 校验 → 才删）
    // ========================================================

    /// <summary>
    /// 真归档。**必须在后台线程调**（几百 MB 的复制 + 校验会卡住界面）。
    /// <para>
    /// progress 由**后台线程**调用，收的是结构化进度快照；调用方自己负责
    /// Marshal 回 UI 线程（<see cref="Ui.ProgressDialog"/> 已经代劳了）。
    /// </para>
    /// <para>
    /// ct 用来「安全中止」：**只在复制 / 校验两个阶段生效**，那时本地还一个
    /// 文件都没删，停下最多是硬盘上留半份拷贝。一旦进到「写留痕」「清理本地」
    /// 就不再理会取消 —— 删到一半停下才是最糟的结果。
    /// </para>
    /// </summary>
    public static ArchiveResult Execute(ProjectRef r, ArchivePlan plan,
                                        Action<ArchiveProgress>? progress = null,
                                        CancellationToken ct = default)
    {
        if (!plan.Ok) return new ArchiveResult { Ok = false, Message = plan.Reason };

        string target = plan.TargetDir;

        // 阶段权重：复制 0→72、校验 72→96、写留痕 96→97、清理本地 97→100。
        // 为什么这么分：复制最耗时、逐个校验次之，后面两步只是收尾。
        const double P1 = 72, P2 = 96, P3 = 97, P4 = 100;
        const int SC = 4;

        try
        {
            // ---- ① 建目标目录 ----
            Report(progress, "复制到硬盘", 1, SC, 0, 0, "正在建立目标目录…", 0);
            Directory.CreateDirectory(target);

            var toDisk = plan.Entries.Where(e => e.ToDisk).ToList();
            int total = Math.Max(1, toDisk.Count);

            // ---- ② 复制 ----
            int i = 0;
            foreach (var e in toDisk)
            {
                // ★ 取消点：此刻本地还没删任何东西，随时可以安全停
                if (ct.IsCancellationRequested) return Cancelled("归档", "复制");
                i++;
                Report(progress, "复制到硬盘", 1, SC, i, toDisk.Count, e.Rel, P1 * i / total);

                string dst = Path.Combine(target, e.Rel);
                string? dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                string src = Path.Combine(r.Dir, e.Rel);
                File.Copy(src, dst, overwrite: true);
            }

            // ---- ③ 逐个校验指纹（全部通过才敢删） ----
            var bad = new List<string>();
            i = 0;
            foreach (var e in toDisk)
            {
                if (ct.IsCancellationRequested) return Cancelled("归档", "校验");
                i++;
                Report(progress, "校验落盘", 2, SC, i, toDisk.Count, e.Rel, P1 + (P2 - P1) * i / total);

                string src = Path.Combine(r.Dir, e.Rel);
                string dst = Path.Combine(target, e.Rel);

                if (!File.Exists(dst)) { bad.Add(e.Rel + "（目标没生成）"); continue; }

                long srcLen = new FileInfo(src).Length;
                long dstLen = new FileInfo(dst).Length;
                if (srcLen != dstLen) { bad.Add($"{e.Rel}（大小不一致）"); continue; }

                if (!string.Equals(Fingerprint(src), Fingerprint(dst), StringComparison.Ordinal))
                    bad.Add($"{e.Rel}（内容指纹不一致）");
            }

            if (bad.Count > 0)
            {
                // ★ 关键：校验没过 → 一个本地文件都不删。
                return new ArchiveResult
                {
                    Ok = false,
                    Message = "复制过去的文件和原文件对不上，已中止，本地什么都没删。\n\n" +
                              "有问题的文件：\n· " + string.Join("\n· ", bad.Take(10)) +
                              (bad.Count > 10 ? $"\n…还有 {bad.Count - 10} 个" : "") +
                              "\n\n工程已经在硬盘上了，可以手动对比处理。",
                    TargetDir = target,
                };
            }

            // ★ 最后一次取消机会：校验全过了、本地还一个没删。
            //   过了这一行就进入"落盘收尾"，不再响应取消。
            if (ct.IsCancellationRequested) return Cancelled("归档", "清理本地之前");

            // ---- ④ 写留痕（清单 + 归档记录），并一起送一份到硬盘 ----
            Report(progress, "写归档留痕", 3, SC, 1, 2, "归档清单.json", P3 - 1);
            var manifest = BuildManifest(r, plan, target);
            Directory.CreateDirectory(r.ArchiveDir);
            string mfPath = Path.Combine(r.ArchiveDir, "归档清单.json");
            ContractJson.WriteFile(mfPath, manifest);

            string notePath = Path.Combine(r.ArchiveDir, "归档记录.md");
            File.WriteAllText(notePath, BuildArchiveNote(r, plan, target), ContractJson.Utf8NoBom);
            Report(progress, "写归档留痕", 3, SC, 2, 2, "归档记录.md", P3);

            foreach (string f in new[] { mfPath, notePath })
            {
                string dst = Path.Combine(target, "_归档", Path.GetFileName(f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, overwrite: true);
            }

            // ---- ⑤ 删本地大件（此刻硬盘上已有一份且校验通过） ----
            //   ★ 这一步**故意不响应取消**：删到一半停下才是最糟的结果；
            //     而它只是纯删除、不读写内容，很快，锁着界面等它做完反而更安全。
            var toDelete = plan.Entries.Where(e => !e.KeepLocal).ToList();
            Report(progress, "清理本地", 4, SC, 0, toDelete.Count, "本地大件", P3);
            int k = 0;
            foreach (var e in toDelete)
            {
                k++;
                if (k % 20 == 1 || k == toDelete.Count)
                    Report(progress, "清理本地", 4, SC, k, toDelete.Count, e.Rel,
                           P3 + (P4 - P3) * k / Math.Max(1, toDelete.Count));

                try { File.Delete(Path.Combine(r.Dir, e.Rel)); } catch { /* 删不掉就留着，不致命 */ }
            }
            PruneEmptyDirs(r.Dir);

            // ---- ⑥ 更新档案：状态 + 归档记录 ----
            r.Model.Status = ProjectStatus.Archived;
            r.Model.Archive = new ArchivePart
            {
                ArchivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                TargetDir = target,
                VolumeSerial = plan.Disk.Serial,
                VolumeLabel = plan.Disk.Label,
                FileCount = toDisk.Count,
                TotalBytes = toDisk.Sum(e => e.Size),
            };
            ProjectStore.Save(r);

            // ---- ⑦ 记盘：下次认这块盘用（第一次归档才记） ----
            var cfg = WorkbenchWindow.State.Config;
            cfg.ArchiveVolumeSerial = plan.Disk.Serial;
            cfg.ArchiveVolumeLabel = plan.Disk.Label;
            if (plan.DiskFirstUse) WorkbenchWindow.State.SaveConfig();

            Report(progress, "全部完成", SC, SC, 0, 0, "归档完成", P4);

            return new ArchiveResult
            {
                Ok = true,
                FileCount = toDisk.Count,
                Bytes = toDisk.Sum(e => e.Size),
                TargetDir = target,
                Message = $"已归档：{r.Model.Name}",
            };
        }
        catch (Exception ex)
        {
            return new ArchiveResult
            {
                Ok = false,
                TargetDir = target,
                Message = "归档中途出错了（本地文件没有删除）：\n\n" + ex.Message,
            };
        }
    }

    // ---- 进度上报 / 取消结果 两个小工具（Execute 和 Restore 共用） ----

    /// <summary>发一条进度快照（后台线程调用；progress 为 null 时什么都不做）。</summary>
    private static void Report(Action<ArchiveProgress>? progress,
                              string stage, int stageIndex, int stageCount,
                              int done, int total, string detail, double percent)
        => progress?.Invoke(new ArchiveProgress
        {
            Stage = stage,
            StageIndex = stageIndex,
            StageCount = stageCount,
            Done = done,
            Total = total,
            Detail = detail,
            Percent = percent,
        });

    /// <summary>
    /// 用户自己按了「安全中止」时的返回。
    /// hint 不传则用归档那套措辞（本地一个都没动）；取回要传自己的措辞
    /// —— 取回是往本地写，中途停下会留下"半新半旧"的目录，得提醒重跑一次补全。
    /// </summary>
    private static ArchiveResult Cancelled(string what, string stoppedAt, string? hint = null) => new()
    {
        Ok = false,
        Cancelled = true,
        Message = $"{what}已中止（停在「{stoppedAt}」）。\n\n" +
                  (hint ?? "★ 本地文件一个都没动，工程原样都在，放心。\n" +
                           "硬盘上可能留了半份拷贝，不用管它：下次归档会自动覆盖。"),
    };

    /// <summary>取回被中止时的额外说明（取回是往本地写，半路停下会新旧混着）。</summary>
    private const string RestoreCancelHint =
        "★ 已经拷回来一部分，本地现在是「半新半旧」。\n" +
        "再点一次「取回工程」就能补全 —— 重复的文件会被覆盖，重跑是安全的。";

    /// <summary>把空目录由深到浅删掉，让本地桩干净；项目根永远不动。</summary>
    private static void PruneEmptyDirs(string root)
    {
        try
        {
            var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                                .OrderByDescending(d => d.Length)
                                .ToList();

            foreach (string d in dirs)
            {
                try
                {
                    if (d.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch { /* 删不掉就算了 */ }
            }
        }
        catch { /* 整段不致命 */ }
    }

    private static ArchiveManifest BuildManifest(ProjectRef r, ArchivePlan plan, string target)
    {
        var mf = new ArchiveManifest
        {
            项目名 = r.Model.Name,
            项目Id = r.Model.Id,
            归档时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            源目录 = r.Dir,
            目标目录 = target,
            卷序列号 = plan.Disk.Serial,
            卷标 = plan.Disk.Label,
            搬到硬盘文件数 = plan.MoveCount,
            搬到硬盘字节 = plan.MoveBytes,
            本地删除文件数 = plan.DeleteCount,
            本地删除字节 = plan.DeleteBytes,
            本地保留文件数 = plan.KeepCount,
            本地保留字节 = plan.KeepBytes,
            成片 = plan.Films,
        };

        foreach (var e in plan.Entries)
        {
            mf.文件.Add(new ManifestFile
            {
                路径 = e.Rel,
                大小 = e.Size,
                指纹 = e.ToDisk ? Fingerprint(Path.Combine(r.Dir, e.Rel)) : "",
                处理 = e.ToDisk
                        ? (e.KeepLocal ? "搬走（本地也留）" : "搬走（本地不留）")
                        : "不搬（分镜渲染出来的画面）· 本地也不留",
            });
        }

        return mf;
    }

    private static string BuildArchiveNote(ProjectRef r, ArchivePlan plan, string target)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 归档记录 · {r.Model.Name}");
        sb.AppendLine();
        sb.AppendLine($"> 归档时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"> 工程去向：`{target}`");
        sb.AppendLine($"> 所在盘：{plan.Disk.Describe()}");
        sb.AppendLine();
        sb.AppendLine("## 这次做了什么");
        sb.AppendLine();
        sb.AppendLine($"- 搬到硬盘：**{plan.MoveCount} 个文件 / {ArchivePlan.Human(plan.MoveBytes)}**");
        sb.AppendLine($"- 本地删除：**{plan.DeleteCount} 个文件 / {ArchivePlan.Human(plan.DeleteBytes)}**（分镜渲染出来的画面、卡片图、备份、大件）");
        sb.AppendLine($"- 本地保留：**{plan.KeepCount} 个文件 / {ArchivePlan.Human(plan.KeepBytes)}**（工程档案、脚本、分镜代码、封面、复盘数据）");
        sb.AppendLine();
        sb.AppendLine("## 成片");
        sb.AppendLine();
        foreach (string f in plan.Films) sb.AppendLine($"- `{f}`");
        sb.AppendLine();
        sb.AppendLine("## 怎么取回");
        sb.AppendLine();
        sb.AppendLine("工作台 →「已归档」→ 这个项目 → 点「取回工程」。");
        sb.AppendLine("或者手动：把上面那个目录整个拷回 `" + r.Dir + "`。");
        sb.AppendLine();
        sb.AppendLine("> 本地保留的 `review\\`（复盘数据）以本地为准；取回时不会被硬盘上的旧版覆盖。");
        return sb.ToString();
    }

    // ========================================================
    // ③ 取回工程（硬盘 → 本地）
    // ========================================================

    /// <summary>
    /// 把归档的工程从硬盘整包拷回本地，项目恢复成"进行中"。
    ///
    /// 【为什么要专门做这一步】归档是"真搬走"——本地没有源视频和成片了。
    ///   哪天想改一版再发、或者某个镜头要重渲，没有取回就只能重做。
    ///   归档就成了一条单向路，一次误操作就是永久损失。
    ///
    /// 【review\ 和 _归档\ 不覆盖】那两份以**本地**为准（复盘数据是归档后
    ///   也可能继续填的），用硬盘上的旧版盖回去等于把后来的记录抹掉。
    /// </summary>
    public static ArchiveResult Restore(ProjectRef r, Action<ArchiveProgress>? progress = null,
                                        CancellationToken ct = default)
    {
        var arc = r.Model.Archive;
        if (arc == null || string.IsNullOrWhiteSpace(arc.TargetDir))
            return new ArchiveResult { Ok = false, Message = "这个项目没有归档记录，取不了。" };

        string src = arc.TargetDir;
        if (!Directory.Exists(src))
        {
            var disk = Probe(src);
            return new ArchiveResult
            {
                Ok = false,
                Message = disk.Exists
                    ? $"归档目录不在了：{src}\n\n盘在，但这个目录被移动/删除了。"
                    : $"移动硬盘没插上（找不到 {src}）。\n\n插好硬盘再点「取回工程」。",
            };
        }

        // 换盘保护：序列号对不上就别往里写
        var now = Probe(src);
        if (now.Exists && !string.IsNullOrEmpty(arc.VolumeSerial) &&
            !string.Equals(now.Serial, arc.VolumeSerial, StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveResult
            {
                Ok = false,
                Message = "现在插着的不是归档时那块盘（序列号对不上），已中止取回。\n\n" +
                          $"归档时：{arc.VolumeSerial}　现在：{now.Serial}",
            };
        }

        // 取回只有两步：把文件拷回来（占绝大头）→ 把工程档案写回「进行中」。
        const int SC = 2;
        const double P1 = 96, P2 = 100;

        try
        {
            var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList();
            int total = Math.Max(1, files.Count);
            int i = 0;
            int copied = 0;

            foreach (string f in files)
            {
                // ★ 取消点：取回是"只拷不删"，随时停都不会弄丢任何东西
                if (ct.IsCancellationRequested) return Cancelled("取回", "复制", RestoreCancelHint);

                string rel = Path.GetRelativePath(src, f);
                string relSlash = rel.Replace('/', '\\');

                // 本地权威的两处，不用硬盘的旧版覆盖
                if (relSlash.StartsWith("review\\", StringComparison.OrdinalIgnoreCase) ||
                    relSlash.StartsWith("_归档\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                i++;
                Report(progress, "复制回本地", 1, SC, i, files.Count, relSlash, P1 * i / total);

                string dst = Path.Combine(r.Dir, rel);
                string? dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                File.Copy(f, dst, overwrite: true);
                copied++;
            }

            // 恢复成进行中；归档记录清掉（工程已经回来了，不再需要那条线索）
            Report(progress, "写回工程档案", 2, SC, 1, 1, "project.json", P1);
            r.Model.Status = ProjectStatus.Active;
            r.Model.Archive = null;
            ProjectStore.Save(r);
            Report(progress, "全部完成", SC, SC, 0, 0, "取回完成", P2);

            return new ArchiveResult
            {
                Ok = true,
                FileCount = copied,
                TargetDir = src,
                Message = $"已取回：{r.Model.Name}（{copied} 个文件）。现在它回到「进行中」。",
            };
        }
        catch (Exception ex)
        {
            return new ArchiveResult { Ok = false, Message = "取回中途出错了：\n\n" + ex.Message };
        }
    }

    // ========================================================
    // ④ 杂项
    // ========================================================

    /// <summary>在资源管理器里打开某个目录（归档后"去看看硬盘上长什么样"）。</summary>
    public static void OpenFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了，不值得弹窗 */ }
    }

    /// <summary>
    /// 让文件总库的台账把硬盘上新增的文件入账（后台，不阻塞）。
    ///
    /// 【为什么要调它，而不是在 C# 里自己写一遍】
    ///   台账那套规则（快指纹、增量、跳过坏文件、镜像到 E:\_台账\）
    ///   已经是稳定跑着的脚本。在这儿复制一份 C# 版本 = 将来两边一改就分叉。
    ///   所以工作台只负责"搬文件"，入库这件事交给已有的工具。
    ///
    /// 【为什么可以跳过】它是后台的收尾活，失败也不影响归档本身
    ///   （最坏情况是硬盘上那批文件暂时没进台账，下次手动跑一次就行）。
    /// </summary>
    public static void TryUpdateLedger()
    {
        try
        {
            string bat = @"D:\文件总库\_工具\台账\2-增量更新.bat";
            if (!File.Exists(bat)) return;

            // ★ 起法有三条讲究（V1.13.2 踩到才补的，别删）：
            //   ① `WorkingDirectory` 必须显式指定 bat 自己所在目录 ——
            //      不指定就继承工作台进程的当前目录，子进程会一直抓着那个目录不放
            //      （Windows 上"当前目录"是个不允许删除/改名的句柄）。
            //      2026-10-15 搬家时旧软件夹一直删不掉，元凶就是这条：
            //      一个 15:31 起的 cmd 继承了工作台当时的目录，把旧文件夹钉死了 4 小时。
            //   ② 命令行末尾的 `< nul` 不能省 —— 那个 bat 最后一行是 `pause`，
            //      后台最小化跑没人按键，`pause` 永远等不到输入 → cmd.exe 挂着不退出，
            //      每归档一次就留一个僵尸进程（同样会把目录钉住）。
            //      把 stdin 接到 NUL，`pause` 立刻读到 EOF 放行，脚本跑完窗就消失。
            //   ③ 仍然保持"最小化窗口"的观感（不动 `WindowStyle`），失败也不影响归档。
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\" < nul")
            {
                WorkingDirectory = Path.GetDirectoryName(bat) ?? "",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            });
        }
        catch { /* 台账工具不在/调不动，都不影响归档本身 */ }
    }
}
