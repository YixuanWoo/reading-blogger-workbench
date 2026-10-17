// ============================================================
// RecordingStore.cs - 录音台账（谁录的、绑了哪几句、还在不在用）
// ============================================================
//
// 【一条记录 = 一个 wav + 一个文案区间】
//   录音（物理单位）：takes\t001.wav，一次"空格到空格"就是一条
//   绑定（逻辑单位）：第 12–14 句（句子编号见 ScriptDocument）
//   两者当场配对，配错了可以改 —— 改了只动台账，wav 一个字节都不动。
//
// 【★ 重录不拼接、不覆盖】
//   新录音和旧录音的范围重叠时，旧的那条**不删**，只是标 Dead（作废）：
//     · wav 还在 —— 反悔了能一键恢复
//     · 不进"已录"映射 —— 下游取句子时永远取最新的活的那条
//   这样"重录"永远是"再录一条新的"，不存在剪接、不存在接缝。
//
// 【落盘位置】
//   <项目>\素材\录音\record.json        ← 本文件（台账 + 两个开关）
//   <项目>\素材\录音\takes\t001.wav     ← 原始录音，不裁不处理
//
//   为什么放在 素材\录音\ 而不是 stages\：
//     录音是**原始素材**，跟导入的视频/图片同级；转写稿（stages\01-transcript）
//     是它的下游产物。素材区跟项目一起拷走 / 归档，谁都不会漏。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench.RecordingDesk;

/// <summary>一条录音。</summary>
public sealed class RecordingTake
{
    /// <summary>编号（t001…）。同时是 wav 文件名。</summary>
    public string Id { get; set; } = "";

    /// <summary>绑定的第一句（句子全局编号，含）。</summary>
    public int A { get; set; }

    /// <summary>绑定的最后一句（含）。</summary>
    public int B { get; set; }

    /// <summary>wav 相对路径（相对"素材\录音"），如 takes/t001.wav。</summary>
    public string File { get; set; } = "";

    /// <summary>录音时长（秒）。</summary>
    public double DurSec { get; set; }

    /// <summary>峰值电平（dBFS）。低于 -50 基本就是没声音（WO Mic 掉线就是这样）。</summary>
    public double PeakDb { get; set; } = -96;

    /// <summary>作废：被后来的录音整段/部分盖掉。wav 留着，可以恢复。</summary>
    public bool Dead { get; set; }

    /// <summary>录这条的时间（本地时间，yyyy-MM-dd HH:mm:ss）。</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>这条覆盖了多少句（含两端）。</summary>
    public int SentenceCount => Math.Max(0, B - A + 1);

    public bool Covers(int index) => index >= A && index <= B;

    public bool Overlaps(int a, int b) => !(B < a || A > b);
}

/// <summary>record.json 的根。</summary>
public sealed class RecordFile
{
    public int Version { get; set; } = 1;

    /// <summary>录这批音时稿子的指纹。对不上 → 提醒"稿子改过了"。</summary>
    public string ScriptStamp { get; set; } = "";

    /// <summary>上次用的麦克风名字（下次自动选回它）。</summary>
    public string LastDevice { get; set; } = "";

    /// <summary>录完自动试听（默认开）。</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>没选选区时，录完按语速猜范围（默认开）。</summary>
    public bool AutoGuess { get; set; } = true;

    /// <summary>全部录音（含作废的）。顺序 = 录制顺序。</summary>
    public List<RecordingTake> Takes { get; set; } = new();
}

/// <summary>一个项目的录音台账。负责读写、编号、重叠处理、句子映射。</summary>
public sealed class RecordingStore
{
    /// <summary>"素材\录音"</summary>
    public string Dir { get; }

    /// <summary>"素材\录音\takes"</summary>
    public string TakesDir { get; }

    /// <summary>record.json 全路径。</summary>
    public string JsonPath { get; }

    public RecordFile Data { get; private set; } = new();

