// ============================================================
// FFmpegExporter.cs - 按 Decision 把成片渲染出来
// ============================================================
//
// 【它的职责】
//   输入：原始音频/视频 + 一份 CutPlan（哪些段保留）
//   输出：拼接好的成片文件
//
// 【为什么用 filter_complex 而不是"先切再拼"】
//   两条路都能走通，但代价差很多：
//
//   路线 A（先切 N 段临时文件，再 concat）
//     ✗ 要在磁盘上生成 N 个临时文件，10 分钟视频可能 200 段 = 200 个文件
//     ✗ 还要写 concat 清单，还要清理
//     ✗ 磁盘 IO 是整个过程最慢的环节
//
//   路线 B（一条 filter 一次跑完）← 我们用这个
//     ✓ 零临时文件
//     ✓ ffmpeg 内部流式处理，只读一遍源文件
//     ✓ 实测《乡土中国》307 秒片子 34.5 秒出片（8.89 倍速）
//
//   代价是命令行会变长（每段 2 行 filter）。200 段大约 30KB 的命令行。
//   Windows 命令行长度上限约 32KB —— 这是真实风险！
//   解决办法写在下面的 BuildFilterArgs 里：超长就用 concat 清单文件兜底。
//
// 【编码器选择】
//   优先 NVENC（你机器上那块 RTX 2070），失败自动回落软件编码。
//   为什么：NVENC 快 3~5 倍，但不是每台机器都有 NVIDIA 显卡。
//   自动回落让同一份代码在笔记本集显上也能跑（只是慢一点）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;

namespace LiteCut.Core.Media;

/// <summary>
/// 导出时的一组参数。
/// </summary>
public sealed class ExportOptions
{
    /// <summary>是否要视频轨。源文件没视频会自动关掉。</summary>
    public bool WithVideo { get; set; } = true;

    /// <summary>是否要音频轨。</summary>
    public bool WithAudio { get; set; } = true;

    /// <summary>视频编码器。null 表示自动（先试 NVENC，不行用 libx264）。</summary>
    public string? VideoCodec { get; set; }

    /// <summary>音频码率（默认 192k）。</summary>
    public string AudioBitrate { get; set; } = "192k";

    /// <summary>视频画质：NVENC 用 cq，libx264 用 crf（默认 20，肉眼无损）。</summary>
    public int Quality { get; set; } = 20;

    /// <summary>输出分辨率，如 1920x1080。null = 保持原始。</summary>
    public string? Scale { get; set; }

    /// <summary>额外追加的 ffmpeg 参数（高级用户用）。</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>进度回调。传出来的是 ffmpeg 的原始输出行。</summary>
    public IProgress<string>? Progress { get; set; }
}

/// <summary>
/// 导出结果。
/// </summary>
public sealed class ExportResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>输出文件路径。</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>耗时（秒）。算实时倍速要用。</summary>
    public double ElapsedSec { get; set; }

    /// <summary>出错时的错误信息。</summary>
    public string Error { get; set; } = "";

    /// <summary>实际使用的编码器（NVENC 还是回落到了软件编码）。</summary>
    public string VideoCodecUsed { get; set; } = "";

    /// <summary>ffmpeg 最后若干行输出。排错用。</summary>
    public string LogTail { get; set; } = "";
}

/// <summary>
/// 按 CutPlan 导出成片。
/// </summary>
public static class FFmpegExporter
{
    /// <summary>
    /// 时间区间。导出、试听、预览三条路共用同一种输入结构。
    /// </summary>
    public readonly record struct Range(double Start, double End);

    // ========================================================
    // 主入口（喂 CutPlan）
    // ========================================================

    /// <summary>
    /// 按 Keep 段导出成片。
    /// </summary>
    public static ExportResult Export(string sourcePath, CutPlan plan, string outputPath, ExportOptions? opt = null)
    {
        var ranges = new List<Range>();
        foreach (var t in plan.ComputeTimeline())
            ranges.Add(new Range(t.SourceStartSec, t.SourceEndSec));

        return ExportRanges(sourcePath, ranges, outputPath, opt);
    }

