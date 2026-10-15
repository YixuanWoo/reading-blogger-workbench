// ============================================================
// FFmpegDecoder.cs - 把任意音频文件变成 PCM
// ============================================================
//
// 【它的职责】
//   Core 里的粗剪引擎只认 PcmData（一串 float，单声道，16kHz）。
//   但用户手里的是 m4a、mp3、mp4、wav…… 引擎不认识这些格式。
//   这个文件就是中间那层翻译。
//
// 【为什么用 ffmpeg 而不是 NAudio】
//   NAudio 有两个硬伤，都是本项目绕不开的：
//     ① m4a(AAC) 在部分 Windows 上需要系统解码器，不一定装了
//     ② NAudio 是 Windows 专用库 —— 放进 Core 会破坏跨平台
//
//   ffmpeg 两个问题都没有：格式全覆盖，而且每个系统都能装。
//   代价是要起一个进程、走一遍标准输出，比直接读文件慢一点。
//   这个代价值得：解一次 10 分钟的音频大约 2~3 秒，用户等得起。
//
// 【跨平台说明】
//   System.Diagnostics.Process 是 .NET 标准库的一部分，Mac/Linux 上一样能用。
//   所以这个文件放进 Core 不会破坏"跨平台判据"。
//   将来换 Mac，只要那台机器上有 ffmpeg，这里一行代码都不用改。
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using LiteCut.Core.Engine;

namespace LiteCut.Core.Media;

/// <summary>
/// 用 ffmpeg 实现的音频解码器。
/// </summary>
public sealed class FFmpegDecoder : IAudioDecoder
{
    /// <summary>
    /// ffmpeg 的路径。不给就用 FFmpegLocator 找出来的那个。
    /// </summary>
    public string FFmpegPath { get; }

    /// <summary>
    /// 构造时可显式指定 ffmpeg 位置；不指定就用自动搜寻的结果。
    /// </summary>
    public FFmpegDecoder(string? ffmpegPath = null)
    {
        FFmpegPath = ffmpegPath ?? FFmpegLocator.FFmpegPath
                     ?? throw new InvalidOperationException(
                         "找不到 ffmpeg。请在工作台里手动指定 ffmpeg.exe 的位置。");
    }

    /// <summary>
    /// 把音频文件解码成单声道浮点 PCM。
    /// </summary>
    /// <param name="path">音频/视频文件路径（m4a / mp3 / wav / mp4 都行）</param>
    /// <param name="targetSampleRate">重采样到多少 Hz</param>
    /// <returns>解码后的 PCM 数据</returns>
    public PcmData Decode(string path, int targetSampleRate = 16000)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("要解码的音频文件不存在", path);

        // --------------------------------------------------------
        // ffmpeg 命令行拆解
        //
        //   -v error        只输出错误信息，别往 stderr 刷进度（我们要解析 stderr）
        //   -i <path>       输入文件
        //   -vn             不要视频轨（纯音频文件时这行是多余的，但无害）
        //   -ac 1           混成单声道（粗剪只需要能量，不需要立体声）
        //   -ar 16000       重采样到 16kHz（16k 对人类语音够用，数据量只有 44.1k 的 36%）
        //   -f f32le        输出裸的 32 位浮点little-endian，不带任何文件头
        //   -               输出到标准输出（不落盘，省一次磁盘 IO）
        //
        // 为什么是 16kHz：
        //   我们要分析的是"哪里有声音、哪里没声音"，不是做音乐母带。
        //   16kHz 覆盖到 8kHz 频率，人声的基频和主要共振峰全在里面。
        //   再高只是让后面的包络计算白白慢几倍。
        // --------------------------------------------------------
        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            Arguments = $"-v error -i \"{path}\" -vn -ac 1 -ar {targetSampleRate} -f f32le -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("ffmpeg 进程启动失败");

        // ---- 一边读 stdout 一边攒到内存缓冲 ----
        // 不用 RedirectStandardOutput 的 ReadToEnd：那个会把全部字节读成一个
        // byte[] 再转 float[]，峰值内存是 2 倍。流式写省一半内存。
        using var stdout = proc.StandardOutput.BaseStream;
        using var ms = new MemoryStream();
        stdout.CopyTo(ms);

        string errText = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        byte[] raw = ms.ToArray();

        if (proc.ExitCode != 0)
        {
            string tail = errText.Length > 500 ? errText[^500..] : errText;
            throw new InvalidOperationException(
                $"ffmpeg 解码失败（退出码 {proc.ExitCode}）：{Environment.NewLine}{tail}");
        }

        if (raw.Length < 4)
            throw new InvalidOperationException(
                $"ffmpeg 没有解出任何音频。文件可能是纯视频，或者音频轨损坏。{Environment.NewLine}{errText}");

        // ---- byte[] → float[] ----
        // f32le 格式：每 4 个字节是一个 IEEE754 单精度浮点，-1.0 ~ +1.0
        float[] samples = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 4);

        return new PcmData
        {
            Samples = samples,
            SampleRate = targetSampleRate,
        };
    }

    // ========================================================
    // 探测：这个文件里到底有没有视频轨
    // ========================================================

    /// <summary>
    /// 查询媒体文件的信息。拿不到就返回保守值（当作有视频）。
    /// </summary>
    /// <param name="path">媒体文件路径</param>
    /// <returns>是否包含视频轨 / 音频轨 / 总时长</returns>
    public static MediaProbe Probe(string path)
    {
        var result = new MediaProbe();

        // ffprobe 不可用或文件不存在 → 保守假设"有视频有音频"，
        // 让流程继续跑；真出问题会在导出时报错，那时候再处理。
        string? probe = FFmpegLocator.FFprobePath;
        if (probe == null || !File.Exists(path))
        {
            result.HasAudio = true;
            result.HasVideo = true;
            result.Error = probe == null ? "没有 ffprobe，按'有视频'处理" : "文件不存在";
            return result;
        }

        var psi = new ProcessStartInfo
        {
            FileName = probe,
            // -show_entries：只要想要的字段，别输出一堆无关信息
            // -of default=nw=1：输出成 key=value 形式，好解析
            Arguments = $"-v error -show_entries format=duration -show_entries " +
                        $"stream=codec_type -of default=nw=1 \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) { result.HasAudio = true; result.HasVideo = true; return result; }

            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);

            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.StartsWith("codec_type=video")) result.HasVideo = true;
                else if (t.StartsWith("codec_type=audio")) result.HasAudio = true;
                else if (t.StartsWith("duration="))
                {
                    if (double.TryParse(t["duration=".Length..], out double d))
                        result.DurationSec = d;
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.HasAudio = true;
            result.HasVideo = true;
        }

        return result;
    }

    /// <summary>
    /// 解码时的人话状态（界面上显示用）。
    /// </summary>
    public override string ToString() => $"FFmpegDecoder({FFmpegPath})";
}

/// <summary>
/// 媒体文件的探测结果。
/// </summary>
public sealed class MediaProbe
{
    /// <summary>有没有视频轨。</summary>
    public bool HasVideo { get; set; }

    /// <summary>有没有音频轨。</summary>
    public bool HasAudio { get; set; }

    /// <summary>总时长（秒）。探测失败是 0。</summary>
    public double DurationSec { get; set; }

    /// <summary>探测过程中的说明或错误。正常时为空。</summary>
    public string Error { get; set; } = "";
}
