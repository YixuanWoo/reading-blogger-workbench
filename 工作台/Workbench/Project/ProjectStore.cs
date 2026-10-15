// ============================================================
// ProjectStore.cs - 项目在磁盘上的读写（唯一碰文件的地方）
// ============================================================
//
// 【项目在磁盘上长什么样】
//   <项目根目录>\<项目名>\
//       project.json     ← 全部状态（源文件、粗剪、精剪时间轴、分镜、导出记录）
//       media\           ← 这个项目自己的素材（导入时复制进来）
//       render\          ← 渲染输出
//       export\          ← 成片
//
// 【为什么状态放一个 json 而不是拆成好几个文件】
//   拆开的好处是"某一块坏了不影响别的"，坏处是保存时要么写一半失败、
//   要么得做事务。对一个人的工作台来说，一次原子写一个文件最不容易出事故。
//   真到了文件太大那天再拆（那时候 Version 已经给了迁移的口子）。
//
// 【写盘安全】
//   先写 .tmp 再 File.Replace 覆盖 —— 万一写到一半断电/崩溃，
//   原来那份 project.json 还是完好的。剪了一晚上的东西不能赌运气。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench.Project;

/// <summary>
/// 一个已打开/已定位的项目：文件夹 + 内存里的那份状态。
/// </summary>
public sealed class ProjectRef
{
    /// <summary>项目文件夹（绝对路径）</summary>
    public string Dir { get; init; } = "";

    /// <summary>项目状态</summary>
    public ProjectModel Model { get; set; } = new();

    /// <summary>主状态文件</summary>
    public string JsonPath => Path.Combine(Dir, "project.json");

    /// <summary>本项目自己的素材目录</summary>
    public string MediaDir => Path.Combine(Dir, "media");

    /// <summary>渲染输出目录</summary>
    public string RenderDir => Path.Combine(Dir, "render");

    /// <summary>成片目录</summary>
    public string ExportDir => Path.Combine(Dir, "export");

    /// <summary>
    /// 本项目自己的素材目录（素材\）。
    ///
    /// 【为什么素材在项目里，而不是全局一个素材库】
    ///   2026-10-01 用户拍板：工作台不指向任何外部固定素材夹。
    ///   立项时自动建出这个文件夹，这个项目用到的图、音频、公共素材
    ///   全部待在项目自己肚子里 —— 整个项目文件夹拷走，素材一个不少。
    /// </summary>
    public string AssetsDir => Path.Combine(Dir, "素材");

    /// <summary>本项目素材里的"公共素材"子夹（背景图这类每个镜头共用的东西放这里）。</summary>
    public string AssetsCommonDir => Path.Combine(AssetsDir, "公共素材");

    /// <summary>
    /// 各步骤产物的落盘根目录。
    ///
    /// 【为什么要落盘，而不是只留在 project.json 里】
    ///   ① 进度能从"猜"变成"数文件"；
    ///   ② 打开项目一眼看出上次停在哪一步；
    ///   ③ Agent 读写文件比读写数据库容易得多 —— 任务包/回包都在这里交换。
    /// </summary>
    public string StageDir => Path.Combine(Dir, "stages");

    /// <summary>
    /// ①·五 转写：口播音频的逐句稿（transcript.json）。
    ///
    /// 【为什么单独给这一步一个目录】
    ///   转写不是工作台自己干的（要模型/算力/联网），是**外部 Agent** 写进来的
    ///   —— 见 Workbench\Agent\TranscriptPrompt.cs 开头的说明。
    ///   既然是"别人写、我们读"，就必须有一个写死的、双方都认的位置：
    ///   提示词里能写死路径、导入对话框默认就落在这里，两边永远不会错位。
    ///   （以前只有一个全局的 Config.TranscriptDir，还指向 C 盘一个已删掉的
    ///    临时目录 —— 换个项目就得满硬盘找稿子。2026-10-01 收进项目里。）
    /// </summary>
    public string TranscriptDir => Path.Combine(Dir, "stages", "01-transcript");