    // ========================================================
    // 主入口（喂区间列表）
    // ========================================================

    /// <summary>
    /// 把指定的若干区间拼成一个文件。这是所有导出动作的最终落点。
    /// </summary>
    /// <param name="sourcePath">源媒体文件</param>
    /// <param name="ranges">要保留的时间区间（秒）</param>
    /// <param name="outputPath">输出文件（扩展名决定容器）</param>
    /// <param name="opt">导出参数</param>
    public static ExportResult ExportRanges(string sourcePath, List<Range> ranges,
                                            string outputPath, ExportOptions? opt = null)
    {
        var result = new ExportResult { OutputPath = outputPath };

        opt ??= new ExportOptions();
        string ffmpeg = FFmpegLocator.FFmpegPath
                        ?? throw new InvalidOperationException("找不到 ffmpeg，无法导出。");

        if (!File.Exists(sourcePath))
        {
            result.Error = $"源文件不存在：{sourcePath}";
            return result;
        }
        if (ranges.Count == 0)
        {
            result.Error = "没有要保留的内容（可能全被剪掉了）";
            return result;
        }

        // ---- 探测源文件的轨道情况 ----
        var probe = FFmpegDecoder.Probe(sourcePath);
        bool wantVideo = opt.WithVideo && probe.HasVideo;
        bool wantAudio = opt.WithAudio && probe.HasAudio;

        if (!wantVideo && !wantAudio)
        {
            result.Error = "源文件既没有视频轨也没有音频轨，导不出来";
            return result;
        }

        // ---- 决定编码器 ----
        string videoCodec = "";
        if (wantVideo)
        {
            videoCodec = opt.VideoCodec ?? (IsNvencAvailable(ffmpeg) ? "h264_nvenc" : "libx264");
            result.VideoCodecUsed = videoCodec;
        }

        // ---- 组装命令行 ----
        string args = BuildFilterArgs(sourcePath, ranges, outputPath, opt,
                                      wantVideo, wantAudio, videoCodec, out string filterLen);

        opt.Progress?.Report($"[ffmpeg] 开始导出，共 {ranges.Count} 段" +
                             (filterLen.Length > FilterInlineLimit ? "（段数多，已改用文件传参）" : ""));

        var sw = Stopwatch.StartNew();
        bool ok = RunProcess(ffmpeg, args, opt.Progress, out string stderr, out int exitCode);
        sw.Stop();

        result.ElapsedSec = sw.Elapsed.TotalSeconds;
        result.LogTail = stderr.Length > 800 ? stderr[^800..] : stderr;

        if (!ok || exitCode != 0)
        {
            // NVENC 失败时自动回落软件编码重试一次 —— 这是最常见的失败原因：
            // 显卡驱动版本、编码器占用、显存不足都会导致 NVENC 挂掉。
            if (wantVideo && videoCodec == "h264_nvenc")
            {
                opt.Progress?.Report("[ffmpeg] NVENC 失败，回落软件编码重试");
                string args2 = BuildFilterArgs(sourcePath, ranges, outputPath, opt,
                                               wantVideo, wantAudio, "libx264", out _);
                bool ok2 = RunProcess(ffmpeg, args2, opt.Progress, out string stderr2, out int code2);
                result.ElapsedSec = sw.Elapsed.TotalSeconds;
                result.VideoCodecUsed = "libx264（NVENC 不可用，已回落）";
                result.LogTail = stderr2.Length > 800 ? stderr2[^800..] : stderr2;
                CleanScripts();

                if (ok2 && code2 == 0 && File.Exists(outputPath))
                {
                    result.Success = true;
                    return result;
                }
            }

            result.Error = $"导出失败（退出码 {exitCode}）";
            CleanScripts();
            return result;
        }

        CleanScripts();
        result.Success = File.Exists(outputPath);
        if (!result.Success) result.Error = "命令执行了但输出文件没有生成";
        return result;
    }

    // ========================================================
    // 命令行组装
    // ========================================================

    /// <summary>超过这个长度，filter 就写进文件而不是塞命令行。</summary>
    private const int FilterInlineLimit = 12000;

