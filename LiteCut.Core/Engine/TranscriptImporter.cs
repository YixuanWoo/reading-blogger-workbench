// ============================================================
// TranscriptImporter.cs - 把外部拿来的转写稿读进来
// ============================================================
//
// 【为什么不能直接用反序列化】
//
//   标准流程是：
//     外部 Agent 按标准产出 transcript.json → 我们反序列化 → 直接用
//
//   但"外部 Agent"这四个字意味着**格式偏差是常态，不是意外**：
//     它把词的字段名写成 w 而不是 text
//     它把句子编号写成 id 而不是 index
//     它把音频路径塞进一个嵌套的 source 对象里
//     它给的时间戳是字符串而不是数字
//   ……每一条都会让标准的反序列化失败或读出空对象。
//
//   更糟的是失败方式的隐蔽性：JSON 语法完全合法，反序列化"成功"，
//   但 Segments 是空的、每个词的文字是空的。
//   程序不报错，但语义层等于没启用 —— 用户还以为"AI 说它能用"。
//
// 【所以这里做三件事】
//
//   ① 先按标准格式试
//   ② 失败或读出空内容 → 走宽松解析，把常见的别名映射过来
//   ③ 无论哪条路进来，都做一次"清洗"，把明显的坏数据修掉
//
//   并且每一步都记进 Notes —— 因为"我替你改了什么"必须能交代清楚，
//   否则用户永远不知道为什么结果跟他预期的不一样。
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LiteCut.Core.Contracts;

namespace LiteCut.Core.Engine;

/// <summary>导入结果。除了稿子本身，还要交代"这一路做了什么修正"。</summary>
public sealed class TranscriptLoadResult
{
    /// <summary>解析出来的稿子。失败时为 null。</summary>
    public Transcript? Transcript { get; set; }

    /// <summary>是否成功（至少要有句子）。</summary>
    public bool Ok => Transcript != null && Transcript.Segments != null && Transcript.Segments.Count > 0;

    /// <summary>过程记录。界面上应该展示给用户，不要藏着。</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>转写稿导入。</summary>
public static class TranscriptImporter
{
    /// <summary>从一个 JSON 文件导入。</summary>
    public static TranscriptLoadResult LoadFile(string path)
    {
        var res = new TranscriptLoadResult();
        if (!File.Exists(path))
        {
            res.Notes.Add($"文件不存在：{path}");
            return res;
        }

        string json = File.ReadAllText(path, Encoding.UTF8);
        return Load(json);
    }

    /// <summary>从一段 JSON 文本导入。</summary>
    public static TranscriptLoadResult Load(string json)
    {
        var res = new TranscriptLoadResult();
        if (string.IsNullOrWhiteSpace(json))
        {
            res.Notes.Add("内容是空的");
            return res;
        }

        // ---- 第 0 步：先认「原始 ASR」格式 ----
        //
        // 【为什么必须放在最前面】
        //   豆包、剪映这类工具导出的原始 ASR 长这样：
        //     { "subtitles": [ {"start_time":5.52,"end_time":14.18,"subtitle_text":"..."} ] }
        //   它和我们的标准契约字段名完全不同，但**它才是最有价值的稿子** ——
        //   因为它保留了每一遍重录、每一次卡壳，而"整理后的成稿"把这些全删了。
        //
        //   实测（《伪史论》658s）：
        //     喂整理后的 49 句  → 成片 486.84s（语义层几乎没干活，只剪出 4 处填充词）
        //     喂原始 277 片段   → 成片 430.44s（抓到 17 处重录 + 10 处卡壳）
        //   所以：只要检测出这是原始 ASR，就优先按它读，不要被后面的宽松模式抢走。
        Transcript? t = TryParseRawAsr(json, res.Notes);

        // ---- 第一步：按标准格式试 ----
        if (t == null)
        {
            try
            {
                t = JsonSerializer.Deserialize<Transcript>(json, ContractJson.Options);
            }
            catch (Exception ex)
            {
                res.Notes.Add($"标准格式解析失败（{ex.GetType().Name}），改走宽松模式");
            }

            bool usable = t != null && t.Segments != null && t.Segments.Count > 0
                          && t.Segments.All(s => !string.IsNullOrEmpty(s.Text) || (s.Words?.Count ?? 0) > 0);

            if (usable)
            {
                res.Notes.Add("标准格式解析成功");
            }
            else
            {
                if (t != null) res.Notes.Add("标准读出来了但内容是空的，改走宽松模式");
                var loose = ParseLoose(json, res.Notes);
                if (loose != null) t = loose;
            }
        }

        if (t == null)
        {
            res.Notes.Add("宽松模式也解析不出来 —— 这份文件可能不是转写稿");
            return res;
        }

        // ---- 第三步：清洗 ----
        Sanitize(t, res.Notes);

        res.Transcript = t;
        return res;
    }