    /// <summary>
    /// ⭐ 〇 稿源：Markdown 写作台的本机视图状态（view-state.json）。
    ///
    /// 【为什么编号 00 —— 它在①之前】
    ///   稿子是转写的**上游**：script.md → transcript.json → ②③④⑤。
    ///   现有 01/03/04/05 **一个都不用动**，只往前加一个 00。
    ///   （02空缺，那是精剪的阶段目录，与稿源无关。）
    ///
    /// 【稿源本身不在这里】
    ///   script.md 放&lt;项目根&gt;\ —— 它的用户是你，要能在 Obsidian 里直接找到打开。
    ///   这里放的是"给程序看的"：写作台的本机视图状态（view-state.json，
    ///   只记预览开关这类本机备忘，不随文档迁移）。
    /// </summary>
    public string ScriptDir => Path.Combine(Dir, "stages", "00-script");

    /// <summary>③ 分镜：口播稿 / 镜头表 / 给 Agent 的提示词与任务包。</summary>
    public string ShotDir => Path.Combine(Dir, "stages", "03-shot");

    /// <summary>
    /// ④ 渲染：镜头产物（逐镜头 MP4）、渲染任务包、渲染日志。
    ///
    /// 【为什么和上面的 RenderDir 并存】
    ///   RenderDir 是老的 `<项目>\render`，当年只用来倒卡片图；
    ///   现在渲染整段走 Agent（HyperFrames），产物是"逐镜头 MP4 + 任务包"，
    ///   跟其它步骤的产物一样归到 stages 下按步骤编号放 —— 一眼看出做到第几步。
    /// </summary>
    public string RenderStageDir => Path.Combine(Dir, "stages", "04-render");

    /// <summary>
    /// 渲染产物目录（逐镜头 MP4）：&lt;项目夹&gt;\渲染产物\。
    ///
    /// 【为什么必须和上面的 RenderStageDir 分开写】
    ///   两个目录装的不是一回事：
    ///     RenderStageDir（stages\04-render）= 渲染这一步的**过程文件**：
    ///                                          任务包、提示词、日志、缩略图；
    ///     RenderOutputDir（渲染产物）        = 真正拿去 ⑤ 组接的 **shot_xxx.mp4**。
    ///   2026-10-01 之前两者混用同一个字段（render-spec.json 的 OutputDir）：
    ///   分镜页写的是 stages\04-render、渲染页写的是 渲染产物 —— 同一个 json
    ///   两个页面写出两个值，重开项目时"产物到底在哪"就说不清了（T-10）。
    ///   现在统一：凡是要"找/写 shot_xxx.mp4"的，一律用这一条。
    /// </summary>
    public string RenderOutputDir => Path.Combine(Dir, "渲染产物");

    /// <summary>⑤ 组接与导出：成片、XML、清单。</summary>
    public string AssembleDir => Path.Combine(Dir, "stages", "05-assemble");

    /// <summary>
    /// ★ V1.13.0 复盘数据目录：&lt;项目&gt;\review\。
    ///
    /// 【为什么数据放在项目里（而不是一个全局的数据库/飞书表）】
    ///   复盘数据是**某一支片**的属性（播放量、涨粉、结论），跟片子长在一起才对；
    ///   而"项目自包含"是这个工作台从第一天起的铁律（整个项目夹拷走，什么都不缺）。
    ///   放在这里还带来一个关键好处：**归档之后它还在本地**，
    ///   硬盘不插着也能查数据、填数据、做可视化。
    /// </summary>
    public string ReviewDir => Path.Combine(Dir, "review");

    /// <summary>复盘数据文件（多平台 × 多次快照）。</summary>
    public string ReviewPath => Path.Combine(ReviewDir, "review.json");

