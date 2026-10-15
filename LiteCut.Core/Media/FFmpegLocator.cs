// ============================================================
// FFmpegLocator.cs - 找到机器上能用的 ffmpeg / ffprobe
// ============================================================
//
// 【为什么单独抽出这个文件】
//   工作台要做三件依赖 ffmpeg 的事：
//     ① 把 m4a/mp3/mp4 解码成 PCM（给粗剪引擎）
//     ② 按 CutPlan 把成片渲染出来
//     ③ 探测源文件有没有视频轨
//
//   三件事都得先知道"ffmpeg 在哪台机器的哪个角落"。
//   与其三处各写一遍搜索逻辑（然后版本不一致），不如统一在这里找。
//
// 【为什么不放死路径】
//   你现在这台机器 ffmpeg 在 D:\HyperFrames\_tools\bin\，
//   但换台机器、或者 Mac 上就完全不一样了。
//   硬编码路径 = 换机器必炸。所以这里按顺序找，并且允许用户手动指定一次
//   （记到配置文件里，下次直接读）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace LiteCut.Core.Media;

/// <summary>
/// ffmpeg 的定位与信息查询。
/// </summary>
public static class FFmpegLocator
{
    /// <summary>
    /// ffmpeg 可执行文件的实际路径。找不到就是 null。
    /// </summary>
    public static string? FFmpegPath { get; private set; }

    /// <summary>
    /// ffprobe 可执行文件的实际路径。找不到就是 null。
    /// </summary>
    public static string? FFprobePath { get; private set; }

    /// <summary>
    /// 搜索时先看的地方。按优先级从上往下。
    /// 第一个（用户配置过的）留空，由 Configure 填。
    /// </summary>
    private static readonly List<string> CandidateDirs = new();

    /// <summary>
    /// 已经跑过一次搜索了吗（避免重复 IO）。
    /// </summary>
    private static bool _searched;

    /// <summary>
    /// 用户手动指定的路径优先。传进来就会排在最前面。
    /// </summary>
    /// <param name="ffmpegPath">ffmpeg 的完整路径，可以是 null（表示让用户去猜）</param>
    /// <param name="probePath">ffprobe 的完整路径，可以是 null（默认跟 ffmpeg 同目录）</param>
    public static void Configure(string? ffmpegPath, string? probePath = null)
    {
        CandidateDirs.Clear();

        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            CandidateDirs.Add(ffmpegPath);
            // 指定了 ffmpeg 但没指定 probe → 假设 probe 就在隔壁
            string? dir = Path.GetDirectoryName(ffmpegPath);
            if (!string.IsNullOrEmpty(dir))
                CandidateDirs.Add(Path.Combine(dir, "ffprobe.exe"));
        }

        if (!string.IsNullOrWhiteSpace(probePath))
            CandidateDirs.Add(probePath);

        _searched = false;
    }

    /// <summary>
    /// 常备的候选位置。教科书意义上的"常见安装地点"。
    /// 你的 ffmpeg 在 D:\HyperFrames\_tools\bin\ —— 放在最前面。
    /// </summary>
    private static readonly string[] WellKnownPaths =
    {
        @"D:\HyperFrames\_tools\bin\ffmpeg.exe",
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
    };

    /// <summary>
    /// 执行一次搜索。找到就返回 true。
    /// 已经搜过且成功过的话，直接复用结果（省一次磁盘查询）。
    /// </summary>
    public static bool TryFind()
    {
        if (_searched && FFmpegPath != null) return true;
        _searched = true;

        // ---- 候选 1：用户手动指定 / 配置记录的 ----
        foreach (string? p in CandidateDirs)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (!File.Exists(p)) continue;

            // 名字里带 ffmpeg 的是 ffmpeg，带 probe 的是 ffprobe
            string name = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
            if (name.Contains("ffprobe"))
            {
                FFprobePath ??= p;
                // 顺手在同目录找 ffmpeg
                string? dir = Path.GetDirectoryName(p);
                string? sib = dir == null ? null : Path.Combine(dir, "ffmpeg.exe");
                if (sib != null && File.Exists(sib)) FFmpegPath ??= sib;
            }
            else
            {
                FFmpegPath ??= p;
                string? dir = Path.GetDirectoryName(p);
                string? sib = dir == null ? null : Path.Combine(dir, "ffprobe.exe");
                if (sib != null && File.Exists(sib)) FFprobePath ??= sib;
            }
        }

        // ---- 候选 2：环境变量 FFMPEG ----
        if (FFmpegPath == null)
        {
            string? env = Environment.GetEnvironmentVariable("FFMPEG");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) FFmpegPath = env;
        }

        // ---- 候选 3：PATH 里挨个目录翻 ----
        if (FFmpegPath == null)
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string cand = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                        if (File.Exists(cand)) { FFmpegPath = cand; break; }
                    }
                    catch { /* PATH 里可能有畸形条目，跳过 */ }
                }
            }
        }

        // ---- 候选 4：常见安装地点 ----
        foreach (string p in WellKnownPaths)
        {
            if (FFmpegPath != null) break;
            if (File.Exists(p)) FFmpegPath = p;
        }

        // ffprobe 还没找到 → 跟 ffmpeg 同目录猜一个
        if (FFprobePath == null && FFmpegPath != null)
        {
            string? dir = Path.GetDirectoryName(FFmpegPath);
            string cand = dir == null ? "" : Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(cand)) FFprobePath = cand;
        }

        return FFmpegPath != null;
    }

    /// <summary>
    /// 让用户确认的一句人话描述：现在用的是哪个 ffmpeg。
    /// 界面底部状态栏显示这个，出问题第一时间能定位。
    /// </summary>
    public static string Describe()
    {
        if (FFmpegPath == null) return "未找到 ffmpeg（请手动指定）";
        string probe = FFprobePath != null ? "，ffprobe 已就绪" : "（缺 ffprobe，视频轨道检测会退化）";
        return $"ffmpeg：{FFmpegPath}{probe}";
    }
}