    // ============================================================
    // 原始 ASR（豆包 / 剪映 / 各类语音识别工具的原生导出）
    // ============================================================

    /// <summary>
    /// 认出「原始 ASR」格式并转成标准 Transcript。认不出来就返回 null，交给后面的路。
    ///
    /// 【为什么单独开一条路，而不是塞进宽松模式】
    ///   原始 ASR 的字段名（subtitles / start_time / subtitle_text）和我们契约完全不搭，
    ///   宽松模式是靠"挨个猜字段名"工作的，很容易把数组认错、把顺序搞反。
    ///   这种格式**必须是**明确的、可判定的，所以单独写死一条路更稳。
    ///
    /// 【怎么判定】
    ///   顶层有 subtitles（或 utts / segments / data）数组，且数组元素同时带
    ///   「开始时间」「结束时间」「文本」三个字段 —— 三条都对上才认，避免误认别的文件。
    /// </summary>
    private static Transcript? TryParseRawAsr(string json, List<string> notes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception)
        {
            return null;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // 数组可能在顶层，也可能嵌一层
            JsonElement? arr = Arr(root, "subtitles", "subtitle", "utts", "utterances",
                                        "sentences", "data", "result", "results", "list");
            if (arr == null) return null;

            var list = arr.Value;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return null;

            // 先看第一个元素能不能认出三个字段，认不出就说明不是这个格式
            var first = list[0];
            if (first.ValueKind != JsonValueKind.Object) return null;

            string? txt0 = Str(first, "subtitle_text", "text", "content", "words", "sentence", "transcript");
            double? s0 = Num(first, "start_time", "start", "begin", "startTime", "bt", "from");
            double? e0 = Num(first, "end_time", "end", "finish", "endTime", "et", "to");
            if (txt0 == null || s0 == null || e0 == null) return null;

            var t = new Transcript();
            t.Segments = new List<TranscriptSegment>();

            int idx = 0;
            int skipped = 0;
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) { skipped++; continue; }

                string? txt = Str(el, "subtitle_text", "text", "content", "words", "sentence", "transcript");
                double? a = Num(el, "start_time", "start", "begin", "startTime", "bt", "from");
                double? b = Num(el, "end_time", "end", "finish", "endTime", "et", "to");
                if (txt == null || a == null || b == null) { skipped++; continue; }

                // ★ 有些工具给的是毫秒整数，这里统一换算成秒
                double sa = a.Value, sb = b.Value;
                if (sa > 100000 && sb > 100000) { sa /= 1000.0; sb /= 1000.0; }

                t.Segments.Add(new TranscriptSegment
                {
                    Index = idx++,
                    StartSec = sa,
                    EndSec = sb,
                    Text = txt,
                });
            }

            if (t.Segments.Count == 0) return null;

            // 时长：优先用稿子里写的，没有就用最后一句的结束时间
            double? dur = Num(root, "duration", "audioDurationSec", "audio_duration", "total_duration");
            t.AudioDurationSec = dur ?? (t.Segments[^1].EndSec);
            t.Engine = "raw-asr";
            t.Language = "zh";

            notes.Add($"识别为【原始 ASR】格式：{t.Segments.Count} 个片段"
                      + (skipped > 0 ? $"（跳过 {skipped} 个残缺项）" : ""));
            notes.Add("  ↑ 原始 ASR 才是对的原料：它保留了每一遍重录和卡壳。"
                      + "若你手上还有「整理后的成稿」，请不要用它 —— 重录已被提前删掉，算法无从判断。");