    /// <summary>复盘报告（一页人话，由复盘页生成）。</summary>
    public string ReviewReportPath => Path.Combine(ReviewDir, "复盘报告.md");

    /// <summary>归档留痕目录：&lt;项目&gt;\_归档\（归档清单.json + 归档记录.md）。</summary>
    public string ArchiveDir => Path.Combine(Dir, "_归档");

    /// <summary>
    /// 这个项目是不是"已归档"。
    /// 判据用 Status 字段（唯一权威）—— Archive 段只是"工程在哪"的线索，
    /// 取回之后会被清掉，所以不能拿它当判据。
    /// </summary>
    public bool IsArchived =>
        string.Equals(Model.Status, ProjectStatus.Archived, StringComparison.OrdinalIgnoreCase);

    public string Name => Model.Name;

    public override string ToString() => $"{Model.Name} @ {Dir}";
}

/// <summary>
/// 项目的增删查改。所有和"项目文件夹"打交道的代码都必须走这里。
/// </summary>
public static class ProjectStore
{
    /// <summary>已删除项目的回收子目录（不真删，只是挪进去 —— 剪到一半的东西不能一键没了）</summary>
    private const string TrashFolderName = "_已删除";

    // ========================================================
    // 项目根目录
    // ========================================================

    private static string? _rootOverride;

