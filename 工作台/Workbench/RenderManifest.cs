// ============================================================
// RenderManifest.cs - 渲染产物清单（V1.1.0 / T-16 新增）
// ============================================================
//
// 【它是干什么的：回答"磁盘上这个 MP4 到底是谁的产物"】
//
//   以前渲染页判"这一镜渲好没"，只看一件事：shot_007.mp4 在不在。
//   可"文件在"不等于"内容对"：
//     · 你拆了一镜，第 7 位现在坐的是"原第 6 镜的后半"，而 shot_007.mp4
//       还是旧第 7 镜的画面 —— 文件名对得上，内容全错；
//     · 你改了台词/画面构思，MP4 是改之前渲的 —— 文件在，但要重渲。
//   这两种情况以前**完全看不出来**。
//
//   所以要一份清单，记下"渲出来那一刻，这一镜长什么样"。
//
// 【★ 键必须是永久 ID，不能是镜号】
//   镜号会随拆合重排。用镜号当键，拆一次镜头这份清单自己就过期了；
//   用 ID 当键，镜号怎么变它都对得上（file 字段跟着迁移一起改）。
//
// 【为什么指纹要含时长】
//   拆 / 合会直接改 StartSec/EndSec —— 时长一变，画面必然要重渲。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench;

/// <summary>清单里的一条：某个镜头 ID 的产物情况。</summary>
public sealed class RenderManifestEntry
{
    /// <summary>渲染那一刻的镜号。★ 仅供人看，**会过期**（拆合之后就对不上了）。</summary>
    public int Shot { get; set; }

    /// <summary>渲染完成时间（ISO 字符串）。</summary>
    public string RenderedAt { get; set; } = "";

    /// <summary>渲染那一刻镜头内容的指纹。对不上 = 内容已变，要重渲。</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>产物文件名（如 shot_007.mp4）。迁移改名时跟着一起改。</summary>
    public string File { get; set; } = "";
}

/// <summary>
/// 产物清单的读写、指纹计算、基线回扫、迁移改名。
/// </summary>
public static class RenderManifest
{
    public const string FileName = "render-manifest.json";

    public static string PathOf(string outputDir) => Path.Combine(outputDir, FileName);

    /// <summary>某个镜号的产物文件名（全项目统一口径，别再各写各的）。</summary>
    public static string Mp4NameOf(int index) => $"shot_{index:D3}.mp4";

    /// <summary>
    /// 镜头内容指纹。改了参与拼接的任何一项，指纹就变 → 该重渲了。
    /// 不含 Id/Index（那是"谁"和"排第几"，不是内容）。
    ///
    /// ★ V1.10.15：**插片行**额外拼入插片原声两项（<c>InsertAudio</c> / <c>InsertVolumeDb</c>）——
    ///   开关一拨，产物的音轨就不一样了（静音 ↔ 有原声、音量大小）。
    ///   不加进去的话，用户拨了开关却看到"已就绪"，出片时才发现声音不对。
    ///
    /// ★ 但**普通镜头绝不参与这两项** —— 否则一次版本升级会把所有历史产物
    ///   全判成"待重渲"（25 个镜头集体变橙，纯属虚惊）。
    ///   「内容没变就不该重渲」这条比"指纹算法整齐"重要。
    /// </summary>
    public static string FingerprintOf(ShotRow r)
    {
        string core = string.Join('\u0001',
            r.Text ?? "", r.Description ?? "", r.Motion ?? "", r.Image ?? "",
            r.DurationSec.ToString("F3"));

        if (!r.IsInsert) return WorkbenchState.Fnv1a(core);

        return WorkbenchState.Fnv1a(core + '\u0001'
            + (r.InsertAudio ? "aud" : "-") + '\u0001'
            + r.InsertVolumeDb.ToString("F2"));
    }

    // ========================================================
    // 读写
    // ========================================================