            return t;
        }
    }

    // ============================================================
    // 宽松解析
    // ============================================================

    /// <summary>
    /// 容忍各种常见写法，把能认出来的都认出来。
    ///
    /// 每多认一种写法，就少一次"用户兴冲冲导入，结果什么都没发生"。
    /// </summary>
    private static Transcript? ParseLoose(string json, List<string> notes)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { notes.Add($"连 JSON 都不是合法的：{ex.Message}"); return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                notes.Add("顶层不是对象");
                return null;
            }

            var t = new Transcript();

            t.SchemaVersion = Str(root, "schemaVersion", "version") ?? "1.0";
            t.Language = Str(root, "language", "lang") ?? "zh";
            t.Engine = Str(root, "engine", "asr") ?? "";
            t.Model = Str(root, "model") ?? "";

            // 音频路径：可能直接给，也可能埋在 source / meta 里
            t.AudioPath = Str(root, "audioPath", "audio", "audioFile")
                       ?? Str(Nested(root, "source", "meta", "audio"), "path", "file")
                       ?? "";

            // 总时长：同上
            double? dur = Num(root, "audioDurationSec", "duration", "durationSec", "totalSec")
                       ?? Num(Nested(root, "source", "meta", "audio"), "durationSec", "duration");
            t.AudioDurationSec = dur ?? 0;

            t.FullText = Str(root, "fullText", "text") ?? "";

            // segments 也可能叫别的
            JsonElement? segEl = Arr(root, "segments", "segs", "utterances", "results", "sentences");
            if (segEl == null)
            {
                notes.Add("找不到句子列表（试过 segments / utterances / sentences 等名字）");
                return null;
            }

            int idx = 0;
            foreach (var el in segEl.Value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var seg = new TranscriptSegment();

                // 编号：id 可能是 "t0001" 也可能是 1
                JsonElement idEl;
                if (el.TryGetProperty("index", out idEl) || el.TryGetProperty("id", out idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number) seg.Index = idEl.GetInt32();
                    else if (idEl.ValueKind == JsonValueKind.String)
                    {
                        string digits = new string(idEl.GetString()!.Where(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out int n)) seg.Index = n;
                    }
                }
                else seg.Index = idx;

                seg.StartSec = Num(el, "startSec", "start", "begin", "startTime") ?? 0;
                seg.EndSec = Num(el, "endSec", "end", "stop", "endTime") ?? 0;
                seg.Text = Str(el, "text", "content", "sentence") ?? "";

                JsonElement? wordEl = Arr(el, "words", "tokens", "chars");
                if (wordEl != null)
                {
                    foreach (var w in wordEl.Value.EnumerateArray())
                    {
                        if (w.ValueKind != JsonValueKind.Object) continue;
                        seg.Words.Add(new TranscriptWord
                        {
                            // 词的字段名是最容易写歪的一个地方
                            Text = Str(w, "text", "w", "word", "token", "content") ?? "",
                            StartSec = Num(w, "startSec", "start", "begin") ?? 0,
                            EndSec = Num(w, "endSec", "end", "stop") ?? 0,
                            Confidence = Num(w, "confidence", "conf", "prob"),
                        });
                    }
                }

                t.Segments.Add(seg);
                idx++;
            }

            if (t.Segments.Count == 0)
            {
                notes.Add("句子列表是空的");
                return null;
            }

            return t;
        }
    }

    // ============================================================
    // 清洗
    // ============================================================

    /// <summary>
    /// 把明显的坏数据修掉。
    ///
    /// 注意原则：**只修"确定是坏的"，不猜"可能更好的"**。
    /// 比如时间倒挂可以用交换修好（确定是错的），
    /// 但"这一句的时间看着有点早"这类怀疑就不许动。
    /// </summary>
    private static void Sanitize(Transcript t, List<string> notes)
    {
        if (t.Segments == null) return;

        int missingWordTime = 0, missingText = 0, timeSwap = 0, reindex = 0;

        // 编号全是 0 → 重编（否则界面上定位"第几句"会很难用）
        if (t.Segments.All(s => s.Index == 0) && t.Segments.Count > 1)
        {
            for (int i = 0; i < t.Segments.Count; i++) t.Segments[i].Index = i;
            reindex = t.Segments.Count;
        }

        foreach (var seg in t.Segments)
        {
            // 句子没有文字但有词 → 拼出来（兜住"只给词"的转写引擎）
            if (string.IsNullOrEmpty(seg.Text) && seg.Words != null && seg.Words.Count > 0)
            {
                seg.Text = string.Concat(seg.Words.Select(w => w.Text));
                missingText++;
            }

            // 时间倒挂 → 交换。这是明确的错误，修它是安全的。
            if (seg.EndSec < seg.StartSec)
            {
                (seg.StartSec, seg.EndSec) = (seg.EndSec, seg.StartSec);
                timeSwap++;
            }

            // 词：处理缺失与倒挂
            if (seg.Words != null && seg.Words.Count > 0)
            {
                foreach (var w in seg.Words)
                {
                    if (w.EndSec < w.StartSec) { (w.StartSec, w.EndSec) = (w.EndSec, w.StartSec); timeSwap++; }
                }

                // 有些词完全没有时间码 —— 按字数把整句的时长分摊下去。
                // 这样后面至少能得到一个可用的位置估计。
                // （CharTimeMap 里也有类似兜底，这里先做一层的意义是：
                //   补齐之后 words 之间才是单调的，下游逻辑更好写。）
                if (seg.Words.Any(w => w.EndSec <= w.StartSec) && seg.EndSec > seg.StartSec)
                {
                    int totalChars = seg.Words.Sum(w => Math.Max(1, w.Text.Length));
                    double cursor = seg.StartSec;
                    double span = seg.EndSec - seg.StartSec;
                    foreach (var w in seg.Words)
                    {
                        if (w.EndSec > w.StartSec) { cursor = w.EndSec; continue; }
                        double dur = span * Math.Max(1, w.Text.Length) / totalChars;
                        w.StartSec = cursor;
                        w.EndSec = cursor + dur;
                        cursor = w.EndSec;
                    }
                    missingWordTime++;
                }
            }
        }


        // 按时间排序 —— 顺序错了后面所有相邻比较都失去意义
        bool sorted = false;
        for (int i = 1; i < t.Segments.Count; i++)
        {
            if (t.Segments[i].StartSec < t.Segments[i - 1].StartSec) { sorted = true; break; }
        }
        if (sorted)
        {
            t.Segments.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
            notes.Add("句子顺序本来是乱的，已按时间重排");
        }

        if (missingWordTime > 0) notes.Add($"{missingWordTime} 句的词缺时间码，已按句长分摊");
        if (reindex > 0) notes.Add($"{reindex} 句缺编号，已重编");
        if (missingText > 0) notes.Add($"{missingText} 句只有词没有整句，已拼出整句");
        if (timeSwap > 0) notes.Add($"{timeSwap} 处时间倒挂（结束早于开始），已交换");

        if (string.IsNullOrEmpty(t.FullText))
            t.FullText = string.Concat(t.Segments.Select(s => s.Text));
    }

    // ============================================================
    // 取值小工具
    // ============================================================

    private static string? Str(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        }
        return null;
    }

    private static double? Num(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;

            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();

            // 有些引擎会把秒数写成字符串（"12.34"），也认
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString() ?? "";
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    return d;
            }
        }
        return null;
    }

    private static JsonElement? Arr(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array)
                return v;
        }
        return null;
    }

    /// <summary>找嵌套对象：source.meta.audio 这种。</summary>
    private static JsonElement Nested(JsonElement el, params string[] names)
    {
        JsonElement cur = el;
        foreach (var n in names)
        {
            if (cur.ValueKind != JsonValueKind.Object) return default;
            if (cur.TryGetProperty(n, out var next)) cur = next;
            else return default;
        }
        return cur;
    }
}
