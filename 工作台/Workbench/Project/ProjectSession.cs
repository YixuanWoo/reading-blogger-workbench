// ============================================================
// ProjectSession.cs - 「当前打开的项目」+ 内存状态与存档之间的搬运
// ============================================================
//
// 【它管三件事】
//   1. 打开 / 关闭项目：把 project.json 灌进 WorkbenchState，反之亦然
//   2. 脏标记：判断"有没有还没存的改动"
//   3. 保存：把内存状态写回磁盘
//
// 【脏标记为什么不用"手动打标记"】
//   手动打标记要在每个可能改数据的地方都插一行 MarkDirty()，
//   只要漏一处，就会"看着存过了其实没存" —— 这种 bug 最坑人。
//
//   这里改成"指纹比对"：把当前内存状态投影成一份存档，
//   序列化成 JSON，取哈希。哈希和上次存盘时不一样 = 有改动。
//
//   好处是绝对漏不掉：只要有任何字段变了，指纹就变。
//   代价是每隔几秒算一次哈希（几十 KB 的字符串）—— 可以忽略。
//
// 【和 WorkbenchState 的分工】
//   WorkbenchState = 运行时内存状态（含解码波形等大对象，不进存档）
//   ProjectModel   = 存档（只存"下次还想看到"的那部分）
//   ProjectSession = 两者之间的搬运工
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;
using LiteCut.Core.Media;

namespace LiteCut.Workbench.Project;

/// <summary>
/// 当前项目会话。整个程序只有一个实例，挂在 WorkbenchWindow 上。
/// </summary>
public sealed class ProjectSession
{
    private readonly WorkbenchState _state;

    /// <summary>上次存盘时的状态指纹（用来判断有没有未保存的改动）</summary>
    private string _baseline = "";

    /// <summary>当前打开的项目（null = 没打开任何项目）</summary>
    public ProjectRef? Current { get; private set; }

    public bool HasProject => Current != null;

    /// <summary>最后一次成功保存的时间（顶栏显示用）</summary>
    public DateTime? LastSavedAt { get; private set; }

    /// <summary>打开 / 关闭 / 改名之后触发（外壳据此刷新标题和面包屑）</summary>
    public event EventHandler? ProjectChanged;

    /// <summary>保存成功之后触发</summary>
    public event EventHandler? Saved;

    public ProjectSession(WorkbenchState state)
    {
        _state = state;
    }

    // ========================================================
    // 脏标记
    // ========================================================

    /// <summary>有没有还没保存的改动</summary>
    public bool IsDirty => HasProject && !string.Equals(Fingerprint(), _baseline, StringComparison.Ordinal);

    /// <summary>当前内存状态的指纹</summary>
    public string Fingerprint() => StableHash(ContractJson.Serialize(Projected()));

    /// <summary>重设基线（比如打开项目之后）</summary>
    public void ResetBaseline()
    {
        _baseline = Fingerprint();
    }

