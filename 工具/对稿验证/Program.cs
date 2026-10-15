// ============================================================
// Program.cs - 「对稿」（ScriptAligner）的无头断言
// ============================================================
// 直接调生产代码跑一遍关键行为。不启动界面、不碰磁盘。
// 用法： cd 工具\对稿验证 && dotnet run -c Release
// 退出码 0 = 全通过，1 = 有失败项。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;

namespace ScriptAlignVerify;

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"  [OK] {name}"); }
        else { _fail++; Console.WriteLine($"  [XX] {name}"); }
    }

    /// <summary>快速搭一份转写稿。</summary>
    static Transcript T(params string[] texts)
    {
        var tr = new Transcript { Language = "zh", Engine = "test" };
        double t = 0;
        int i = 0;
        foreach (var s in texts)
        {
            tr.Segments.Add(new TranscriptSegment
            {
                Index = i++,
                StartSec = t,
                EndSec = t + 2.0,
                Text = s,
            });
            t += 2.0;
        }
        tr.AudioDurationSec = t;
        return tr;
    }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("== ① 解析稿子（ParseScript：去 Markdown 装饰 + 断句） ==");
        var md = "# 标题\n\n**唐代**的美学，它有三个特点。\n\n- 第一点\n- 第二点\n\n> 引用一句。\n";
        var parsed = ScriptAligner.ParseScript(md);
        var texts = parsed.Select(p => p.Text).ToList();
        Check("标题被识别", texts.Contains("标题"));
        Check("加粗符号被去掉", texts.Any(t => t.Contains("唐代的美学") && !t.Contains("*")));
        Check("列表点被去掉", texts.Contains("第一点") && texts.Contains("第二点"));
        Check("引用符号被去掉", texts.Contains("引用一句。"));
        Check("总共 5 句", parsed.Count == 5);

        Console.WriteLine("== ② 顺序对齐：干净念稿 ==");
        var al2 = ScriptAligner.Align(
            T("我们要讲唐代的美学", "它有三个特点", "先看第一个特点"),
            "我们要讲唐代的美学。它有三个特点。先看第一个特点。");
        Check("对齐可用", al2.Usable);
        Check("对齐率 100%", Math.Abs(al2.Check.AlignmentRatio - 1.0) < 1e-6);
        Check("无重录", al2.RetakeIssues.Count == 0);
        Check("无稿外发挥", al2.Check.AdLibCount == 0);

        Console.WriteLine("== ③ 重录：同一句录了三遍（保后不保前） ==");
        var al3 = ScriptAligner.Align(
            T("第一句内容在这里", "第二句内容在这里", "第二句内容在这里", "第二句内容在这里", "第三句内容在这里"),
            "第一句内容在这里。第二句内容在这里。第三句内容在这里。");
        Console.WriteLine("    归属 = [" + string.Join(",", al3.SegmentScriptIndex) + "]");
        Console.WriteLine("    重录段 = [" + string.Join(",", al3.RetakeIssues.Select(r => r.SegmentIndex)) + "]");
        Check("对齐可用", al3.Usable);
        Check("第三段归第 2 句稿", al3.SegmentScriptIndex[2] == 1);
        Check("认出 2 处重录（前两遍）", al3.RetakeIssues.Count == 2);
        Check("保留的是最后一遍（第 4 段未被标重录）",
              al3.RetakeIssues.All(r => r.SegmentIndex != 3));
        Check("重录原因=Retake", al3.RetakeIssues.All(r => r.Reason == CutReason.Retake));

        Console.WriteLine("== ④ 长句被 ASR 切开：不能误判成重录 ==");
        var al4 = ScriptAligner.Align(
            T("开场白第一句", "我们要讲的是唐代的美学", "它有三个特点", "结尾的第三句"),
            "开场白第一句。我们要讲的是唐代的美学它有三个特点。结尾的第三句。");
        Check("对齐可用", al4.Usable);
        Check("切开的两半都归中间那句稿", al4.SegmentScriptIndex[1] == 1 && al4.SegmentScriptIndex[2] == 1);
        Check("互补的两半不算重录", al4.RetakeIssues.Count == 0);

        Console.WriteLine("== ⑤ 稿外发挥（录音有、稿子上没有） ==");
        var al5 = ScriptAligner.Align(
            T("第一句内容在这里", "今天天气真不错啊哈哈哈哈", "第二句内容在这里", "第三句内容在这里"),
            "第一句内容在这里。第二句内容在这里。第三句内容在这里。");
        Check("对齐可用", al5.Usable);
        Check("第 2 段判为稿外发挥", al5.SegmentScriptIndex[1] == -1);
        Check("AdLib 计数 = 1", al5.Check.AdLibCount == 1);
        Check("偏差里有 adLib 一条", al5.Check.Deviations.Count(d => d.Kind == "adLib") == 1);

        Console.WriteLine("== ⑥ 稿上有、没录到（忘词/跳过） ==");
        var al6 = ScriptAligner.Align(
            T("第一句内容在这里", "第三句内容在这里", "第四句内容在这里"),
            "第一句内容在这里。第二句内容在这里。第三句内容在这里。第四句内容在这里。");
        Check("对齐可用", al6.Usable);
        Check("偏差里有 notSpoken 一条", al6.Check.Deviations.Count(d => d.Kind == "notSpoken") == 1);
        Check("没录到的是第 2 句", al6.Check.Deviations.First(d => d.Kind == "notSpoken").Script.Contains("第二句"));

        Console.WriteLine("== ⑦ 稿子跟录音对不上（拿错文件）→ 自动不用 ==");
        var al7 = ScriptAligner.Align(
            T("第一句内容在这里", "第二句内容在这里", "第三句内容在这里"),
            "苹果香蕉橘子很好吃。西瓜菠萝草莓很甜。葡萄荔枝龙眼很香。");
        Check("判定不可用", !al7.Usable);
        Check("不产出重录", al7.RetakeIssues.Count == 0);

        Console.WriteLine("== ⑧ 没稿子 / 没转写稿 → 安静降级 ==");
        var al8 = ScriptAligner.Align(T("随便说点什么"), "");
        Check("没稿子：Available=false", !al8.Available && !al8.Usable);
        var al9 = ScriptAligner.Align(null, "稿子在这里。");
        Check("没转写稿：Available=true 但不可用", al9.Available && !al9.Usable);

        Console.WriteLine("== ⑨ 老行为不变（不传稿子 = 什么都不加） ==");
        // 直接验证「对稿没启用时」的概况对象
        var check = new ScriptCheck();
        Check("默认 ScriptCheck: Available=false, Used=false", !check.Available && !check.Used);

        Console.WriteLine("== ⑩ 端到端：接进 AutoCutEngine（真的会下刀） ==");
        // 造一段"2 秒有声 + 0.4 秒静音"× 5 的假音频（比纯正弦贴近真实：
        // 有停顿，转写对齐的"整体偏移"估计不会退化，静音段也不会被误剪）
        var (pcm, starts) = VoiceTrack(5);
        // 用激进档：它的语义层额度是 0.40，够放下这 4 秒废片。
        // （适中档额度 0.30，会触发护栏只剪掉一半 —— 那是设计如此，不是 bug）
        var opt = AutoCutOptions.Aggressive();

        string[] spoken = { "第一句内容在这里", "第二句内容在这里", "第二句内容在这里", "第二句内容在这里", "第三句内容在这里" };
        var tr = new Transcript { Language = "zh", Engine = "test" };
        for (int i = 0; i < spoken.Length; i++)
            tr.Segments.Add(new TranscriptSegment
            {
                Index = i,
                StartSec = starts[i],
                EndSec = starts[i] + 2.0,
                Text = spoken[i],
            });
        tr.AudioDurationSec = pcm.DurationSec;

        var plan = AutoCutEngine.Analyze(pcm, opt, tr, "verify", "fake.wav",
                                         "第一句内容在这里。第二句内容在这里。第三句内容在这里。");
        Check("plan.Script 已填", plan.Script != null && plan.Script.Available);
        Check("对稿已采用", plan.Script.Used);
        Check("对稿认出 2 处重录", plan.Script.RetakeCount == 2);
        int retakeDrops = plan.Segments.Count(s => s.Action == CutAction.Drop && s.Reason == CutReason.Retake);
        // 两处重录在时间上紧挨着，会被合并成"一刀" → 只要 >=1 且剪掉约 4 秒即对
        Check("决策里出现 Retake 剪段", retakeDrops >= 1);
        Console.WriteLine($"    剪掉 = {plan.Stats.RemovedSec:F2}s, 总时长 = {plan.Stats.OriginalSec:F2}s, Retake剪段 = {retakeDrops}");
        Check("剪掉约 4 秒（两遍废片）", plan.Stats.RemovedSec >= 3.0 && plan.Stats.RemovedSec <= 5.0);
        Check("保留段仍在（没把内容剪光）", plan.FinalDurationSec > 5.0);

        // 不传稿子 = 老行为（ScanRetakes 照样独立工作，一个字都没变）
        var planNoScript = AutoCutEngine.Analyze(pcm, opt, tr, "verify", "fake.wav");
        Check("不传稿子：Script 未启用", planNoScript.Script != null && !planNoScript.Script.Available);
        Check("不传稿子：老路照常剪（波形/文本重录仍生效）", planNoScript.Stats.RemovedSec > 0);

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass}，失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>造"2 秒有声 + 0.4 秒静音"× count 的假音频，并返回每段的起点（秒）。</summary>
    static (PcmData pcm, double[] starts) VoiceTrack(int count, double onSec = 2.0, double offSec = 0.4, int rate = 16000)
    {
        double unit = onSec + offSec;
        int n = (int)(count * unit * rate);
        var s = new float[n];
        var starts = new double[count];
        for (int k = 0; k < count; k++)
        {
            starts[k] = k * unit;
            int i0 = (int)(starts[k] * rate);
            int i1 = (int)((starts[k] + onSec) * rate);
            for (int i = i0; i < i1 && i < n; i++)
            {
                double t = (double)i / rate;
                s[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 300 * t));
            }
        }
        return (new PcmData { Samples = s, SampleRate = rate }, starts);
    }
}
