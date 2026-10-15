// ============================================================
// AudioRenderPlan.cs - "这条时间轴要渲染成什么音频"的纯数据描述
//
// 【为什么单独抽出来】
//   播放要出声，先得把时间轴上的块拼成一整条连续音频。这个"拼"的过程
//   很贵（要开文件、解码、重采样），所以必须能缓存 —— 而缓存就得有个
//   稳定的"钥匙"：时间轴内容没变，就用上次拼好的那份，别再拼一遍。
//
//   于是把"要拼什么"和"怎么拼"分开：
//     这一层（Core）：只描述"有哪些段、每段取源文件的哪一段、放在哪"，
//                     并算出一个指纹当缓存钥匙。纯数据，不碰音频库。
//     渲染层（WPF） ：照这份计划去开文件解码（见 TimelineAudioRenderer）。
//
//   放 Core 还有两个好处：
//     1. 能脱离界面单独测（指纹对不对、起点总长算得对不对）；
//     2. Build() 会把传进来的段复制成数组 —— 也就是说它天然是一份"快照"。
//        界面线程调完 Build 就可以随便改时间轴，后台线程拿这份计划去渲染
//        不会踩到"集合已修改"。
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut.Core.Timeline;

/// <summary>
/// 渲染计划里的一段：从某个源文件取 [SourceStartSec, SourceEndSec)，
/// 放在时间轴的 TimelineStartSec 处，带自己的增益和淡入淡出。
/// </summary>
public readonly struct AudioSegment
{
    public AudioSegment(
        Guid id,
        string sourcePath,
        double sourceStartSec,
        double sourceEndSec,
        double timelineStartSec,
        double gain,
        double fadeInSec,
        double fadeOutSec)
    {
        Id = id;
        SourcePath = sourcePath ?? "";
        SourceStartSec = sourceStartSec;
        SourceEndSec = sourceEndSec;
        TimelineStartSec = timelineStartSec;
        Gain = gain;
        FadeInSec = fadeInSec;
        FadeOutSec = fadeOutSec;
    }

    /// <summary>
    /// 这块的身份（跟着时间轴上的块走，挪位置、裁切、调音量都不变）。
    /// 增量重拼靠它认出"其实还是同一块，只是挪了地方"。
    /// </summary>
    public Guid Id { get; }

    /// <summary>源素材文件路径</summary>
    public string SourcePath { get; }

    /// <summary>从源素材的第几秒开始取</summary>
    public double SourceStartSec { get; }

    /// <summary>取到源素材的第几秒结束</summary>
    public double SourceEndSec { get; }

    /// <summary>放在时间轴的第几秒</summary>
    public double TimelineStartSec { get; }

    /// <summary>线性增益（不是 dB —— dB 换算在构造计划时就做完了）</summary>
    public double Gain { get; }

    /// <summary>淡入时长（秒）</summary>
    public double FadeInSec { get; }

    /// <summary>淡出时长（秒）</summary>
    public double FadeOutSec { get; }

    /// <summary>这段有多长（秒）</summary>
    public double SourceLengthSec => SourceEndSec - SourceStartSec;

    /// <summary>这段在时间轴上的结束位置</summary>
    public double TimelineEndSec => TimelineStartSec + SourceLengthSec;
}

/// <summary>
/// 一条时间轴对应的完整渲染计划。
/// </summary>
public sealed class AudioRenderPlan
{
    /// <summary>默认采样率。整条流统一用这个采样率，源素材不是的话在渲染时重采样。</summary>
    public const int DefaultSampleRate = 48000;

    private readonly AudioSegment[] _segments;

    private AudioRenderPlan(AudioSegment[] segments, int sampleRate, double startSec, double durationSec)
    {
        _segments = segments;
        SampleRate = sampleRate;
        StartSec = startSec;
        DurationSec = durationSec;
        TotalSamples = durationSec > 0 ? (int)(durationSec * sampleRate) : 0;
        Fingerprint = ComputeFingerprint(segments, sampleRate, startSec);
    }