    private RecordingStore(string projectDir)
    {
        Dir = Path.Combine(projectDir, "素材", "录音");
        TakesDir = Path.Combine(Dir, "takes");
        JsonPath = Path.Combine(Dir, "record.json");
    }

    /// <summary>把一个项目的台账读进来（文件不存在 = 全新，不报错）。</summary>
    public static RecordingStore Load(string projectDir)
    {
        var s = new RecordingStore(projectDir);
        var f = ContractJson.ReadFile<RecordFile>(s.JsonPath);
        if (f != null)
        {
            f.Takes ??= new List<RecordingTake>();
            s.Data = f;
        }
        return s;
    }

    /// <summary>写回 record.json（无 BOM，先写临时文件再替换，防写一半断电）。</summary>
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(TakesDir);

        string tmp = JsonPath + ".tmp";
        ContractJson.WriteFile(tmp, Data);

        try
        {
            if (File.Exists(JsonPath))
                System.IO.File.Replace(tmp, JsonPath, null);
            else
                System.IO.File.Move(tmp, JsonPath);
        }
        catch
        {
            // 替换失败（杀软占用等）→ 直接覆盖，至少别丢数据
            try { System.IO.File.Copy(tmp, JsonPath, true); System.IO.File.Delete(tmp); } catch { }
        }
    }

    /// <summary>活的（没被作废的）录音。</summary>
    public IEnumerable<RecordingTake> Alive => Data.Takes.Where(t => !t.Dead);

    /// <summary>下一条的编号：t001 / t002 …（取最大 +1，删过也不会撞号）。</summary>
    public string NextId()
    {
        int max = 0;
        foreach (var t in Data.Takes)
        {
            if (t.Id.Length > 1 && t.Id[0] == 't' && int.TryParse(t.Id[1..], out int n) && n > max)
                max = n;
        }
        return "t" + (max + 1).ToString("000");
    }

    /// <summary>某条录音的 wav 绝对路径。</summary>
    public string WavPath(RecordingTake t)
        => Path.Combine(Dir, t.File.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>新录音要写的 wav 绝对路径。</summary>
    public string NewWavPath(string id) => Path.Combine(TakesDir, id + ".wav");

    /// <summary>
    /// 把一条录音设为"活"的，并把跟它重叠的其他活录音标成作废。
    /// 恢复作废录音也走这里 —— 规则只有一个：**每句只认最新确认的那条**。
    /// </summary>
    public void Activate(RecordingTake take)
    {
        foreach (var t in Data.Takes)
            if (!ReferenceEquals(t, take) && !t.Dead && t.Overlaps(take.A, take.B))
                t.Dead = true;

        take.Dead = false;
    }

    /// <summary>句子 → 录音编号（没有录过的句子不在字典里）。</summary>
    public Dictionary<int, string> BuildDoneMap(int sentenceCount)
    {
        var map = new Dictionary<int, string>();
        foreach (var t in Alive)
        {
            int a = Math.Max(0, t.A), b = Math.Min(sentenceCount - 1, t.B);
            for (int i = a; i <= b; i++) map[i] = t.Id;
        }
        return map;
    }

    /// <summary>
    /// 学到的语速（字/秒）。只用活录音、且长度合理的那些算 ——
    /// 一条 30 秒却没几个字的录音会把平均值拖垮，所以夹在 [2, 8] 里。
    /// </summary>
    public double LearnedRate(ScriptDocument doc)
    {
        double chars = 0, secs = 0;
        foreach (var t in Alive)
        {
            if (t.DurSec < 2) continue;
            int c = doc.CharsBetween(t.A, t.B);
            if (c <= 0) continue;
            chars += c;
            secs += t.DurSec;
        }
        if (secs <= 0) return 4.3;              // 还没录过：用一个中文口播的常见值
        double r = chars / secs;
        return Math.Clamp(r, 2.0, 8.0);
    }

    /// <summary>当前有没有任何一条活录音。</summary>
    public bool HasAlive => Data.Takes.Any(t => !t.Dead);
}
