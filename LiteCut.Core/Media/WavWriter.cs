// ============================================================
// WavWriter.cs - 把 PCM 写成标准 wav 文件
// ============================================================
//
// 【为什么需要】
//   界面上的"听一下"按钮需要一个能被播放器认出来的文件。
//   但音频源可能是 m4a/mp3，而播放库（NAudio）对某些格式
//   依赖操作系统的解码器 —— 尤其是 ALAC（你的《乡土中国》素材就是），
//   在没有 iTunes/QuickTime 的机器上会直接解不开。
//
//   解决办法：**不依赖任何解码器**。
//   我们自己已经有一份解码好的 float 数组了（比波形算法用过的那份），
//   直接把这份数据写成最朴素的 PCM wav —— 连 30 年前的软件都能播。
//
//   好处：
//     ✓ 零兼容性问题（不用问系统装了什么解码器）
//     ✓ "听粗剪结果"就是把保留段拼起来写一遍，跟导出用的是同一份判断
//     ✓ 播放看到的就是导出听到的，不会出现"预览好听、导出难听"
//
// 【为什么放 Core】
//   写 wav 只是往文件里按格式塞字节，不涉及任何系统 API。
//   将来迁 Mac 一样能用。
// ============================================================

using System;
using System.IO;
using System.Text;
using LiteCut.Core.Engine;

namespace LiteCut.Core.Media;

/// <summary>
/// 把浮点 PCM 写成 16 位 PCM 的 wav 文件。
/// </summary>
public static class WavWriter
{
    /// <summary>
    /// 写成一个单声道 16 位 PCM wav。
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="samples">浮点采样（-1.0 ~ +1.0）</param>
    /// <param name="sampleRate">采样率</param>
    /// <param name="gain">增益倍数，默认 1.0。粗剪预览时用 1.0 最诚实。</param>
    public static void Write16(string path, float[] samples, int sampleRate, float gain = 1.0f)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        int dataBytes = samples.Length * 2;   // 16 位 = 每个采样 2 字节

        // ---- RIFF 头 ----
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);                       // 整个文件大小减去前 8 字节
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        // ---- fmt 子块 ----
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                                   // 本子块长度
        bw.Write((ushort)1);                            // 音频格式：1 = PCM
        bw.Write((ushort)1);                            // 声道数：1 = 单声道
        bw.Write(sampleRate);                           // 采样率
        bw.Write(sampleRate * 2);                       // 字节率 = 采样率 × 声道 × 位深/8
        bw.Write((ushort)2);                            // 块对齐 = 声道 × 位深/8
        bw.Write((ushort)16);                           // 位深

        // ---- data 子块 ----
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);

        // ---- 采样数据 ----
        // float(-1~1) → short(-32768~32767)，超出的要夹紧否则会环绕失真
        // （比如 1.5 会被截断成 -32768，听起来是"啪"一声巨响）
        foreach (float s in samples)
        {
            float v = s * gain;
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            bw.Write((short)(v * 32767f));
        }
    }

    /// <summary>
    /// 从 PcmData 里截取一段，写成 wav。
    /// 做剪辑预览时很方便：只需要某一秒到某一秒。
    /// </summary>
    public static void WriteSlice(string path, PcmData pcm, double startSec, double endSec)
    {
        int i0 = Math.Clamp(pcm.SecToIndex(startSec), 0, pcm.Samples.Length);
        int i1 = Math.Clamp(pcm.SecToIndex(endSec), i0, pcm.Samples.Length);

        float[] slice = new float[i1 - i0];
        Array.Copy(pcm.Samples, i0, slice, 0, slice.Length);
        Write16(path, slice, pcm.SampleRate);
    }

    /// <summary>
    /// 把若干区间拼成一个 wav。这就是"听粗剪结果"的实现。
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="pcm">源波形</param>
    /// <param name="ranges">要拼接的区间（秒）</param>
    public static void WriteConcat(string path, PcmData pcm, System.Collections.Generic.IReadOnlyList<(double Start, double End)> ranges)
    {
        // 先算出总共多少采样，一次性分配好 —— 比 List<float> 反复扩容快得多
        long total = 0;
        foreach (var (a, b) in ranges)
        {
            int i0 = Math.Clamp(pcm.SecToIndex(a), 0, pcm.Samples.Length);
            int i1 = Math.Clamp(pcm.SecToIndex(b), i0, pcm.Samples.Length);
            total += i1 - i0;
        }

        var outBuf = new float[total];
        long pos = 0;
        foreach (var (a, b) in ranges)
        {
            int i0 = Math.Clamp(pcm.SecToIndex(a), 0, pcm.Samples.Length);
            int i1 = Math.Clamp(pcm.SecToIndex(b), i0, pcm.Samples.Length);
            int len = i1 - i0;
            if (len <= 0) continue;
            Array.Copy(pcm.Samples, i0, outBuf, pos, len);
            pos += len;
        }

        Write16(path, outBuf, pcm.SampleRate);
    }
}