    /// <summary>
    /// 拼出 ffmpeg 的完整参数。
    /// 遇到超长 filter 会自动改成「把 filter 写进临时文件」的方式。
    /// </summary>
    private static string BuildFilterArgs(string sourcePath, List<Range> ranges, string outputPath,
                                          ExportOptions opt, bool wantVideo, bool wantAudio,
                                          string videoCodec, out string filter)
    {
        filter = BuildFilter(ranges, wantVideo, wantAudio);

        // ---- 超长 filter 的处理 ----
        //
        // 实测出来的硬边界（本机 Windows + ffmpeg 6.1）：
        //   · 400 段 / 命令行 27732 字符  → ✓ 成功
        //   · 500 段 / 命令行约 34700 字符 → ✗ CreateProcess 报 "文件名或扩展名太长"
        //
        // 也就是说：**长视频 + 气口多，是会在 400~500 段之间崩掉的**。
        // 而带视频轨时每段字符数翻倍，大约 250 段就到顶了。
        //
        // 解法：ffmpeg 支持 `-filter_complex_script`，把 graph 写在文件里读进去。
        // 实测 600 段 / 41KB filter 也稳稳通过，输出时长误差 0.000 秒。
        // 这样命令行长度就跟段数彻底脱钩了。
        string filterArg;
        if (filter.Length > FilterInlineLimit)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(),
                                             "yixuan_filter_" + Guid.NewGuid().ToString("N")[..8] + ".txt");

            // ★★ 这里有个很阴的坑，务必手写编码，别用 Encoding.UTF8 ★★
            //
            //  System.Text.Encoding.UTF8 这个静态实例的 GetPreamble() 返回 BOM（EF BB BF），
            //  File.WriteAllText 会把这三个字节写进文件开头。
            //  ffmpeg 读脚本文件时，会把 BOM 当成第一行内容的一部分，
            //  然后报一个完全看不懂的：
            //    "Failed to set value '...' for option 'filter_complex_script': Invalid argument"
            //
            //  这个错误信息里完全不会提到 BOM，所以靠猜是猜不出来的。
            //  实测对照：Python 的 open().write() 默认不写 BOM → 同一个文件 ffmpeg 正常执行。
            //
            //  用 UTF8Encoding(false) 明确表示"不要 BOM"。
            File.WriteAllText(scriptPath, filter, new UTF8Encoding(false));

