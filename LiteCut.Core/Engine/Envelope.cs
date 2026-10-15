// ============================================================
// Envelope.cs - 音量包络（把几百万个采样点压成几千个数）
// ============================================================
//
// 【为什么需要包络】
//
//   608 秒 × 16000 Hz = 973 万个采样点。
//   直接拿采样点做比较，既慢又没意义——我们关心的是"哪里响、哪里哑"，
//   不是每个波形的相位。
//
//   包络 = 每 10 毫秒算一个 RMS（均方根音量），得到 6 万个数字。
//   它保留了说话的**节奏**（哪个字重、哪里停顿、句子多长），
//   丢掉了我们不要的细节。
//
// 【为什么这个东西能抓"重复"】
//
//   同一句话重说一遍，音节节奏几乎一模一样。
//   换成包络看，就是两条形状高度重合的曲线。
//   而两句不同的话，就算长度一样，节奏也对不上。
//
//   所以判断"是不是重复"，比的是包络形状，不是文字。
//   这就是为什么**没有转写也能去重**。
//
// ============================================================

using System;

namespace LiteCut.Core.Engine;

/// <summary>包络计算。</summary>
public static class Envelope
{
    /// <summary>
    /// 默认每帧 5 毫秒。
    ///
    /// 这个数不是拍脑袋定的 —— 是标定出来的。
    /// 实测：ffmpeg 的 silencedetect 用的就是"5ms 窗口取峰值"。
    /// 用 5ms 峰值，检测结果跟 ffmpeg 几乎完全一致：
    ///   段数 204（ffmpeg 204）、总长 289.6s（ffmpeg 289.7s）、起点误差 0.008s
    /// 换成 20ms RMS 会多剪 37 秒 —— 那就跟你听过并认可的激进版对不上了。
    /// </summary>
    public const double DefaultFrameSec = 0.005;

    /// <summary>计算用的窗口（5 毫秒，与 ffmpeg 对齐）。</summary>
    public const double DefaultWindowSec = 0.005;

    /// <summary>
    /// 算出音量包络（单位：dBFS，静音约 -60 ~ -90，人声约 -20 ~ -30）。
    /// </summary>
    /// <param name="pcm">解码后的音频</param>
    /// <param name="frameSec">每隔多少秒算一个点（默认 5 毫秒）</param>
    /// <param name="windowSec">每个点用多长的窗口计算（默认 5 毫秒，与 ffmpeg 对齐）</param>
    /// <param name="usePeak">
    /// true = 取窗口内的峰值（与 ffmpeg silencedetect 一致，推荐）；
    /// false = 取 RMS（更平滑，但会把更多地方判成静音，剪得更狠）。
    /// </param>
    public static double[] Build(PcmData pcm, double frameSec = DefaultFrameSec,
                                 double windowSec = DefaultWindowSec, bool usePeak = true)
    {
        int hop = Math.Max(1, (int)(frameSec * pcm.SampleRate));
        int win = Math.Max(1, (int)(windowSec * pcm.SampleRate));
        int frames = pcm.Samples.Length / hop;
        var env = new double[frames];

        for (int f = 0; f < frames; f++)
        {
            int start = f * hop;
            int end = Math.Min(start + win, pcm.Samples.Length);

            if (usePeak)
            {
                // 峰值：窗口内最大的绝对值。
                // 只要有一瞬间有声音，这一帧就算响 —— 这是 ffmpeg 的判法，
                // 偏保守，不容易把字头字尾剪掉。
                double mx = 0;
                for (int i = start; i < end; i++)
                {
                    double a = Math.Abs(pcm.Samples[i]);
                    if (a > mx) mx = a;
                }
                env[f] = ToDb(mx);
            }
            else
            {
                double sum = 0;
                int count = 0;
                for (int i = start; i < end; i++)
                {
                    sum += pcm.Samples[i] * pcm.Samples[i];
                    count++;
                }
                env[f] = ToDb(count > 0 ? Math.Sqrt(sum / count) : 0);
            }
        }
        return env;
    }

    /// <summary>
    /// 把包络重采样成固定长度（默认 64 点）。
    /// 这样两段长短不同的音频也能直接比形状。
    /// </summary>
    public static double[] Resample(double[] src, int srcStart, int srcLen, int outLen = 64)
    {
        var dst = new double[outLen];
        if (srcLen <= 0) return dst;
        for (int i = 0; i < outLen; i++)
        {
            double pos = srcStart + (double)i / outLen * srcLen;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, srcStart + srcLen - 1);
            double frac = pos - i0;
            double a = (i0 >= 0 && i0 < src.Length) ? src[i0] : 0;
            double b = (i1 >= 0 && i1 < src.Length) ? src[i1] : 0;
            dst[i] = a + (b - a) * frac;
        }
        return dst;
    }

    /// <summary>
    /// 皮尔逊相关系数：两条曲线形状有多像（-1 ~ 1，1 = 完全一样）。
    ///
    /// 用相关系数而不是"距离"，是因为相关系数不受音量大小影响——
    /// 同一句重说，第二次可能更大声或更小声，但形状是一样的。
    /// </summary>
    public static double Correlation(double[] a, double[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;

        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;

        double num = 0, da = 0, db = 0;
        for (int i = 0; i < n; i++)
        {
            double x = a[i] - ma;
            double y = b[i] - mb;
            num += x * y;
            da += x * x;
            db += y * y;
        }
        if (da <= 1e-12 || db <= 1e-12) return 0;   // 有一段是平的（纯静音），无从比较
        return num / Math.Sqrt(da * db);
    }

    /// <summary>把振幅换算成分贝。0 振幅给 -120dB（而不是负无穷，避免后续计算炸掉）。</summary>
    public static double ToDb(double amplitude)
    {
        if (amplitude <= 1e-10) return -120.0;
        return 20.0 * Math.Log10(amplitude);
    }
}
