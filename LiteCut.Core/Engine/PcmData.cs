// ============================================================
// PcmData.cs - 解码后的音频数据（纯数据，无平台依赖）
// ============================================================
//
// 【为什么要有这一层】
//
//   Core 是跨平台库，不能引用 NAudio（NAudio 只跑 Windows）。
//   但算法需要读到采样点。怎么办？
//
//   把"解码"和"算"分开：
//     - 解码  = 平台活（Windows 用 NAudio，Mac 用 AVAudioEngine），
//               由界面层实现 IAudioDecoder 接口
//     - 计算  = 纯数学（只认 float[]），放在 Core，任何平台都能跑
//
//   这样将来换 Mac，只换解码器那一个类，算法一行不动。
//
// 【为什么固定用单声道】
//
//   说话人只有一个，声道是冗余的。合并成单声道后数据量减半，
//   而且能量计算不会因为左右声道相位差而抖动。
//
// ============================================================

using System;

namespace LiteCut.Core.Engine;

/// <summary>
/// 一段解码后的音频：单声道、32 位浮点、固定采样率。
/// </summary>
public class PcmData
{
    /// <summary>采样点。取值理论上在 -1 ~ 1 之间。</summary>
    public float[] Samples { get; set; } = Array.Empty<float>();

    /// <summary>采样率（Hz）。本项目统一降到 16000——语音分析够用，数据量小。</summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>总时长（秒）。</summary>
    public double DurationSec => SampleRate > 0 ? (double)Samples.Length / SampleRate : 0;

    /// <summary>取一个采样点（越界返回 0，避免算法里到处写边界判断）。</summary>
    public float this[int i] => (i >= 0 && i < Samples.Length) ? Samples[i] : 0f;

    /// <summary>把秒换算成采样点下标。</summary>
    public int SecToIndex(double sec) => (int)(sec * SampleRate);

    /// <summary>把采样点下标换算成秒。</summary>
    public double IndexToSec(int index) => (double)index / SampleRate;
}

/// <summary>
/// 解码器接口。界面层实现它（Windows 用 NAudio，Mac 用 AVAudioEngine）。
///
/// Core 只认这个接口，不认具体平台库 —— 这就是"平台能力走接口"那条规矩的落地。
/// </summary>
public interface IAudioDecoder
{
    /// <summary>
    /// 把音频文件解码成单声道 32 位浮点。
    /// </summary>
    /// <param name="path">文件路径（m4a / mp3 / wav / mp4 都行）</param>
    /// <param name="targetSampleRate">重采样到多少 Hz（建议 16000）</param>
    PcmData Decode(string path, int targetSampleRate = 16000);
}