    /// <summary>所有段（按调用方给的顺序，一般就是时间轴顺序）</summary>
    public IReadOnlyList<AudioSegment> Segments => _segments;

    /// <summary>统一采样率</summary>
    public int SampleRate { get; }

    /// <summary>时间轴原点（第一段的起点）</summary>
    public double StartSec { get; }

    /// <summary>从第一段起点到最后一段终点的时长</summary>
    public double DurationSec { get; }

    /// <summary>该渲染出多少个样本</summary>
    public int TotalSamples { get; }

    /// <summary>
    /// 内容指纹。同样的时间轴 → 同样的指纹，可以用来当缓存钥匙。
    /// 任何会让声音变样的改动（挪块、裁切、音量、淡化、增删）都会改变它。
    /// </summary>
    public ulong Fingerprint { get; }

    /// <summary>
    /// 造一份渲染计划。
    ///
    /// 【必须是快照】传进来的段会被复制成内部数组。调用方（界面线程）之后
    /// 怎么改时间轴都不会影响这份计划 —— 后台预热线程才能安全地拿它去渲染。
    /// </summary>
    public static AudioRenderPlan Build(IEnumerable<AudioSegment> segments, int sampleRate = DefaultSampleRate)
    {
        if (segments == null) throw new ArgumentNullException(nameof(segments));
        if (sampleRate <= 0) sampleRate = DefaultSampleRate;

        // 顺手丢掉空段和没来源的段：它们渲染出来也是纯静音，留着只会拖慢
        var list = new List<AudioSegment>();
        foreach (var s in segments)
        {
            if (string.IsNullOrWhiteSpace(s.SourcePath)) continue;
            if (s.SourceLengthSec <= 0) continue;
            list.Add(s);
        }

        var array = list.ToArray();

        double startSec = 0;
        double endSec = 0;
        if (array.Length > 0)
        {
            startSec = array[0].TimelineStartSec;
            endSec = startSec;
            foreach (var s in array)
            {
                if (s.TimelineStartSec < startSec) startSec = s.TimelineStartSec;
                if (s.TimelineEndSec > endSec) endSec = s.TimelineEndSec;
            }
        }

        return new AudioRenderPlan(array, sampleRate, startSec, Math.Max(0, endSec - startSec));
    }

    /// <summary>空计划（时间轴上一块都没有时用）</summary>
    public static AudioRenderPlan Empty(int sampleRate = DefaultSampleRate)
        => new(Array.Empty<AudioSegment>(), sampleRate, 0, 0);

    // ============================================================
    // 指纹：FNV-1a 64 位
    // ============================================================
    //
    // 【为什么不存"第几次改动"这种版本号】
    //   版本号要靠人手在每个改动点去 +1，漏一处缓存就变成脏的（听到的还是
    //   改之前的声音）。指纹是"从内容算出来"的，只要内容变了就必定不同，
    //   不依赖任何人记得去标记。

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong ComputeFingerprint(AudioSegment[] segments, int sampleRate, double startSec)
    {
        ulong h = FnvOffset;

        h = Mix(h, (ulong)sampleRate);
        h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(startSec));
        h = Mix(h, (ulong)segments.Length);

        foreach (var s in segments)
        {
            h = MixString(h, s.SourcePath);
            h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(s.SourceStartSec));
            h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(s.SourceEndSec));
            h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(s.TimelineStartSec));
            h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(s.Gain));
            h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(s.FadeInSec));
            h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(s.FadeOutSec));
        }

        return h;
    }

    private static ulong Mix(ulong h, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(value >> (i * 8));
            h *= FnvPrime;
        }
        return h;
    }

    private static ulong MixString(ulong h, string text)
    {
        foreach (char c in text)
        {
            h ^= (byte)(c & 0xFF);
            h *= FnvPrime;
            h ^= (byte)(c >> 8);
            h *= FnvPrime;
        }
        // 结束标记：避免 ["ab","c"] 和 ["a","bc"] 撞成同一个指纹
        h ^= 0x1F;
        h *= FnvPrime;
        return h;
    }
}