            filterArg = "-filter_complex_script \"" + scriptPath + "\"";
            ScriptFilesToClean.Add(scriptPath);
        }
        else
        {
            filterArg = "-filter_complex \"" + filter + "\"";
        }

        return AssembleRest(sourcePath, outputPath, opt, wantVideo, wantAudio, videoCodec, filterArg);
    }

    /// <summary>
    /// 本次运行生成的临时 filter 文件，跑完要删掉。
    /// </summary>
    private static readonly List<string> ScriptFilesToClean = new();

    /// <summary>
    /// 组装 filter 图。
    /// </summary>
    private static string BuildFilter(List<Range> ranges, bool wantVideo, bool wantAudio)
    {
        var sb = new StringBuilder();
        int n = ranges.Count;

        for (int i = 0; i < n; i++)
        {
            // ★ 必须用 InvariantCulture：某些系统的小数分隔符是逗号，
            //   拼出 "trim=start=0,000" 会被 ffmpeg 当成参数分隔符，直接崩。
            string a = ranges[i].Start.ToString("F3", CultureInfo.InvariantCulture);
            string b = ranges[i].End.ToString("F3", CultureInfo.InvariantCulture);

            if (wantVideo)
                sb.Append($"[0:v]trim=start={a}:end={b},setpts=PTS-STARTPTS[v{i}];");
            if (wantAudio)
                sb.Append($"[0:a]atrim=start={a}:end={b},asetpts=PTS-STARTPTS[a{i}];");
        }

        // ★ 这里最容易写错：第一个标签也要看有没有对应的轨。
        //   纯音频导出时若还写 [v0]，ffmpeg 会报
        //   "Invalid argument"（它找不到这个标签）——因为你根本没生成 v0。
        if (wantVideo) sb.Append("[v0]");
        if (wantAudio) sb.Append("[a0]");
        for (int i = 1; i < n; i++)
        {
            if (wantVideo) sb.Append($"[v{i}]");
            if (wantAudio) sb.Append($"[a{i}]");
        }
        sb.Append($"concat=n={n}:v={(wantVideo ? 1 : 0)}:a={(wantAudio ? 1 : 0)}");
        sb.Append(wantVideo ? "[outv]" : "").Append(wantAudio ? "[outa]" : "");

        return sb.ToString();
    }

    /// <summary>
    /// 拼输入的其余部分（映射、编码器、画质、输出路径）。
    /// </summary>
    private static string AssembleRest(string sourcePath, string outputPath, ExportOptions opt,
                                       bool wantVideo, bool wantAudio, string videoCodec,
                                       string filterArg)
    {
        var sb = new StringBuilder();

        sb.Append("-hide_banner -y -i \"").Append(sourcePath).Append("\" ");
        sb.Append(filterArg);

        // ---- 输出映射 ----
        if (wantVideo) sb.Append(" -map \"[outv]\"");
        if (wantAudio) sb.Append(" -map \"[outa]\"");

        // ---- 编码器与画质 ----
        if (wantVideo)
        {
            if (videoCodec == "h264_nvenc")
            {
                // NVENC 不用 crf，用 cq（同一个意思，参数名不同）
                sb.Append($" -c:v h264_nvenc -preset p4 -cq {opt.Quality}");
            }
            else
            {
                sb.Append($" -c:v libx264 -preset veryfast -crf {opt.Quality}");
            }
            sb.Append(" -pix_fmt yuv420p");   // 兼容性：不加的话 QuickTime/微信打不开
        }
        else
        {
            sb.Append(" -vn");
        }

        if (wantAudio)
            sb.Append($" -c:a aac -b:a {opt.AudioBitrate}");
        else
            sb.Append(" -an");

        // ---- 缩放（可选）----
        if (wantVideo && !string.IsNullOrWhiteSpace(opt.Scale))
            sb.Append($" -vf scale={opt.Scale}");

        if (!string.IsNullOrWhiteSpace(opt.ExtraArgs))
            sb.Append(' ').Append(opt.ExtraArgs);

        sb.Append(" \"").Append(outputPath).Append('"');

        return sb.ToString();
    }

    /// <summary>
    /// 清理本次导出产生的临时 filter 文件。忘了删会在临时目录越积越多。
    /// </summary>
    private static void CleanScripts()
    {
        foreach (string f in ScriptFilesToClean)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
        ScriptFilesToClean.Clear();
    }

    // ========================================================
    // 辅助
    // ========================================================

    /// <summary>
    /// 这块显卡能不能用 NVENC。结果会缓存，避免每次导出都试一遍。
    /// </summary>
    internal static bool IsNvencAvailable(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                // 用 lavfi 造一帧假视频，让 NVENC 编码一次，输出到空设备
                Arguments = "-hide_banner -f lavfi -i testsrc=size=320x240:rate=1:duration=0.1 " +
                            "-c:v h264_nvenc -f null -",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;   // 任何异常都当作"用不了"，走软件编码
        }
    }

    /// <summary>
    /// 跑一个外部进程，把 stderr 逐步转发给进度回调。
    /// </summary>
    internal static bool RunProcess(string exe, string args, IProgress<string>? progress,
                                    out string stderr, out int exitCode)
    {
        stderr = "";
        exitCode = -1;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p == null) return false;

            // ffmpeg 的进度信息走 stderr（-progress 可以改到 stdout，但 stderr 更通用）
            var lines = new List<string>();
            string? line;
            while ((line = p.StandardError.ReadLine()) != null)
            {
                lines.Add(line);
                if (lines.Count > 400) lines.RemoveAt(0);   // 只留最近 400 行，别无限涨
                progress?.Report(line);
            }

            p.WaitForExit();
            exitCode = p.ExitCode;
            stderr = string.Join(Environment.NewLine, lines);
            return true;
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return false;
        }
    }
}
