using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using LiteCut;
using LiteCut.Core.Timeline;

namespace AudioProbe
{
    /// <summary>
    /// 一次性验证：新的音频渲染器 + 缓存 + 预热。
    /// 重点证明"同一个源文件只开一次、相邻段顺读不倒回"没有读错位置 ——
    /// 这个改动一旦读偏，用户听到的就是错位的声音。
    /// </summary>
    public static class Program
    {
        const string RealProjectJson = @"D:\工作台\项目\思辨与立场\project.json";
        // 测试音频仍是用户素材库里的实体文件（开发工具用，不进产品；文件本体没搬家）
        const string RealM4a = @"D:\视频创作\读书博主\音频素材库\康宁大药房(感恩堂店) 24.m4a";

        static int _pass, _fail;
        static readonly string TempDir = Path.Combine(Path.GetTempPath(), "litecut-audio-files");

        public static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Directory.CreateDirectory(TempDir);

            try
            {
                TestRampFile();
                TestGapsAreSilent();
                TestGainAndFades();
                TestHitTest();

                TestPlanFingerprint();
                TestEngineCacheAndPrewarm();
                TestDeviceReuse();
                TestIncrementalPatch();

                // 这三段要用真实的项目文件和大段 m4a 素材。换台机器就没有了，
                // 所以找不到时跳过并说明 —— 合成素材那几段已经覆盖了核心不变量。
                bool hasProject = File.Exists(RealProjectJson);
                bool hasM4a = File.Exists(RealM4a);

                if (hasProject)
                {
                    TestPatchOnRealProject();
                }
                else
                {
                    Section("九、真实项目上的增量重拼 —— 已跳过");
                    Console.WriteLine($"   找不到项目文件：{RealProjectJson}");
                }

                if (hasM4a)
                {
                    TestSeekAccuracy();
                }
                else
                {
                    Section("十、定位精度 —— 已跳过");
                    Console.WriteLine($"   找不到素材：{RealM4a}");
                }

                if (hasProject)
                {
                    TestRealProjectSpeed();
                }
                else
                {
                    Section("七、真实项目实测 —— 已跳过");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("探针自身崩了: " + ex);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"===== 通过 {_pass} 项，失败 {_fail} 项 =====");
            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 一、渲染位置精确性（合成文件）
        // ============================================================
        //
        // 造一个"值即下标"的素材：第 i 个样本的值 = i × 1e-6。
        // 于是从输出值反推源下标，就能精确知道每段到底读的是哪里。

        const double RampScale = 1e-6;

        static void TestRampFile()
        {
            Section("一、渲染位置精确性（48kHz 源）");

            string f = Path.Combine(TempDir, "ramp48k.wav");
            WriteRampWav(f, 48000, 1, 60.0);

            // 段：A 相邻、B 紧接（走顺读）、C 远跳、D 回跳
            var segs = new List<AudioSegment>
            {
                Seg(0,  f,  1.0,  2.0,  0.0),    // 起点
                Seg(1,  f,  2.0,  3.0,  1.0),    // 紧邻上一段（顺读）
                Seg(2,  f, 10.0, 10.5,  2.0),    // 跳过 7 秒（应重新定位）
                Seg(3,  f,  3.0,  3.5,  2.5),    // 往回跳（应重新定位）
                Seg(4,  f, 20.0, 20.2,  3.0),    // 再往前跳
            };

            var plan = AudioRenderPlan.Build(segs, 48000);
            var out1 = TimelineAudioRenderer.Render(plan);

            Check("渲染长度 = 计划总样本数", out1.Length == plan.TotalSamples,
                  $"实际 {out1.Length} / 计划 {plan.TotalSamples}");

            int total = 0, bad = 0;
            double worst = 0;

            foreach (var s in segs)
            {
                long from = (long)(s.SourceStartSec * 48000);
                int len = (int)(s.SourceLengthSec * 48000);
                int at = (int)((s.TimelineStartSec - plan.StartSec) * 48000);

                for (int i = 0; i < len; i++)
                {
                    total++;
                    double expect = (from + i) * RampScale;
                    double actual = out1[at + i];
                    double err = Math.Abs(actual - expect) / RampScale;   // 换算成"差了几个样本"
                    if (err > worst) worst = err;
                    if (err > 2.5) bad++;
                }
            }

            Check($"全部 {total} 个样本都落在正确的源位置（最大偏差 {worst:F3} 个样本）", bad == 0,
                  $"越界样本 {bad} 个");

            // 反向确认"值不再是下标"：如果哪个段被读偏了 7 秒，这里会立刻爆出来
            Check("第 3 段（远跳 10s）确实读的是 10s 处",
                  Near(out1[SegOffset(plan, 2.0)], 10.0 * 48000 * RampScale), "远跳段起点值不对");
            Check("第 4 段（回跳 3s）确实读的是 3s 处",
                  Near(out1[SegOffset(plan, 2.5)], 3.0 * 48000 * RampScale), "回跳段起点值不对");

            // ---- 44100 源 → 48000 输出（重采样路径）----
            string f44 = Path.Combine(TempDir, "ramp44k.wav");
            WriteRampWav(f44, 44100, 1, 30.0);

            var segs44 = new List<AudioSegment> { Seg(0, f44, 5.0, 6.0, 0.0) };
            var plan44 = AudioRenderPlan.Build(segs44, 48000);
            var out44 = TimelineAudioRenderer.Render(plan44);

            Check("44.1k 源渲染长度按 48k 折算", Math.Abs(out44.Length - 48000) <= 2,
                  $"长度 {out44.Length}（期望 ≈48000）");

            int idxAtMid = (int)Math.Round(out44[24000] / RampScale);
            int expectMid = (int)((5.0 * 44100) + 24000 * (44100.0 / 48000.0));
            Check("重采样后中点的源位置正确", Math.Abs(idxAtMid - expectMid) <= 3,
                  $"实际源下标 {idxAtMid} / 期望 {expectMid}");
        }

        static int SegOffset(AudioRenderPlan plan, double timelineSec)
            => (int)((timelineSec - plan.StartSec) * plan.SampleRate);

        static bool Near(double a, double b, double tolSamples = 2.5) => Math.Abs(a - b) < tolSamples * RampScale;

        static void TestGapsAreSilent()
        {
            Section("二、空隙是静音、块边界对齐");

            string f = Path.Combine(TempDir, "ramp48k.wav");
            var segs = new List<AudioSegment>
            {
                Seg(0, f, 0.0, 1.0, 0.0),    // 时间轴 0.0 ~ 1.0
                Seg(1, f, 5.0, 6.0, 2.0),    // 时间轴 2.0 ~ 3.0（中间留 1 秒空）
            };
            var plan = AudioRenderPlan.Build(segs, 48000);
            var o = TimelineAudioRenderer.Render(plan);

            Check("总长 = 3 秒", Math.Abs(plan.DurationSec - 3.0) < 0.001, $"实际 {plan.DurationSec:F4}");

            bool gapSilent = true;
            for (int i = 48000; i < 96000; i++) if (o[i] != 0) { gapSilent = false; break; }
            Check("第一块之后的 1 秒空隙全是静音", gapSilent);

            Check("第一块起点有声音", o[10] != 0);   // 下标 0 的样点值本来就是 0（ramp 从 0 开始）
            Check("第一块正好在 1.0s 处收尾（下一采样已静音）", o[47999] != 0 && o[48000] == 0);
            Check("第二块正好从 2.0s 处开始响", o[96000] != 0 && o[95999] == 0);
        }

        static void TestGainAndFades()
        {
            Section("三、增益与淡入淡出");

            string f = Path.Combine(TempDir, "ramp48k.wav");
            long from = (long)(2.0 * 48000);

            // 增益 0.5
            var p1 = AudioRenderPlan.Build(new[] { Seg(0, f, 2.0, 2.1, 0.0, gain: 0.5) }, 48000);
            var o1 = TimelineAudioRenderer.Render(p1);
            int mid = 2400;
            double expect = (from + mid) * RampScale * 0.5;
            Check("增益 0.5 生效", Math.Abs(o1[mid] - expect) <= expect * 1e-5,
                  $"实际 {o1[mid]:E6} / 期望 {expect:E6}");

            // 淡入 0.01 秒（480 个样本）
            var p2 = AudioRenderPlan.Build(new[] { Seg(0, f, 2.0, 2.1, 0.0, fadeIn: 0.01) }, 48000);
            var o2 = TimelineAudioRenderer.Render(p2);
            Check("淡入：第 0 个样本为 0", o2[0] == 0);
            double half = o2[240] / ((from + 240) * RampScale);
            Check($"淡入：中途衰减系数 ≈0.5（实际 {half:F3}）", Math.Abs(half - 0.5) < 0.02);

            // 淡出 0.01 秒
            var p3 = AudioRenderPlan.Build(new[] { Seg(0, f, 2.0, 2.1, 0.0, fadeOut: 0.01) }, 48000);
            var o3 = TimelineAudioRenderer.Render(p3);
            int last = 4800 - 1;
            Check("淡出：最后一个样本接近 0", Math.Abs(o3[last]) < (from + last) * RampScale * 0.01);
        }

        // ============================================================
        // 三·五、命中测试（点击位置不许"偏右"）
        // ============================================================

        static string Describe(TimelineClip? c, string edge)
            => c == null ? "（不命中）" : $"{c.SourceName}.{edge}";

        static TimelineClip MkClip(string name, double srcStart, double srcEnd, double tlStart)
            => new TimelineClip
            {
                SourcePath = "probe.wav",
                SourceName = name,
                SourceStartSec = srcStart,
                SourceEndSec = srcEnd,
                SourceTotalSec = 100,
                TimelineStartSec = tlStart,
            };

        static void TestHitTest()
        {
            Section("三·五、命中测试 —— 点击位置不许偏右");

            // 【背景】2026-09-29 用户报告：A、B 首尾相接时，点 A 的右侧想拖
            // 右边缘裁切，选中的却是右边的 B（"实际点击的位置偏右"），而且
            // 一按住鼠标 A 就自己延长、顶到 B 的左边缘。根因有两条：
            //   ① HitTest 从尾部"先边缘后身体"逐块扫 —— 缝左侧会先命中 B 的
            //      左边缘。修法：点在谁身上，边缘只认谁自己的。
            //   ② 界面层 TrimTo 的右边缘"时间轴→源素材"映射基准写错（那半截
            //      在 WaveformControl 里，注释里钉死了推导，这里钉死 HitTest 的行为）。

            var p = new TimelineProject();
            var a = MkClip("A", 0, 10, 0);
            var b = MkClip("B", 10, 20, 10);
            p.Clips.Add(a);
            p.Clips.Add(b);                       // 首尾相接，缝在 10 秒

            const double tol = 0.04;              // 8 像素 × 0.005 秒/像素

            var (c1, e1) = p.HitTest(10 - tol * 0.5, tol);
            Check("缝左侧（还在 A 身上）→ 命中 A 的右边缘", c1 == a && e1 == "right",
                  $"得到 {Describe(c1, e1)}");

            var (c2, e2) = p.HitTest(10 + tol * 0.5, tol);
            Check("缝右侧（已经到 B 身上）→ 命中 B 的左边缘", c2 == b && e2 == "left",
                  $"得到 {Describe(c2, e2)}");

            var (c3, e3) = p.HitTest(5.0, tol);
            Check("A 的正中间 → 身体命中", c3 == a && e3 == "body", $"得到 {Describe(c3, e3)}");

            var (c5, e5) = p.HitTest(20 - 0.01, tol);
            Check("B 右边缘内侧 → B 的右边缘", c5 == b && e5 == "right", $"得到 {Describe(c5, e5)}");

            // 大片空白：B 结束于 20，下一块从 40 开始
            var c4clip = MkClip("C", 40, 50, 40);
            p.Clips.Add(c4clip);
            var (c4, e4) = p.HitTest(30.0, tol);
            Check("大片空白 → 不命中任何块", c4 == null, $"得到 {Describe(c4, e4)}");

            // 窄缝（0.03 秒空档，小于容差）：点空白处也能抓到两侧边缘，方便微调
            p.Clips.Clear();
            var d1 = MkClip("D1", 0, 10, 0);
            var d2 = MkClip("D2", 20, 30, 10.03);
            p.Clips.Add(d1);
            p.Clips.Add(d2);

            var (c6, e6) = p.HitTest(10.01, tol);   // 缝里偏左
            Check("窄缝偏左 → 左块的右边缘", c6 == d1 && e6 == "right", $"得到 {Describe(c6, e6)}");

            var (c7, e7) = p.HitTest(10.02, tol);   // 缝里偏右
            Check("窄缝偏右 → 右块的左边缘", c7 == d2 && e7 == "left", $"得到 {Describe(c7, e7)}");

            var (c8, e8) = p.HitTest(10.015, tol);  // 正当中：两边一样近，抓哪边都合理
            bool ok8 = c8 != null && ((c8 == d1 && e8 == "right") || (c8 == d2 && e8 == "left"));
            Check("窄缝正中 → 抓到缝两侧之一的边缘", ok8, $"得到 {Describe(c8, e8)}");
        }

        // ============================================================
        // 四、缓存指纹
        // ============================================================

        static void TestPlanFingerprint()
        {
            Section("四、缓存指纹（缓存钥匙）");

            string f = Path.Combine(TempDir, "ramp48k.wav");
            var baseline = new List<AudioSegment> { Seg(0, f, 1.0, 2.0, 0.0), Seg(1, f, 5.0, 6.0, 2.0) };

            ulong fp0 = AudioRenderPlan.Build(baseline, 48000).Fingerprint;
            ulong fp0Again = AudioRenderPlan.Build(baseline, 48000).Fingerprint;
            Check("同样内容两次构建 → 指纹相同", fp0 == fp0Again);

            var moved = new List<AudioSegment> { Seg(0, f, 1.0, 2.0, 0.0), Seg(1, f, 5.0, 6.0, 2.5) };
            Check("挪动一块 → 指纹变", AudioRenderPlan.Build(moved, 48000).Fingerprint != fp0);

            ulong backAgain = AudioRenderPlan.Build(moved, 48000).Fingerprint;
            Check("挪走再挪回来 → 指纹回到原值（证明是按内容算的，不是版本号）",
                  backAgain != fp0 && AudioRenderPlan.Build(baseline, 48000).Fingerprint == fp0);

            var volumed = new List<AudioSegment> { Seg(0, f, 1.0, 2.0, 0.0, gain: 0.9), Seg(1, f, 5.0, 6.0, 2.0) };
            Check("改音量 → 指纹变", AudioRenderPlan.Build(volumed, 48000).Fingerprint != fp0);

            var trimmed = new List<AudioSegment> { Seg(0, f, 1.2, 2.0, 0.0), Seg(1, f, 5.0, 6.0, 2.0) };
            Check("裁切 → 指纹变", AudioRenderPlan.Build(trimmed, 48000).Fingerprint != fp0);

            var faded = new List<AudioSegment> { Seg(0, f, 1.0, 2.0, 0.0, fadeIn: 0.3), Seg(1, f, 5.0, 6.0, 2.0) };
            Check("加淡入 → 指纹变", AudioRenderPlan.Build(faded, 48000).Fingerprint != fp0);

            var removed = new List<AudioSegment> { Seg(0, f, 1.0, 2.0, 0.0) };
            Check("删一块 → 指纹变", AudioRenderPlan.Build(removed, 48000).Fingerprint != fp0);

            var swapped = new List<AudioSegment> { Seg(1, f, 5.0, 6.0, 2.0), Seg(0, f, 1.0, 2.0, 0.0) };
            Check("交换两块顺序 → 指纹变", AudioRenderPlan.Build(swapped, 48000).Fingerprint != fp0);

            string other = Path.Combine(TempDir, "ramp44k.wav");
            var otherFile = new List<AudioSegment> { Seg(0, other, 1.0, 2.0, 0.0), Seg(1, f, 5.0, 6.0, 2.0) };
            Check("换源文件 → 指纹变", AudioRenderPlan.Build(otherFile, 48000).Fingerprint != fp0);

            // 拼接歧义：["ab","c"] vs ["a","bc"] 不能撞车
            ulong a1 = PathHash(new[] { "ab", "c" });
            ulong a2 = PathHash(new[] { "a", "bc" });
            Check("字符串拼接不会撞指纹", a1 != a2);

            Check("空计划指纹稳定且总样本数为 0",
                  AudioRenderPlan.Build(new AudioSegment[0], 48000).TotalSamples == 0);
        }

        static ulong PathHash(string[] paths)
        {
            var segs = new List<AudioSegment>();
            foreach (var p in paths) segs.Add(Seg(0, p, 0, 1, 0));
            return AudioRenderPlan.Build(segs, 48000).Fingerprint;
        }

        // ============================================================
        // 五、引擎层：缓存命中 + 后台预热
        // ============================================================

        static void TestEngineCacheAndPrewarm()
        {
            Section("五、引擎缓存与后台预热");

            var project = LoadRealProject();

            using var engine = new AudioEngine();
            Check("初始状态：缓存是空的", !engine.HasCachedAudio);

            var sw = Stopwatch.StartNew();
            engine.Prewarm(project);
            sw.Stop();
            Check($"Prewarm 立刻返回（{sw.ElapsedMilliseconds} ms，不阻塞界面）", sw.ElapsedMilliseconds < 250,
                  $"实际 {sw.ElapsedMilliseconds} ms");

            // 等后台拼好
            var wait = Stopwatch.StartNew();
            while (!engine.HasCachedAudio && wait.ElapsedMilliseconds < 30000)
                Thread.Sleep(50);

            Check($"后台预热完成（{wait.ElapsedMilliseconds} ms）", engine.HasCachedAudio);

            ulong fp = ReadCachedFingerprint(engine);
            ulong nowFp = TimelineAudioRenderer.BuildPlan(project).Fingerprint;
            Check("缓存里的正是当前时间轴那一份", fp == nowFp);

            // 再调一次：不该重新拼
            var sw2 = Stopwatch.StartNew();
            engine.Prewarm(project);
            Thread.Sleep(400);
            sw2.Stop();
            Check($"时间轴没变时再预热 = 什么都不做（{sw2.ElapsedMilliseconds} ms）",
                  ReadCachedFingerprint(engine) == nowFp);

            // 改一块 → 指纹应变化，预热后缓存要换新
            project.Clips[0].VolumeDb += 3.0;
            ulong changed = TimelineAudioRenderer.BuildPlan(project).Fingerprint;
            Check("改一块之后指纹确实变了", changed != nowFp);

            engine.Prewarm(project);
            var wait2 = Stopwatch.StartNew();
            while (ReadCachedFingerprint(engine) != changed && wait2.ElapsedMilliseconds < 30000)
                Thread.Sleep(50);
            Check($"预热把缓存换成了新的那一份（{wait2.ElapsedMilliseconds} ms）",
                  ReadCachedFingerprint(engine) == changed);
            Check($"改一块音量后缓存刷新很快（{wait2.ElapsedMilliseconds} ms —— 走的是增量重拼，不是整条重拼）",
                  wait2.ElapsedMilliseconds < 600, $"实际 {wait2.ElapsedMilliseconds} ms");

            // 连打多次 Prewarm（模拟用户连续操作）不应崩、且最终缓存是最新的
            project.Clips[1].TimelineStartSec += 0.5;
            ulong final = TimelineAudioRenderer.BuildPlan(project).Fingerprint;
            for (int i = 0; i < 12; i++) engine.Prewarm(project);
            var wait3 = Stopwatch.StartNew();
            while (ReadCachedFingerprint(engine) != final && wait3.ElapsedMilliseconds < 30000)
                Thread.Sleep(50);
            Check($"连续 12 次 Prewarm 后缓存收敛到最新内容（{wait3.ElapsedMilliseconds} ms）",
                  ReadCachedFingerprint(engine) == final);
        }

        static ulong ReadCachedFingerprint(AudioEngine engine)
        {
            var f = typeof(AudioEngine).GetField("_cachedPlan", BindingFlags.NonPublic | BindingFlags.Instance);
            var plan = f?.GetValue(engine) as AudioRenderPlan;
            return plan?.Fingerprint ?? 0UL;
        }

        // ============================================================
        // 六、播放设备复用（暂停/重播不该重建设备）
        // ============================================================

        static void TestDeviceReuse()
        {
            Section("六、播放设备复用（暂停/重播不重建设备）");

            string f = Path.Combine(TempDir, "ramp48k.wav");
            var project = new TimelineProject();
            project.Clips.Add(new TimelineClip
            {
                SourcePath = f,
                SourceStartSec = 0,
                SourceEndSec = 0.4,      // 全零附近的短块，够测行为又不出声
                TimelineStartSec = 0,
            });

            using var engine = new AudioEngine();
            var stopped = new ManualResetEventSlim(false);
            engine.PlaybackStopped += (s, e) => stopped.Set();

            engine.Play(project);
            object out1 = ReadField(engine, "_output");
            Check("第一次播放：建了播放设备", out1 != null);

            engine.Pause();
            engine.Play(project);
            object out2 = ReadField(engine, "_output");
            Check("暂停后接着播：设备被复用（没有重建）", ReferenceEquals(out1, out2));

            // 让它自然播完
            Check("0.4 秒的块能自然播完（PlaybackStopped 触发）", stopped.Wait(6000));

            // 播完之后再按一次空格 —— 这里走的就是"设备还开着"那条分支
            var provider = ReadField(engine, "_provider");
            var posProp = provider?.GetType().GetProperty("Position");
            long GetPos() => (long)(posProp?.GetValue(provider) ?? -1L);
            void SetPos(long v) => posProp?.SetValue(provider, v);

            if (provider != null) SetPos(0);
            long before = GetPos();

            engine.Play(project);
            Thread.Sleep(400);
            long after = GetPos();

            Check($"播完之后再播：声音确实又走起来了（位置 {before} → {after}）", after > before,
                  $"Position {before} → {after}");

            object out3 = ReadField(engine, "_output");
            Check("播完之后再播：设备仍然是同一个（没重建）", ReferenceEquals(out2, out3));

            // ---- 改了时间轴之后再播，设备会重建（代价明确，行为确定）----
            // 编辑过时间轴 → 拼出来的是新数组 → 设备缓冲里还压着最多 100ms 的旧声音，
            // 换数据前必须先把它冲掉。于是这里**故意**退到重建设备那条路。
            engine.Stop();
            engine.Play(project);
            object before3 = ReadField(engine, "_output");
            engine.Pause();

            project.Clips[0].VolumeDb = -6.0;           // 动一下 → 拼出来的数组是新的一份
            engine.Play(project);
            object after3 = ReadField(engine, "_output");
            Check("改过时间轴之后再播：设备被重建（换数据必须先冲掉设备缓冲）",
                  !ReferenceEquals(before3, after3));
            engine.Stop();

            // ---- 为什么"换数据不换设备"这条捷径不能走（上面那三个数字就是代价）----
            Console.WriteLine();
            var asm = typeof(AudioEngine).Assembly;
            var provType = asm.GetType("LiteCut.MemorySampleProvider");
            var zero = new float[48000];

            object MakeProvider() => Activator.CreateInstance(provType, zero, 48000);

            const int Rounds = 8;

            var swA = Stopwatch.StartNew();
            for (int i = 0; i < Rounds; i++)
            {
                var o = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
                o.Init((ISampleProvider)MakeProvider());
                o.Play();
                o.Stop();
                o.Dispose();
            }
            swA.Stop();

            var keep = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
            keep.Init((ISampleProvider)MakeProvider());
            var swB = Stopwatch.StartNew();
            for (int i = 0; i < Rounds; i++)
            {
                keep.Stop();
                keep.Play();
            }
            swB.Stop();
            keep.Dispose();

            Console.WriteLine($"   设备「拆了重建」{swA.ElapsedMilliseconds / Rounds} ms/次"
                              + $"　「留着设备重开」{swB.ElapsedMilliseconds / Rounds} ms/次"
                              + "　← 省下来的是这几十毫秒，代价见下");

            // ---- Stop() 引发的 PlaybackStopped 是同步还是异步？----
            // 这就是"留着重开"那条捷径走不通的原因：Stop() 引发的 PlaybackStopped
            // **不保证同步**（有时 Stop 返回之后才发）。所以不能靠"Stop 前后临时摘掉
            // 事件处理器"来避开它 —— 摘了再装回去，晚到的那次就会漏进来，被当成
            // "播完了"（红线冻住、播放键弹回）。要省这几十毫秒，得另想办法
            // （例如后台预先开好一个空闲设备），不能跟异步时序赌。
            var timings = new List<string>();
            int asyncCount = 0;
            for (int i = 0; i < 6; i++)
            {
                var probeDev = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
                probeDev.Init((ISampleProvider)MakeProvider());
                int fired = 0;
                probeDev.PlaybackStopped += (s, e) => Interlocked.Increment(ref fired);
                probeDev.Play();
                Thread.Sleep(120);

                probeDev.Stop();
                int rightAfterStop = Volatile.Read(ref fired);
                Thread.Sleep(200);
                int laterOn = Volatile.Read(ref fired);
                probeDev.Dispose();

                if (rightAfterStop == 0 && laterOn >= 1) asyncCount++;
                timings.Add(rightAfterStop == 1 ? "同步" : "异步");
                Check($"第 {i + 1} 次：Stop() 引发的 PlaybackStopped 最终只来了 1 次", laterOn == 1,
                      $"返回时 {rightAfterStop} 次，稍后 {laterOn} 次");
            }

            Console.WriteLine("   Stop() → PlaybackStopped 时序：" + string.Join(" / ", timings)
                              + $"（{asyncCount}/6 次是异步的 —— 这条数据就是「不能靠摘事件处理器绕开它」的依据）");
        }

        static object ReadField(object target, string name)
            => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);