    /// <summary>
    /// 稳定哈希（FNV-1a 64 位）。
    /// 不用 string.GetHashCode()：它在 .NET 里每次进程启动都会变种子，
    /// 拿它做"和上次比"的判断会时对时错。
    /// </summary>
    private static string StableHash(string s)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= prime;
            }
            return h.ToString("x16");
        }
    }

    // ========================================================
    // 打开 / 关闭
    // ========================================================

    /// <summary>
    /// 打开项目：把存档灌进内存状态。
    /// 注意这里会把上一步残留的运行时状态清干净 —— 不然会出现
    /// "打开项目 B 却还带着项目 A 的波形/分镜"这种串味事故。
    /// </summary>
    public void Open(ProjectRef r)
    {
        Current = r;
        ApplyToState(r.Model);
        LastSavedAt = r.Model.UpdatedAt;
        ResetBaseline();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>关掉当前项目，回到"没打开项目"的状态。</summary>
    public void Close()
    {
        Current = null;
        ClearState();
        _baseline = "";
        LastSavedAt = null;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    // ========================================================
    // 保存
    // ========================================================

    /// <summary>
    /// 保存项目。返回是否真的写了盘。
    /// 没打开项目 / 没有任何改动时直接返回 false，不做无谓的磁盘写。
    /// </summary>
    public bool Save(bool force = false)
    {
        if (Current == null) return false;
        if (!force && !IsDirty) return false;

        try
        {
            Current.Model = Projected();
            ProjectStore.Save(Current);
            LastSavedAt = Current.Model.UpdatedAt;
            ResetBaseline();
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            // 存不下（磁盘满/只读/文件被占用）不该打断剪辑，交给调用方提示
            return false;
        }
    }

    /// <summary>改项目名（同时刷新基线，避免"因为改名而永远显示未保存"）。</summary>
    public void Rename(string newName)
    {
        if (Current == null || string.IsNullOrWhiteSpace(newName)) return;
        Current.Model.Name = newName.Trim();
        ProjectStore.Save(Current);
        _baseline = Fingerprint();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>重新扫描磁盘（外部改过档案时用）</summary>
    public void Reload()
    {
        if (Current == null) return;
        var fresh = ProjectStore.Load(Current.Dir);
        if (fresh == null) return;
        Open(fresh);
    }

    // ========================================================
    // 内存 → 存档
    // ========================================================

    /// <summary>
    /// 把当前内存状态投影成一份存档对象（不动 Current.Model，纯函数）。
    /// 保存和"算指纹"都用它，保证两者永远看的是同一份数据。
    /// </summary>
    private ProjectModel Projected()
    {
        var src = Current?.Model ?? new ProjectModel();

        var m = new ProjectModel
        {
            // ---- 元信息：原样带过来（时间戳不参与指纹，避免每次算都不同）----
            Version = ProjectModel.CurrentVersion,
            Id = src.Id,
            Name = src.Name,
            Channel = src.Channel,
            Status = src.Status,
            Note = src.Note,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt,

            // ★ V1.13.0 归档记录必须原样带过来。
            //   Projected() 是"新建一个对象再逐个字段搬"，漏了这一个字段
            //   就会发生"点一次保存，归档信息没了"——项目从已归档退回普通项目，
            //   而工程还躺在硬盘上，两边对不上。这是最容易踩的一脚。
            Archive = src.Archive,
        };

        // ---- ① 源素材 ----
        m.Source = new SourcePart
        {
            AudioPath = _state.SourcePath,
            TranscriptPath = _state.TranscriptPath,
            Transcript = _state.Transcript,
        };

        // ---- ① 粗剪 ----
        m.Cut = new CutPart
        {
            Preset = _state.Config.LastPreset,
            Options = _state.LastOptions,
            Plan = _state.Plan,
        };

        // ---- ② 精剪：完整时间轴（原样存，才能原样还原）----
        m.FineCut = new FineCutPart
        {
            Clips = _state.TimelineSnapshot.Select(Clone).ToList(),
            PlayheadSec = _state.TimelinePlayheadSec,
            ViewStartSec = _state.TimelineViewStartSec,
            SecPerPixel = _state.TimelineSecPerPixel,
            SourcePath = _state.TimelineSourcePath,
            Applied = _state.HasFineCut,
        };

        // ---- ③ 分镜 ----
        m.Shots = new ShotPart
        {
            ScriptText = _state.ScriptText,
            Rows = _state.Shots.ToList(),
            // ★ 草稿也要存 —— 不然"拆了一半还没交接"就关程序，回来全白拆。
            //   ⚠ 这里必须是深拷贝：草稿和已交接版若共享 ShotRow 对象引用，
            //     在草稿上打字会顺手改掉已交接版，闸门就失效了。
            DraftRows = _state.ShotDraft.Select(WorkbenchState.CloneShot).ToList(),
            CommittedAt = _state.ShotCommittedAt,
            NextShotSeq = _state.ShotSeq,
            Requirement = _state.ShotRequirement,
            SplitMode = _state.ShotSplitMode,
            PaceSec = _state.ShotPaceSec,
            ScriptSource = _state.FinalScriptSource,
            // ★ 稿子连"改过的句子"一起存 —— 重开项目不用重改一遍错字
            FinalScript = _state.FinalScriptDoc,
        };

        // ---- ④⑤ 渲染 / 导出 ----
        m.Output = new OutputPart
        {
            RenderDir = _state.RenderOutputDir,
            LastExportPath = _state.LastExportPath,
        };

        m.Ui = new UiPart { NavId = src.Ui?.NavId ?? "" };
        m.Progress = ProjectStore.EstimateProgress(m);
        return m;
    }

    private static ClipPart Clone(ClipPart c) => new()
    {
        Id = c.Id,
        SourcePath = c.SourcePath,
        SourceName = c.SourceName,
        SourceStartSec = c.SourceStartSec,
        SourceEndSec = c.SourceEndSec,
        SourceTotalSec = c.SourceTotalSec,
        TimelineStartSec = c.TimelineStartSec,
        VolumeDb = c.VolumeDb,
        FadeInSec = c.FadeInSec,
        FadeOutSec = c.FadeOutSec,
    };

    // ========================================================
    // 存档 → 内存
    // ========================================================

    private void ApplyToState(ProjectModel m)
    {
        ClearState();

        // ---- ① 源素材 ----
        _state.SourcePath = m.Source.AudioPath ?? "";
        _state.TranscriptPath = m.Source.TranscriptPath ?? "";
        _state.Transcript = m.Source.Transcript;

        // 波形 / 探测结果不进存档（能从源文件重算）——
        // 交给第①步自己解码：把文件排进它的待加载槽位即可。
        if (!string.IsNullOrWhiteSpace(_state.SourcePath) &&
            System.IO.File.Exists(_state.SourcePath))
            Views.Step1CutView.PendingFile = _state.SourcePath;

        // ---- ① 粗剪 ----
        _state.Plan = m.Cut.Plan;
        if (m.Cut.Options != null) _state.LastOptions = m.Cut.Options;
        _state.Config.LastPreset = m.Cut.Preset;

        // ---- ② 精剪 ----
        // 完整块列表 → 内存快照；精剪页会照着它还原时间轴
        _state.TimelineSnapshot = (m.FineCut.Clips ?? new List<ClipPart>()).Select(Clone).ToList();
        _state.TimelineSourcePath = m.FineCut.SourcePath ?? "";
        _state.TimelinePlayheadSec = m.FineCut.PlayheadSec;
        _state.TimelineViewStartSec = m.FineCut.ViewStartSec;
        _state.TimelineSecPerPixel = m.FineCut.SecPerPixel;

        // 【只还原"属于当前源文件"的时间轴】
        //   档案里的源文件路径可能已经被换过（换素材后没清时间轴，或者手工改过档案）。
        //   一旦对不上，这份版面就是废的 —— 还原了反而危险：精剪页会拿旧区间
        //   去当新素材的剪辑决策。所以这里直接不认（旧块仍留在档案里，没丢）。
        bool timelineMatchesSource =
            _state.TimelineSnapshot.Count > 0 &&
            (_state.TimelineSourcePath.Length == 0 ||
             string.Equals(_state.TimelineSourcePath, _state.SourcePath, StringComparison.OrdinalIgnoreCase));

        _state.PendingTimelineSnapshot = timelineMatchesSource ? _state.TimelineSnapshot : null;

        // 精剪是否接管下游：按存档里的标记恢复；没接管就让下游继续用粗剪
        _state.FineCutKeeps = m.FineCut.Applied && timelineMatchesSource
            ? _state.TimelineSnapshot
                .OrderBy(c => c.TimelineStartSec)
                .Where(c => c.SourceEndSec - c.SourceStartSec > 0.01)
                .Select(c => new FFmpegExporter.Range(c.SourceStartSec, c.SourceEndSec))
                .ToList()
            : null;

        // ---- ③ 分镜 ----
        _state.ScriptText = m.Shots.ScriptText ?? "";
        _state.Shots = m.Shots.Rows ?? new List<ShotRow>();
        _state.ShotDraft = (m.Shots.DraftRows ?? new List<ShotRow>())
                           .Select(WorkbenchState.CloneShot).ToList();
        _state.ShotCommittedAt = m.Shots.CommittedAt ?? "";
        _state.ShotSeq = m.Shots.NextShotSeq;

        // ★ 老档案没有 Id / 草稿 —— 这里补齐，纯加字段，不动任何文件名、不动任何产物。
        //   顺序也讲究：先补 ID 再补草稿（发号器要先把老镜头都号上，新发的才不会撞）。
        _state.EnsureShotIds(_state.Shots);
        if (_state.ShotDraft.Count == 0 && _state.Shots.Count > 0)
            _state.ShotDraft = _state.Shots.Select(WorkbenchState.CloneShot).ToList();
        _state.EnsureShotIds(_state.ShotDraft);

        // 发号器兜底：老档案 ShotSeq = 0，但镜头表里已经有 ID 了 ——
        // 不修的话下一个新镜会发 s01，跟既有 ID 撞上（身份就乱了）。
        _state.ShotSeq = Math.Max(_state.ShotSeq, MaxSeqOf(_state.Shots, _state.ShotDraft));

        _state.ShotRequirement = m.Shots.Requirement ?? "";
        _state.ShotSplitMode = m.Shots.SplitMode;
        _state.ShotPaceSec = m.Shots.PaceSec > 0 ? m.Shots.PaceSec : 6.0;
        _state.FinalScriptSource = m.Shots.ScriptSource ?? "";
        _state.FinalScriptDoc = m.Shots.FinalScript;

        // ---- ④⑤ 渲染 / 导出 ----
        _state.RenderOutputDir = m.Output.RenderDir ?? "";
        _state.LastExportPath = m.Output.LastExportPath ?? "";
    }

    /// <summary>
    /// 两份镜头表里最大的那个 ID 序号（s07 → 7）。
    /// 用来给发号器兜底：老档案没有 NextShotSeq，不修的话新镜会发 s01 跟既有 ID 撞上。
    /// </summary>
    private static int MaxSeqOf(params List<ShotRow>[] tables)
    {
        int max = 0;
        foreach (var t in tables)
            foreach (var r in t)
            {
                string id = r.Id ?? "";
                if (id.Length > 1 && (id[0] == 's' || id[0] == 'S') &&
                    int.TryParse(id.AsSpan(1), out int n) && n > max) max = n;
            }
        return max;
    }

    /// <summary>
    /// 清空运行时状态。打开新项目 / 关闭项目时都要先清，
    /// 否则会出现"项目 A 的数据串到项目 B 里"。
    /// </summary>
    private void ClearState()
    {
        _state.SourcePath = "";
        _state.Pcm = null;
        _state.Probe = new MediaProbe();
        _state.Transcript = null;
        _state.TranscriptPath = "";

        _state.Plan = null;
        _state.LastOptions = AutoCutOptions.Aggressive();

        _state.FineCutKeeps = null;
        _state.TimelineSnapshot = new List<ClipPart>();
        _state.TimelineSourcePath = "";
        _state.TimelinePlayheadSec = 0;
        _state.TimelineViewStartSec = 0;
        _state.TimelineSecPerPixel = 0.05;
        _state.PendingTimelineSnapshot = null;

        _state.ScriptText = "";
        _state.Shots = new List<ShotRow>();
        _state.ShotDraft = new List<ShotRow>();
        _state.ShotCommittedAt = "";
        _state.ShotSeq = 0;
        _state.ShotRequirement = "";
        // ★ 切法取「上次用的那次」（默认 2=语义合并），不写死 0。
        //   写死 0 的后果：每新建一个项目，分镜页的单选都亮在「一句一镜」，
        //   用户得手动拨一次；而一旦误点「生成镜头切分」，刚合并好的块会被冲成一句一镜。
        //   注意本方法也被 ApplyToState 在开头调用 —— 已存在的项目随后会被存档值覆盖，
        //   所以这里只是「新项目初值」，不会把老项目打回默认。
        _state.ShotSplitMode = _state.Config.LastShotSplitMode;
        _state.ShotPaceSec = 6.0;
        _state.FinalScriptSource = "";
        _state.FinalScriptDoc = null;

        _state.RenderOutputDir = "";
        _state.LastExportPath = "";
    }
}