    /// <summary>
    /// 项目根目录。用户可以改（顶栏/项目页里能改），改了立刻生效。
    /// 默认放在 exe 同文件夹下的「项目」（2026-10-01 用户拍板：
    /// 软件用到的文件必须在 exe 旁边，项目数据不散落在外部目录）。
    /// </summary>
    public static string RootDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_rootOverride)) return _rootOverride!;

            string pref = WorkbenchWindow.State.Config.ProjectRoot;
            if (!string.IsNullOrWhiteSpace(pref)) return pref;

            return FallbackRoot();
        }
        set
        {
            _rootOverride = value;
            WorkbenchWindow.State.Config.ProjectRoot = value;
            WorkbenchWindow.State.SaveConfig();
        }
    }

    private static string FallbackRoot()
    {
        // exe 在哪，项目就在哪：&lt;exe目录&gt;\项目\
        // （发布版 = D:\工作台\项目\；开发跑在 bin 下也无妨，是完整自包含的）
        return Path.Combine(AppContext.BaseDirectory, "项目");
    }

    /// <summary>确保根目录存在（第一次用时创建，别让用户在资源管理器里手动建）。</summary>
    public static void EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
        }
        catch
        {
            // 建不了就让上层在列表页/新建时报错，不要在这里打断启动
        }
    }

    // ========================================================
    // 查
    // ========================================================

    /// <summary>
    /// 扫描根目录下所有项目。读不出来的（缺 project.json / 文件坏了）直接跳过，
    /// 不让一个坏项目把整个列表拖垮。
    /// </summary>
    public static List<ProjectRef> LoadAll()
    {
        var result = new List<ProjectRef>();
        try
        {
            if (!Directory.Exists(RootDir)) return result;

            foreach (string dir in Directory.GetDirectories(RootDir))
            {
                string leaf = Path.GetFileName(dir);
                if (leaf.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase)) continue;

                var r = Load(dir);
                if (r != null) result.Add(r);
            }
        }
        catch
        {
            // 目录读不了就返回已读到的部分
        }

        return result.OrderByDescending(p => p.Model.UpdatedAt).ToList();
    }

    /// <summary>读一个项目文件夹。没有 project.json 就返回 null。</summary>
    public static ProjectRef? Load(string dir)
    {
        try
        {
            string jsonPath = Path.Combine(dir, "project.json");
            if (!File.Exists(jsonPath)) return null;

            var model = ContractJson.ReadFile<ProjectModel>(jsonPath);
            if (model == null) return null;

            model = Migrate(model);

            // 兜底：老档案可能没存名字，用文件夹名顶上
            if (string.IsNullOrWhiteSpace(model.Name))
                model.Name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(model.Id))
                model.Id = Guid.NewGuid().ToString("N");

            var r = new ProjectRef { Dir = dir, Model = model };
            EnsureAssets(r);   // 老项目没有「素材」夹的，打开时补建（幂等）
            return r;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 版本迁移。现在只有 v1，所以只是补齐默认值；
    /// 将来结构变了，在这里按 Version 逐级往上修 —— 老项目永远打得开。
    /// </summary>
    private static ProjectModel Migrate(ProjectModel m)
    {
        m.Source ??= new SourcePart();
        m.Cut ??= new CutPart();
        m.FineCut ??= new FineCutPart();
        m.FineCut.Clips ??= new List<ClipPart>();
        m.Shots ??= new ShotPart();
        m.Shots.Rows ??= new List<ShotRow>();
        m.Output ??= new OutputPart();
        m.Ui ??= new UiPart();

        if (string.IsNullOrWhiteSpace(m.Status)) m.Status = ProjectStatus.Active;
        if (m.FineCut.SecPerPixel <= 0) m.FineCut.SecPerPixel = 0.05;

        m.Version = ProjectModel.CurrentVersion;
        return m;
    }

    // ========================================================
    // 写
    // ========================================================

    /// <summary>
    /// 存盘。先写临时文件再原子替换 —— 中途崩了也不会把原档案写坏。
    /// </summary>
    public static void Save(ProjectRef r)
    {
        Directory.CreateDirectory(r.Dir);
        Directory.CreateDirectory(r.MediaDir);

        r.Model.UpdatedAt = DateTime.Now;

        string tmp = r.JsonPath + ".tmp";
        File.WriteAllText(tmp, ContractJson.Serialize(r.Model), new UTF8Encoding(false));

        if (File.Exists(r.JsonPath))
        {
            // File.Replace 需要目标存在；用 ignoreMetadataErrors 兼容各种文件系统
            File.Replace(tmp, r.JsonPath, destinationBackupFileName: null,
                         ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, r.JsonPath);
        }
    }

    // ========================================================
    // 新建 / 归档 / 删除
    // ========================================================

    /// <summary>
    /// 新建一个项目（会真的建出文件夹和 project.json）。
    /// 同名时自动加序号，不覆盖已有的。
    /// </summary>
    public static ProjectRef Create(string name, string channel, string note = "")
    {
        EnsureRoot();

        name = string.IsNullOrWhiteSpace(name) ? "未命名项目" : name.Trim();
        string safe = SanitizeFolderName(name);

        string dir = Path.Combine(RootDir, safe);
        int n = 2;
        while (Directory.Exists(dir))
            dir = Path.Combine(RootDir, $"{safe} ({n++})");

        var model = new ProjectModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Channel = channel ?? "",
            Note = note ?? "",
            Status = ProjectStatus.Active,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };

        var r = new ProjectRef { Dir = dir, Model = model };

        Directory.CreateDirectory(r.Dir);
        Directory.CreateDirectory(r.MediaDir);
        Directory.CreateDirectory(r.RenderDir);
        Directory.CreateDirectory(r.ExportDir);
        EnsureAssets(r);
        Save(r);

        return r;
    }

    /// <summary>
    /// 软件自带的素材种子文件（随程序发布，立项时从这里复制进项目）。
    ///
    /// ★ V1.14.0：先看配置里的「立项素材种子目录」（设置 → 路径配置），
    ///   留空才退回 exe 旁的 `素材模板\`。以前它是写死的，设置里改了没处生效。
    /// </summary>
    private static string SeedDir
    {
        get
        {
            string pref = WorkbenchWindow.State.Config.SeedDir;
            if (!string.IsNullOrWhiteSpace(pref)) return pref;
            return FallbackSeedDir;
        }
    }

    /// <summary>种子目录的默认位置（exe 旁的「素材模板」）。设置界面要显示它。</summary>
    public static string FallbackSeedDir => Path.Combine(AppContext.BaseDirectory, "素材模板");

    /// <summary>
    /// 确保项目的「素材\公共素材\」目录存在，并把软件自带的种子素材
    /// （标准背景图等）复制进去。立项时调用；老项目缺这个夹也会在打开时补上。
    /// </summary>
    public static void EnsureAssets(ProjectRef r)
    {
        try
        {
            Directory.CreateDirectory(r.AssetsCommonDir);

            // 种子素材：有什么复制什么（现在只有标准背景图，将来加东西放 素材模板\ 就自动跟）
            if (Directory.Exists(SeedDir))
            {
                foreach (string src in Directory.GetFiles(SeedDir))
                {
                    string dst = Path.Combine(r.AssetsCommonDir, Path.GetFileName(src));
                    if (!File.Exists(dst))          // 项目里改过的文件不许被覆盖
                        File.Copy(src, dst);
                }
            }
        }
        catch
        {
            // 素材夹建不出来不致命 —— 渲染页会提示背景缺失，用户可手动补
        }
    }

    /// <summary>归档 / 取消归档。</summary>
    public static void SetArchived(ProjectRef r, bool archived)
    {
        r.Model.Status = archived ? ProjectStatus.Archived : ProjectStatus.Active;
        Save(r);
    }

    /// <summary>改名：改 project.json 里的名字（文件夹名不动，免得把路径搞乱）。</summary>
    public static void Rename(ProjectRef r, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        r.Model.Name = newName.Trim();
        Save(r);
    }

    /// <summary>
    /// "删除"项目 —— 其实是把整个文件夹挪进 _已删除 里。
    /// 剪辑到一半的东西不能真的消失，需要腾空间时自己去资源管理器清。
    /// </summary>
    public static void MoveToTrash(ProjectRef r)
    {
        string trash = Path.Combine(RootDir, TrashFolderName);
        Directory.CreateDirectory(trash);

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string target = Path.Combine(trash, $"{stamp}_{Path.GetFileName(r.Dir)}");
        int n = 2;
        while (Directory.Exists(target))
            target = Path.Combine(trash, $"{stamp}_{Path.GetFileName(r.Dir)} ({n++})");

        Directory.Move(r.Dir, target);
    }

    // ========================================================
    // 工具
    // ========================================================

    /// <summary>把项目名里不能当文件夹名的字符换掉。</summary>
    public static string SanitizeFolderName(string name)
    {
        var sb = new StringBuilder(name.Length);
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (char c in name)
            sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);

        string s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length == 0) s = "未命名项目";
        if (s.Length > 60) s = s[..60];
        return s;
    }

    /// <summary>
    /// 估个进度（0~1），给卡片墙上的进度条用。
    /// 规则简单粗暴：每完成一个阶段加一段，够用就行。
    /// </summary>
    public static double EstimateProgress(ProjectModel m)
    {
        double p = 0.05;   // 立项本身算开了个头
        if (!string.IsNullOrWhiteSpace(m.Source.AudioPath)) p += 0.15;
        if (m.Cut.Plan != null && m.Cut.Plan.Segments.Count > 0) p += 0.15;
        if (m.FineCut.Clips.Count > 0) p += 0.20;
        if (m.Shots.Rows.Count > 0) p += 0.15;
        if (HasRenderOutput(m)) p += 0.15;
        if (!string.IsNullOrWhiteSpace(m.Output.LastExportPath)) p += 0.15;
        return Math.Min(1.0, p);
    }

    private static bool HasRenderOutput(ProjectModel m)
    {
        try
        {
            if (Directory.Exists(m.Output.RenderDir) &&
                Directory.EnumerateFiles(m.Output.RenderDir).Any())
                return true;
        }
        catch
        {
            // 目录读不了就当没有
        }
        return false;
    }
}