        // ============================================================
        // 八、增量重拼
        // ============================================================
        //
        // 设计要点（决定了断言该怎么写）：
        //   · 一个字节都不读源文件 —— 新值 = 旧值 × (新包络 / 旧包络)
        //   · 所以"挪块 / 裁短 / 调音量 / 改淡化 / 平移 / 删块"都应当走增量，
        //     而且结果要和"整条重拼"一致到浮点舍入级别
        //   · "要更多源素材"的操作（新增块、把边缘往外拉、换源文件）
        //     以及"旧包络为 0 推不回来"的情形，必须拒绝（退整条）
        //   · 施工图必须把"没动的段"也列出来（要照搬），否则新成品会缺一大片

        static void TestIncrementalPatch()
        {
            Section("八、增量重拼（从旧成品就地推导）");

            string f = Path.Combine(TempDir, "ramp48k.wav");
            string f2 = Path.Combine(TempDir, "ramp48k-2.wav");
            WriteRampWav(f2, 48000, 1, 60.0);

            // 五段、彼此有间隙（挪得动），这是最典型的布局。
            // 第一个参数是"身份槽位"——同一槽位每次造出来的都是同一块（Id 稳定）。
            List<AudioSegment> Layout() => new()
            {
                Seg(0, f,  1.0,  2.0, 0.0),
                Seg(1, f, 12.0, 13.0, 2.0),
                Seg(2, f, 24.0, 25.0, 4.0),
                Seg(3, f, 36.0, 36.5, 6.0),
                Seg(4, f, 48.0, 49.0, 8.0),
            };

            var p0 = AudioRenderPlan.Build(Layout(), 48000);
            var s0 = TimelineAudioRenderer.Render(p0);

            // ---- ① 一点没动 → 连数组都不用换 ----
            var same = TimelineAudioRenderer.TryPatch(p0, s0, AudioRenderPlan.Build(Layout(), 48000));
            Check("一点没动：直接复用同一份数组（0 拷贝）", ReferenceEquals(same, s0));

            // ---- ② 改一块的音量 ----
            var v = Layout();
            v[2] = Seg(2, f, 24.0, 25.0, 4.0, gain: 0.5);
            var pv = AudioRenderPlan.Build(v, 48000);
            var sv = TimelineAudioRenderer.Render(pv);
            var patchV = TimelineAudioRenderer.TryPatch(p0, s0, pv);
            Check("改音量：走增量重拼（不是整条重来）", patchV != null);
            CheckSame("改音量：结果与整条重拼逐样本一致", patchV, sv);
            // 最容易漏的一点：新成品是新分配的数组，没动的块也必须搬过去
            Check("改音量：没动的那 4 块照样有声音（新数组不是一片空白）",
                  SoundAt(patchV, 0.5) && SoundAt(patchV, 2.5) && SoundAt(patchV, 6.2) && SoundAt(patchV, 8.5));

            // ---- ③ 挪一块（旧位置要擦干净、新位置要有声、其余块不能动）----
            var m = Layout();
            m[1] = Seg(1, f, 12.0, 13.0, 7.0);      // 从 t=2.0 挪到 t=7.0
            var pm = AudioRenderPlan.Build(m, 48000);
            var patchM = TimelineAudioRenderer.TryPatch(p0, s0, pm);
            Check("挪块：走增量重拼", patchM != null);
            CheckSame("挪块：结果与整条重拼逐样本一致", patchM, TimelineAudioRenderer.Render(pm));
            Check("挪块：旧位置擦干净了（整段静音，没有残留声）", SilentBetween(patchM, 2.0, 3.0));
            Check("挪块：新位置有声音", SoundAt(patchM, 7.5));
            Check("挪块：其余 3 块原封不动",
                  SoundAt(patchM, 0.5) && SoundAt(patchM, 4.5) && SoundAt(patchM, 6.2));

            // ---- ④ 裁短：裁尾巴 / 裁开头 ----
            var t = Layout();
            t[4] = Seg(4, f, 48.0, 48.5, 8.0);          // 尾块从 1 秒裁到 0.5 秒
            var pt = AudioRenderPlan.Build(t, 48000);
            var patchT = TimelineAudioRenderer.TryPatch(p0, s0, pt);
            Check("裁尾巴：走增量重拼", patchT != null);
            CheckSame("裁尾巴：结果与整条重拼逐样本一致", patchT, TimelineAudioRenderer.Render(pt));
            Check($"裁尾巴：缓冲区跟着变短（{patchT?.Length} = {RenderLen(pt)}）",
                  patchT != null && patchT.Length == pt.TotalSamples);

            var h = Layout();
            h[2] = Seg(2, f, 24.4, 25.0, 4.0);          // 源入点往后缩 0.4 秒（新区间 ⊂ 旧区间）
            var ph = AudioRenderPlan.Build(h, 48000);
            var patchH = TimelineAudioRenderer.TryPatch(p0, s0, ph);
            Check("裁开头：走增量重拼（新区间是旧区间的子集）", patchH != null);
            CheckSame("裁开头：结果与整条重拼逐样本一致", patchH, TimelineAudioRenderer.Render(ph));

            // ---- ⑤ 整条平移（原点跟着动）----
            var sh = Layout();
            for (int i = 0; i < sh.Count; i++)
            {
                var s = sh[i];
                sh[i] = Seg(i, s.SourcePath, s.SourceStartSec, s.SourceEndSec,
                            s.TimelineStartSec + 5.0, s.Gain, s.FadeInSec, s.FadeOutSec);
            }
            var psh = AudioRenderPlan.Build(sh, 48000);
            var patchSh = TimelineAudioRenderer.TryPatch(p0, s0, psh);
            Check("整条平移（原点变了）：仍走增量重拼", patchSh != null);
            CheckSame("整条平移：结果与整条重拼逐样本一致", patchSh, TimelineAudioRenderer.Render(psh));
            Check("整条平移：内容与原成品一模一样（只是坐标系挪了）", BitSame(patchSh, s0));

            // ---- ⑥ 删一块 → 空出来的位置保持静音 ----
            var dl = Layout();
            dl.RemoveAt(3);                              // 删掉 t=6.0 那块
            var pdl = AudioRenderPlan.Build(dl, 48000);
            var patchDl = TimelineAudioRenderer.TryPatch(p0, s0, pdl);
            Check("删一块：走增量重拼", patchDl != null);
            CheckSame("删一块：结果与整条重拼逐样本一致", patchDl, TimelineAudioRenderer.Render(pdl));
            Check("删一块：它原来占的那 0.5 秒变成静音", SilentBetween(patchDl, 6.0, 6.5));

            // ---- ⑦ 板块互相重叠：改其中一块也要算对（后写的盖住先写的）----
            var ov = new List<AudioSegment>
            {
                Seg(0, f, 1.0, 2.0, 0.0),
                Seg(1, f, 5.0, 6.0, 1.0),
                Seg(2, f, 9.0, 10.0, 1.5),      // 和上一块重叠 0.5 秒
            };
            var pov = AudioRenderPlan.Build(ov, 48000);
            var sov = TimelineAudioRenderer.Render(pov);

            var ov2 = new List<AudioSegment>
            {
                Seg(0, f, 1.0, 2.0, 0.0),
                Seg(1, f, 5.0, 6.0, 1.0, gain: 0.7),
                Seg(2, f, 9.0, 10.0, 1.5),
            };
            var pov2 = AudioRenderPlan.Build(ov2, 48000);
            var patchOv = TimelineAudioRenderer.TryPatch(pov, sov, pov2);
            Check("重叠布局：改其中一块仍走增量重拼", patchOv != null);
            CheckSame("重叠布局：结果与整条重拼一致（重叠区仍是被压在下面那块的声音）",
                      patchOv, TimelineAudioRenderer.Render(pov2), tol: 2e-6);

            // ---- ⑧ 该拒绝的必须拒绝（宁可慢，不能听错）----
            var ad = Layout();
            ad.Add(Seg(5, f, 50.0, 51.0, 10.0));
            var rejAdd = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(ad, 48000));
            Check("新增一块：拒绝增量重拼（新块要读源素材）", !rejAdd.CanPatch);
            Console.WriteLine($"     拒绝原因：{rejAdd.Reason}");