    /// <summary>读清单。文件不在 / 坏了 → 空表（调用方据此走"没有清单"的老口径）。</summary>
    public static Dictionary<string, RenderManifestEntry> Load(string outputDir)
    {
        var map = new Dictionary<string, RenderManifestEntry>(StringComparer.Ordinal);
        try
        {
            string p = PathOf(outputDir);
            if (!File.Exists(p)) return map;

            var got = ContractJson.Deserialize<Dictionary<string, RenderManifestEntry>>(
                          File.ReadAllText(p, Encoding.UTF8));
            if (got == null) return map;

            foreach (var kv in got)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null) map[kv.Key] = kv.Value;
        }
        catch { /* 清单坏了就当没有 —— 绝不能因为它读不出来就让渲染页整页报错 */ }
        return map;
    }

    /// <summary>写清单（原子）。写不进去也不抛 —— 它只是"账本"，丢了不影响出片。</summary>
    public static void Save(string outputDir, Dictionary<string, RenderManifestEntry> map)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            string p = PathOf(outputDir);
            string tmp = p + ".tmp";
            // ★ 必须无 BOM：这份清单是给**外部工具**（渲染执行者 / Python / node）读的，
            //   带 BOM 时 Python `json.load` 直接抛 "Unexpected UTF-8 BOM"（2026-10-01 实机查出）。
            File.WriteAllText(tmp, ContractJson.Serialize(
                map.OrderBy(k => k.Key, StringComparer.Ordinal)
                   .ToDictionary(k => k.Key, k => k.Value)), ContractJson.Utf8NoBom);
            if (File.Exists(p)) File.Replace(tmp, p, null);
            else File.Move(tmp, p);
        }
        catch { }
    }

    // ========================================================
    // 登记 / 回扫
    // ========================================================

    /// <summary>
    /// 把一镜登记为"刚渲好"（渲染完成、单镜重渲完成后调）。
    /// </summary>
    /// <param name="renderedAt">
    /// 产物**实际**的渲染时刻。默认=现在（工作台自己刚渲完的那一刻）。
    /// ★ V1.10.10 起允许显式传：回扫老产物 / 认领外部渲好的产物时，
    ///   要写产物文件的写入时间 —— 写成"现在"会让"这一镜是不是被重渲过"的判断失真。
    /// </param>
    public static void Record(Dictionary<string, RenderManifestEntry> map, ShotRow r,
                              string fileName, DateTime? renderedAt = null)
    {
        if (string.IsNullOrWhiteSpace(r.Id)) return;
        map[r.Id] = new RenderManifestEntry
        {
            Shot = r.Index,
            RenderedAt = (renderedAt ?? DateTime.Now).ToString("o"),
            Fingerprint = FingerprintOf(r),
            File = fileName,
        };
    }

    /// <summary>
    /// ★ 老项目第一次打开时建基线。
    ///
    /// 【为什么不直接在"有 MP4 而且清单里没这条"时报「待重渲」】
    ///   像「思辨与立场」那种早就有 51 个 MP4、但从来没有清单的项目，
    ///   一打开就把 51 镜全标成"待重渲"，用户会以为软件疯了 ——
    ///   而且那不是事实，只是"我们不知道它是什么时候渲的"。
    ///   所以第一次见到产物就把它当基线记下来（内容取当前镜头表）。
    ///   这是一次性的，之后任何改动都会被指纹抓到。
    /// </summary>
    public static int BackfillMissing(string outputDir,
                                      IEnumerable<ShotRow> shots,
                                      Dictionary<string, RenderManifestEntry> map)
    {
        int n = 0;
        foreach (var r in shots)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) continue;
            if (map.ContainsKey(r.Id)) continue;

            string file = Mp4NameOf(r.Index);
            string full = Path.Combine(outputDir, file);
            if (!File.Exists(full)) continue;

            // ★ V1.10.10：基线时刻取**产物文件自己的写入时间**，不取"现在"。
            //   取"现在"会把基线时间戳顶到所有产物之后，于是"这个 MP4 比清单新"
            //   这条判据（ReclaimRerendered 靠它认领外部渲好的产物）一开始就失效。
            Record(map, r, file, File.GetLastWriteTime(full));
            n++;
        }
        return n;
    }

    // ========================================================
    // 迁移改名
    // ========================================================

    /// <summary>
    /// 交接 / 迁移之后同步清单：
    ///   · 还在的镜头 → 把 file 改成新镜号的名字（镜号可能变了）
    ///   · 已经没了的镜头（被合并掉的）→ 删掉它的条目
    /// 内容指纹**故意不动** —— 内容没变，变了的话后面自然会判"待重渲"。
    /// </summary>
    public static void Remap(Dictionary<string, RenderManifestEntry> map,
                             IReadOnlyDictionary<string, int> newIndexById,
                             IReadOnlyCollection<string> removedIds)
    {
        foreach (var id in removedIds) map.Remove(id);

        foreach (var kv in map)
        {
            if (!newIndexById.TryGetValue(kv.Key, out int idx)) continue;
            kv.Value.Shot = idx;
            kv.Value.File = Mp4NameOf(idx);
        }
    }
}