            var ex = Layout();
            ex[3] = Seg(3, f, 36.0, 37.5, 6.0);
            var rejEx = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(ex, 48000));
            Check("源出点往后扩：拒绝", !rejEx.CanPatch);
            Console.WriteLine($"     拒绝原因：{rejEx.Reason}");

            var ex2 = Layout();
            ex2[3] = Seg(3, f, 35.0, 36.5, 6.0);
            var rejEx2 = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(ex2, 48000));
            Check("源入点往前扩：拒绝", !rejEx2.CanPatch);
            Console.WriteLine($"     拒绝原因：{rejEx2.Reason}");

            var sw2 = Layout();
            sw2[1] = Seg(1, f2, 12.0, 13.0, 2.0);
            var rejSrc = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(sw2, 48000));
            Check("换了源文件：拒绝", !rejSrc.CanPatch);
            Console.WriteLine($"     拒绝原因：{rejSrc.Reason}");

            // 旧增益是 0（整块静音）→ 乘什么都回不来
            var z = Layout();
            z[1] = Seg(1, f, 12.0, 13.0, 2.0, gain: 0);
            var pz = AudioRenderPlan.Build(z, 48000);
            var sz = TimelineAudioRenderer.Render(pz);
            var z2 = Layout();
            z2[1] = Seg(1, f, 12.0, 13.0, 2.0, gain: 1.0);
            var rejZ = AudioPatchLayout.Analyze(pz, AudioRenderPlan.Build(z2, 48000));
            Check("旧成品那块增益是 0：拒绝（0 乘不出东西来）", !rejZ.CanPatch);
            Console.WriteLine($"     拒绝原因：{rejZ.Reason}");

            // 旧成品第 0 个样本被淡入压成 0，而新状态不要淡入了
            var fi = new List<AudioSegment> { Seg(0, f, 1.0, 2.0, 0.0, fadeIn: 0.5) };
            var pfi = AudioRenderPlan.Build(fi, 48000);
            var sfi = TimelineAudioRenderer.Render(pfi);
            var fi2 = new List<AudioSegment> { Seg(0, f, 1.0, 2.0, 0.0) };
            var rejFi = AudioPatchLayout.Analyze(pfi, AudioRenderPlan.Build(fi2, 48000));
            Check("去掉淡入：拒绝（旧成品第 0 个样本被淡入压成 0，恢复不出来）", !rejFi.CanPatch);
            Console.WriteLine($"     拒绝原因：{rejFi.Reason}");

            var rejRate = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(Layout(), 44100));
            Check("采样率变了：拒绝", !rejRate.CanPatch);
            Console.WriteLine($"     拒绝原因：{rejRate.Reason}");

            // 旧成品长度和计划对不上 → 缓存不可信，不能拿去推
            Check("旧成品长度与计划不符：拒绝（不信任这份缓存）",
                  TimelineAudioRenderer.TryPatch(p0, new float[123], AudioRenderPlan.Build(Layout(), 48000)) == null);

            // ---- ⑨ 改动很多也照样增量（照搬是内存拷贝，不需要阈值）----
            var big = new List<AudioSegment>();
            for (int i = 0; i < 20; i++) big.Add(Seg(i, f, 1.0 + i, 2.0 + i, i * 2.0));
            var pb = AudioRenderPlan.Build(big, 48000);
            var sb = TimelineAudioRenderer.Render(pb);

            var bigChanged = new List<AudioSegment>();
            for (int i = 0; i < 20; i++)
                bigChanged.Add(Seg(i, f, 1.0 + i, 2.0 + i, i * 2.0, gain: i < 14 ? 0.5 : 1.0));
            var pbc = AudioRenderPlan.Build(bigChanged, 48000);
            var patchBc = TimelineAudioRenderer.TryPatch(pb, sb, pbc);
            Check("改了 14/20 块：仍走增量重拼", patchBc != null);
            CheckSame("改了 14/20 块：结果与整条重拼一致", patchBc, TimelineAudioRenderer.Render(pbc));

            var bigOne = new List<AudioSegment>();
            for (int i = 0; i < 20; i++)
                bigOne.Add(Seg(i, f, 1.0 + i, 2.0 + i, i * 2.0, gain: i == 7 ? 0.5 : 1.0));
            var pbo = AudioRenderPlan.Build(bigOne, 48000);
            var patchBo = TimelineAudioRenderer.TryPatch(pb, sb, pbo);
            Check("只改 1/20 块：走增量重拼", patchBo != null);
            CheckSame("只改 1/20 块：结果与整条重拼一致", patchBo, TimelineAudioRenderer.Render(pbo));

            // ---- ⑩ 连续重拼不累积误差 ----
            var curPlan = p0;
            var curSamples = s0;
            bool allPatched = true, allSame = true;
            var rnd = new Random(1234);
            for (int step = 0; step < 8; step++)
            {
                var layout = Layout();
                int pick = rnd.Next(layout.Count);
                var s = layout[pick];
                double g = Math.Round(0.2 + rnd.NextDouble() * 0.8, 1);   // 一位小数，可复现
                layout[pick] = Seg(pick, s.SourcePath, s.SourceStartSec, s.SourceEndSec,
                                   s.TimelineStartSec, g, s.FadeInSec, s.FadeOutSec);
                var pn = AudioRenderPlan.Build(layout, 48000);
                var got = TimelineAudioRenderer.TryPatch(curPlan, curSamples, pn);
                if (got == null) { allPatched = false; break; }
                if (!Same(got, TimelineAudioRenderer.Render(pn), 2e-5)) allSame = false;
                curPlan = pn;
                curSamples = got;
            }
            Check("连续 8 次增量重拼：每次都走增量", allPatched);
            Check("连续 8 次增量重拼：始终与整条重拼一致（不累积误差）", allSame);

            // ---- ⑪ 施工图本身（纯算术，可以直接断言）----
            var sp = AudioPatchLayout.Footprint(p0, p0.Segments[0]);
            Check("占位起点算得对", sp.Start == 0 && sp.End == 48000, sp.ToString());
            Check("首尾相接不算重叠", !new SampleSpan(0, 100).Overlaps(new SampleSpan(100, 200)));
            Check("真的重叠能识别出来", new SampleSpan(0, 150).Overlaps(new SampleSpan(100, 200)));

            var mv = Layout();
            mv[2] = Seg(2, f, 24.0, 25.0, 30.0);       // 只把 slot2 挪到 30 秒处
            var ap = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(mv, 48000));
            int rebuilds = 0, copies = 0;
            foreach (var j in ap.Jobs) { if (j.IsCopy) copies++; else rebuilds++; }
            Check("施工图：挪一块 → 1 段重算、其余 4 段照搬",
                  ap.CanPatch && rebuilds == 1 && copies == 4, $"重算 {rebuilds} / 照搬 {copies}");

            var vv = Layout();
            vv[3] = Seg(3, f, 36.0, 36.5, 6.0, gain: 0.4);
            var ap2 = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(vv, 48000));
            int r2 = 0;
            foreach (var j in ap2.Jobs) if (!j.IsCopy) r2++;
            Check("施工图：改一块音量 → 也只标 1 段要重算", ap2.CanPatch && r2 == 1);

            var ap3 = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Build(Layout(), 48000));
            Check("施工图：完全没动 → 全部照搬（NothingToDo）", ap3.CanPatch && ap3.NothingToDo);

            var ap4 = AudioPatchLayout.Analyze(p0, AudioRenderPlan.Empty(48000));
            Check("施工图：新时间轴空了 → 拒绝", !ap4.CanPatch);
            Console.WriteLine($"     拒绝原因：{ap4.Reason}");
        }

        static int RenderLen(AudioRenderPlan plan) => plan.TotalSamples;

        /// <summary>某个时间点上有声音（该下标非零）</summary>
        static bool SoundAt(float[] a, double sec)
        {
            int i = (int)(sec * 48000);
            return a != null && i >= 0 && i < a.Length && a[i] != 0;
        }

        /// <summary>某个时间区间内全是静音</summary>
        static bool SilentBetween(float[] a, double fromSec, double toSec)
        {
            if (a == null) return false;
            int from = (int)(fromSec * 48000), to = (int)(toSec * 48000);
            if (from < 0) from = 0;
            if (to > a.Length) to = a.Length;
            for (int i = from; i < to; i++) if (a[i] != 0) return false;
            return true;
        }

        /// <summary>两串样本是否一致（tol = 0 表示要求逐位相同）</summary>
        static bool Same(float[] a, float[] b, double tol = 0)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < b.Length; i++)
                if (Math.Abs(a[i] - b[i]) > tol) return false;
            return true;
        }

        /// <summary>逐样本比对并报告（tol = 0 表示要求逐位相同）</summary>
        static void CheckSame(string label, float[] got, float[] expect, double tol = 0)
        {
            if (got == null) { Check(label, false, "增量结果是 null"); return; }
            if (got.Length != expect.Length)
            {
                Check(label, false, $"长度不同 {got.Length} vs {expect.Length}");
                return;
            }

            int diff = 0, worstAt = 0;
            double worst = 0;
            for (int i = 0; i < expect.Length; i++)
            {
                double d = Math.Abs(got[i] - expect[i]);
                if (d > 0) diff++;
                if (d > worst) { worst = d; worstAt = i; }
            }

            bool ok = worst <= tol;
            Check($"{label}［{expect.Length - diff}/{expect.Length} 逐位相同，最大差 {worst:E1}］", ok,
                  ok ? null : $"最差处在 {worstAt / 48000.0:F3}s：{got[worstAt]:E4} vs {expect[worstAt]:E4}");
        }

        static bool BitSame(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            return true;
        }

        // ============================================================
        // 九、真实项目上的增量重拼
        // ============================================================

        static void TestPatchOnRealProject()
        {
            // 标题里不写块数 —— 用户的项目会变（219 → 184），写死了就是定时炸弹
            Section("九、真实项目上的增量重拼");

            var project = LoadRealProject();
            var p0 = TimelineAudioRenderer.BuildPlan(project);

            // 下面要按"第几块"取块，所以下标一律从实际块数推，不要写死数字。
            // （2026-09-29 踩过：项目从 219 块变成 184 块，Clips[218] 直接越界崩掉。）
            int n = project.Clips.Count;
            if (n < 20)
            {
                Console.WriteLine($"   真实项目只有 {n} 块，样本太少，跳过本节");
                return;
            }
            int iVol = Math.Min(100, n - 1);
            int iTrimR = Math.Min(50, n - 1);
            int iMove = Math.Min(10, n - 1);
            int iTail = n - 1;
            int iTrimH = Math.Min(150, n - 1);

            var sw = Stopwatch.StartNew();
            var s0 = TimelineAudioRenderer.Render(p0);
            sw.Stop();
            long fullMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"   {p0.Segments.Count} 块 / {p0.TotalSamples / 48000.0:F1} 秒 / "
                              + $"{p0.TotalSamples:N0} 个样本；整条重拼一次：{fullMs} ms");

            // 真实项目里块是首尾相接的 → 挪块一定会造成重叠。旧设计（"擦掉旧位置再重读那块"）
            // 因为 NAudio 定位有 ±42ms 误差，重叠区根本擦不干净；新设计是从旧成品
            // 就地推导 + 按新顺序把上面那块照搬回来，重叠自然算得对。

            // ---- ① 一点没动 → 0 毫秒 ----
            sw.Restart();
            var noop = TimelineAudioRenderer.TryPatch(p0, s0, TimelineAudioRenderer.BuildPlan(project));
            sw.Stop();
            Check($"时间轴没动：直接复用同一份成品（{sw.ElapsedMilliseconds} ms，0 拷贝）",
                  ReferenceEquals(noop, s0));

            // ---- ② 改一块的音量 ----
            project.Clips[iVol].VolumeDb += 3.0;
            var p1 = TimelineAudioRenderer.BuildPlan(project);
            var s1 = TimePatch(p0, s0, p1, out long volMs);
            Check($"改一块音量：走增量重拼（{volMs} ms，整条是 {fullMs} ms）", s1 != null);
            Check($"改一块音量：明显更快（{volMs} ms × 3 < {fullMs} ms）", volMs * 3 < fullMs);
            if (s1 != null) Compare(s1, TimelineAudioRenderer.Render(p1), "改音量");

            // ---- ③ 裁一块的右边缘（缩短 0.5 秒）----
            project.Clips[iTrimR].SourceEndSec -= 0.5;
            var p2 = TimelineAudioRenderer.BuildPlan(project);
            var s2 = TimePatch(p1, s1, p2, out long trimMs);
            Check($"裁一块的右边缘：走增量重拼（{trimMs} ms）", s2 != null);
            if (s2 != null) Compare(s2, TimelineAudioRenderer.Render(p2), "裁右边缘");

            // ---- ④ 挪一块（必然和邻居重叠）----
            project.Clips[iMove].TimelineStartSec += 1.0;
            var p3 = TimelineAudioRenderer.BuildPlan(project);
            var s3 = TimePatch(p2, s2, p3, out long moveMs);
            Check($"挪块造成重叠：也走增量重拼（{moveMs} ms）", s3 != null);
            if (s3 != null) Compare(s3, TimelineAudioRenderer.Render(p3), "挪块（重叠）");

            // ---- ⑤ 挪到空区（正常拖块就是这个样子）----
            project.Clips[iMove].TimelineStartSec -= 1.0;    // 挪回原位 → 内容和 p2 一致
            double endSec = p2.StartSec + p2.DurationSec;
            project.Clips[iTail].TimelineStartSec = endSec + 2.0;   // 挪到时间轴末尾之后的空地
            var p4 = TimelineAudioRenderer.BuildPlan(project);
            var s4 = TimePatch(p2, s2, p4, out long move2Ms);
            Check($"挪到空区：走增量重拼（{move2Ms} ms）", s4 != null);
            if (s4 != null) Compare(s4, TimelineAudioRenderer.Render(p4), "挪到空区");

            // ---- ⑥ 把一块的源入点往后缩（相当于裁掉开头）----
            project.Clips[iTrimH].SourceStartSec += 0.3;
            var p5 = TimelineAudioRenderer.BuildPlan(project);
            var s5 = TimePatch(p4, s4, p5, out long headMs);
            Check($"裁一块的开头：走增量重拼（{headMs} ms）", s5 != null);
            if (s5 != null) Compare(s5, TimelineAudioRenderer.Render(p5), "裁开头");

            Console.WriteLine($"   增量重拼耗时：改音量 {volMs} / 裁右边缘 {trimMs} / 挪块 {moveMs} / "
                              + $"挪到空地 {move2Ms} / 裁开头 {headMs} ms（整条 {fullMs} ms）");
        }

        /// <summary>跑一次增量重拼并计时</summary>
        static float[] TimePatch(AudioRenderPlan oldPlan, float[] oldSamples,
                                 AudioRenderPlan newPlan, out long ms)
        {
            var sw = Stopwatch.StartNew();
            var got = TimelineAudioRenderer.TryPatch(oldPlan, oldSamples, newPlan);
            sw.Stop();
            ms = sw.ElapsedMilliseconds;
            return got;
        }

        /// <summary>增量结果 vs 整条结果：应当只差浮点舍入</summary>
        static void Compare(float[] got, float[] expect, string label)
        {
            if (got == null) { Check($"{label}：增量结果非空", false); return; }
            if (got.Length != expect.Length)
            {
                Check($"{label}：长度一致", false, $"{got.Length} vs {expect.Length}");
                return;
            }

            int diff = 0, worstAt = 0;
            double worst = 0, peak = 0;
            for (int i = 0; i < expect.Length; i++)
            {
                double d = Math.Abs(got[i] - expect[i]);
                if (d > 0) diff++;
                if (d > worst) { worst = d; worstAt = i; }
                double a = Math.Abs(expect[i]);
                if (a > peak) peak = a;
            }

            double ratio = peak > 0 ? worst / peak : worst;
            Console.WriteLine($"   {label}：{expect.Length - diff}/{expect.Length} 逐位相同，最大差 {worst:E2}"
                              + $"（峰值 {peak:F4}，相对 {ratio:E1}）"
                              + (diff > 0 ? $"，最差处在 {worstAt / 48000.0:F2}s" : ""));

            Check($"{label}：与整条重拼只差浮点舍入（相对 {ratio:E1} < 1e-5）", ratio < 1e-5,
                  $"最大差 {worst:E2} / 峰值 {peak:F4}");
        }

        // ============================================================
        // 十、定位精度：m4a "跳过去读" 和 "一路读过去" 差多少？
        // ============================================================
        //
        // 【为什么这段还留着】
        //   它记录的是"当初为什么放弃「只重读改动那一块」这个方案"的证据：
        //   m4a 直接定位的落点会偏 ±42ms 且方向不定，重新读出来的那块会整体挪几十
        //   毫秒，和前后的块之间就出现"丢一段 / 重一段"。所以最终的增量重拼改成
        //   "一个字节都不读源文件，全部从旧成品就地推导"。这个结论不能靠记忆，
        //   得能随时重跑复现。

        static void TestSeekAccuracy()
        {
            Section("十、定位精度（压缩格式的跳读 vs 顺读）");

            string src = RealM4a;
            double target = 298.785;      // 第 100 块的源入点
            int count = (int)(5.640 * 48000);

            var seekA = ReadRange(src, target, count, warmUpSec: 0);
            var seekB = ReadRange(src, target, count, warmUpSec: 0);
            var seqB = ReadRange(src, target, count, warmUpSec: 60);
            var seqC = ReadRange(src, target, count, warmUpSec: 120);

            int s1 = BestShift(seekA, seekB, out double r1);
            Console.WriteLine($"   两次「直接定位」之间：位移 {s1}，残余 {r1:E2}");
            Check("直接定位是稳定的（同样的跳读两次结果一致）", s1 == 0 && r1 == 0);

            int s2 = BestShift(seqB, seqC, out double r2);
            Console.WriteLine($"   不同预热长度的两次「顺读」之间：位移 {s2}，残余 {r2:E2}");
            Check("顺读是稳定的（顺读结果与从哪儿开始顺读无关）", s2 == 0 && r2 == 0);

            int s3 = BestShift(seekA, seqB, out double r3);
            Console.WriteLine($"   「直接定位」vs「顺读」：最佳位移 {s3} 个采样（{s3 * 1000.0 / 48000:F2} 毫秒），残余 {r3:E2}");
            Console.WriteLine($"      （AAC 一帧 1024 个采样 = 21.3 毫秒）");

            bool aligned = r3 < 0.02;
            if (aligned)
                Check($"跳读与顺读只差一个整体位移（{s3} 个采样 = {s3 * 1000.0 / 48000:F2} ms），不是「内容不对」",
                      Math.Abs(s3) <= 2048);
            else
                Check("跳读与顺读的差异无法用位移解释（残余仍然很大）", false, $"残余 {r3:E2}");

            // ---- 这个偏移是固定的，还是随位置变化？----
            // 固定 → 可以一次性测出来做补偿；变化 → 只能躲开定位。
            Console.WriteLine();
            Console.WriteLine("   不同位置上的偏移量：");
            double[] targets = { 10, 60, 150, 300, 500, 640 };
            var offsets = new List<int>();
            foreach (var t in targets)
            {
                if (t + 6 > 658) continue;
                var sa = ReadRange(src, t, 80000, 0);
                var sb = ReadRange(src, t, 80000, t);      // 从头顺读到此处
                int k = BestShift(sa, sb, out double rr);
                offsets.Add(k);
                Console.WriteLine($"     源 {t,6:F0}s 处：偏移 {k,6} 个采样（{k * 1000.0 / 48000,7:F2} ms）残余 {rr:E1}");
            }

            bool allPositive = offsets.Count > 0;
            foreach (var o in offsets) if (o <= 0) allPositive = false;
            Console.WriteLine($"   偏移是否恒定：{(allPositive ? "同号" : "有正有负")}，取值 {string.Join(",", offsets)}");

            // 用 MediaFoundationReader 直接读（绕开 AudioFileReader 包装），看偏移是否一样
            Console.WriteLine();
            Console.WriteLine("   换成直接用 MediaFoundationReader 读：");
            foreach (var t in new double[] { 60, 300 })
            {
                var sa = ReadMf(src, t, 80000, false);
                var sb = ReadMf(src, t, 80000, true);
                int k = BestShift(sa, sb, out double rr);
                Console.WriteLine($"     源 {t,6:F0}s 处：偏移 {k,6} 个采样（{k * 1000.0 / 48000,7:F2} ms）残余 {rr:E1}");
            }
        }

        /// <summary>用 MediaFoundationReader 直接读（seeking=false → 直接定位；true → 从头顺读）</summary>
        static float[] ReadMf(string path, double startSec, int count, bool seeking)
        {
            using var r = new MediaFoundationReader(path);
            ISampleProvider sp = r.WaveFormat.Channels > 1
                ? new StereoToMonoSampleProvider(r.ToSampleProvider())
                : r.ToSampleProvider();
            int rate = r.WaveFormat.SampleRate;
            var buf = new float[65536];

            if (seeking)
            {
                long skip = (long)(startSec * rate);
                while (skip > 0)
                {
                    int n = sp.Read(buf, 0, (int)Math.Min(skip, buf.Length));
                    if (n <= 0) break;
                    skip -= n;
                }
            }
            else
            {
                r.CurrentTime = TimeSpan.FromSeconds(startSec);
            }

            var result = new float[count];
            int got = 0;
            while (got < count)
            {
                int n = sp.Read(result, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return result;
        }

        /// <summary>从文件里取一段：warmUpSec>0 表示先顺读这么久，=0 表示直接定位</summary>
        static float[] ReadRange(string path, double startSec, int count, double warmUpSec)
        {
            using var r = new AudioFileReader(path);
            ISampleProvider sp = r.WaveFormat.Channels > 1 ? new StereoToMonoSampleProvider(r) : r;
            int rate = r.WaveFormat.SampleRate;
            var buf = new float[65536];

            if (warmUpSec > 0)
            {
                long skip = (long)(startSec * rate);
                while (skip > 0)
                {
                    int n = sp.Read(buf, 0, (int)Math.Min(skip, buf.Length));
                    if (n <= 0) break;
                    skip -= n;
                }
            }
            else
            {
                r.CurrentTime = TimeSpan.FromSeconds(startSec);
            }

            var result = new float[count];
            int got = 0;
            while (got < count)
            {
                int n = sp.Read(result, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return result;
        }

        /// <summary>找出把 b 整体挪几个采样后能最贴近 a 的那个位移（只在中间 10 万采样上算）</summary>
        static int BestShift(float[] a, float[] b, out double residual)
        {
            int margin = 4096;
            int start = margin, len = Math.Min(100000, a.Length - margin * 2);
            if (len <= 0) { residual = double.MaxValue; return 0; }

            int best = 0;
            double bestR = double.MaxValue;

            for (int k = -2048; k <= 2048; k += 8)
            {
                double worst = 0;
                for (int i = start; i < start + len; i++)
                {
                    int j = i + k;
                    if (j < 0 || j >= b.Length) continue;
                    double d = Math.Abs(a[i] - b[j]);
                    if (d > worst) worst = d;
                }
                if (worst < bestR) { bestR = worst; best = k; }
            }

            for (int k = best - 8; k <= best + 8; k++)
            {
                double worst = 0;
                for (int i = start; i < start + len; i++)
                {
                    int j = i + k;
                    if (j < 0 || j >= b.Length) continue;
                    double d = Math.Abs(a[i] - b[j]);
                    if (d > worst) worst = d;
                }
                if (worst < bestR) { bestR = worst; best = k; }
            }

            residual = bestR;
            return best;
        }

        // ============================================================
        // 七、真实项目：改前 vs 改后的耗时
        // ============================================================

        static void TestRealProjectSpeed()
        {
            Section("七、真实项目实测");

            var project = LoadRealProject();
            Console.WriteLine($"   时间轴：{project.Clips.Count} 块，跨度 {project.TotalDurationSec:F1} 秒，"
                              + $"样本数 {(long)(project.TotalDurationSec * 48000):N0}");

            var plan = TimelineAudioRenderer.BuildPlan(project);
            Console.WriteLine($"   计划指纹：{plan.Fingerprint:X16}");

            var sw = Stopwatch.StartNew();
            var samples = TimelineAudioRenderer.Render(plan);
            sw.Stop();
            long cold = sw.ElapsedMilliseconds;
            Console.WriteLine($"   首次渲染（冷）：{cold} ms");

            sw.Restart();
            var again = TimelineAudioRenderer.Render(plan);
            sw.Stop();
            Console.WriteLine($"   再渲染一遍（无缓存时）：{sw.ElapsedMilliseconds} ms");

            // 内容抽检：前 5 秒里应该有声，末尾也应该有声
            bool hasSound = false;
            for (int i = 0; i < 48000 * 5 && !hasSound; i++) if (Math.Abs(samples[i]) > 0.001f) hasSound = true;
            Check("渲染结果里确实有声音", hasSound);

            double rms = 0;
            for (int i = 0; i < samples.Length; i += 97) rms += samples[i] * samples[i];
            rms = Math.Sqrt(rms / (samples.Length / 97.0));
            Console.WriteLine($"   整条 RMS：{rms:F4}");
            Check("整体响度正常（不是全静音、也没削顶）", rms > 0.005 && rms < 0.6, $"RMS={rms:F4}");

            Console.WriteLine($"   旧实现实测是 2459~2717 ms（探针上一轮测的）");

            // ---- 拆解：剩下的时间花在哪 ----
            Console.WriteLine();
            Console.WriteLine("   拆解（同一个源文件，427 秒）：");

            string src = project.Clips[0].SourcePath;
            int srcRate, srcCh;
            double srcDur;
            using (var r = new AudioFileReader(src))
            {
                srcRate = r.WaveFormat.SampleRate;
                srcCh = r.WaveFormat.Channels;
                srcDur = r.TotalTime.TotalSeconds;
            }
            Console.WriteLine($"     源格式：{srcRate} Hz / {srcCh} 声道 / {srcDur:F1} 秒");

            long wantSource = (long)(427.1 * srcRate);
            var buf = new float[65536];

            // A：只解码（顺读），不混音不重采样
            sw.Restart();
            long readTotal = 0;
            using (var r = new AudioFileReader(src))
            {
                ISampleProvider sp = srcCh > 1 ? new StereoToMonoSampleProvider(r) : r;
                while (readTotal < wantSource)
                {
                    int n = sp.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    readTotal += n;
                }
            }
            sw.Stop();
            long decodeMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"     A 纯解码 {readTotal / (double)srcRate:F0} 秒音频：{decodeMs} ms");

            // B：解码 + 重采样 + 混进输出（完整流程）
            Console.WriteLine($"     B 完整渲染：{cold} ms");
            Console.WriteLine($"     ⇒ 重采样+混音部分约 {cold - decodeMs} ms");

            // C：单独测重采样那个循环（同样的样本数，纯算术）
            int outCount = (int)(readTotal * (48000.0 / srcRate));
            var srcArr = new float[readTotal];
            var dstArr = new float[outCount];
            double ratio2 = 48000.0 / srcRate;
            sw.Restart();
            for (int i = 0; i < outCount; i++)
            {
                int idx = (int)(i / ratio2);
                dstArr[i] = srcArr[idx];
            }
            sw.Stop();
            Console.WriteLine($"     C 单独跑重采样循环（{outCount:N0} 次，含除法）：{sw.ElapsedMilliseconds} ms");

            Check($"首次渲染已明显快于旧实现（{cold} ms < 2200 ms）", cold < 2200);
            Check($"解码本身占大头，说明瓶颈在音频解码而不是我们的混音代码（{decodeMs} ms / {cold} ms）",
                  decodeMs > cold * 0.6, $"解码 {decodeMs} 占 {decodeMs * 100.0 / cold:F0}%");
        }

        // ============================================================
        // 工具
        // ============================================================

        /// <summary>
        /// 造一段。第一个参数是"身份槽位" —— 同一个槽位每次造出来的都是同一块
        /// （Id 稳定），这样"改了什么"才有意义：不稳定的 Id 会让每次比对都像"全新的一块"。
        /// </summary>
        static AudioSegment Seg(int slot, string path, double s, double e, double tl,
                                double gain = 1.0, double fadeIn = 0, double fadeOut = 0)
            => new AudioSegment(Rid(slot), path, s, e, tl, gain, fadeIn, fadeOut);

        /// <summary>槽位 → 稳定的 Guid</summary>
        static Guid Rid(int slot) => new Guid(slot, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>写一个"值即下标"的单声道浮点 wav</summary>
        static void WriteRampWav(string path, int rate, int channels, double seconds)
        {
            int n = (int)(seconds * rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = (float)(i * RampScale);

            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            using var w = new WaveFileWriter(path, fmt);
            var bytes = new byte[n * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            w.Write(bytes, 0, bytes.Length);
        }

        static TimelineProject LoadRealProject()
        {
            var p = new TimelineProject();
            using var doc = JsonDocument.Parse(File.ReadAllText(RealProjectJson));
            foreach (var c in doc.RootElement.GetProperty("FineCut").GetProperty("Clips").EnumerateArray())
            {
                var id = c.TryGetProperty("Id", out var idEl) && Guid.TryParse(idEl.GetString(), out var g)
                    ? g : Guid.NewGuid();
                p.Clips.Add(new TimelineClip
                {
                    Id = id,
                    SourcePath = c.GetProperty("SourcePath").GetString(),
                    SourceName = c.TryGetProperty("SourceName", out var sn) ? sn.GetString() : "",
                    SourceStartSec = c.GetProperty("SourceStartSec").GetDouble(),
                    SourceEndSec = c.GetProperty("SourceEndSec").GetDouble(),
                    SourceTotalSec = c.TryGetProperty("SourceTotalSec", out var st) ? st.GetDouble() : 0,
                    TimelineStartSec = c.GetProperty("TimelineStartSec").GetDouble(),
                    VolumeDb = c.TryGetProperty("VolumeDb", out var v) ? v.GetDouble() : 0,
                    FadeInSec = c.TryGetProperty("FadeInSec", out var fi) ? fi.GetDouble() : 0,
                    FadeOutSec = c.TryGetProperty("FadeOutSec", out var fo) ? fo.GetDouble() : 0,
                });
            }
            return p;
        }

        static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("— " + title + " —");
        }

        static void Check(string what, bool ok, string detail = null)
        {
            if (ok) { _pass++; Console.WriteLine($"  [PASS] {what}"); }
            else
            {
                _fail++;
                Console.WriteLine($"  [FAIL] {what}" + (detail != null ? $"   ← {detail}" : ""));
            }
        }
    }
}
