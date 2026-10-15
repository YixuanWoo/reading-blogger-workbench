// ============================================================
// SilenceDetector.cs - 找出"哪里没声音"
// ============================================================
//
// 【这一层解决什么】
//
//   人工剪掉的 237.2 秒里，有 165.2 秒是纯静音/气口。
//   这部分完全不需要听懂内容 —— 看音量就知道。
//
//   这是"工程层"，纯算法，零误伤风险（实测 204 段全部 mean ≤ -40dB）。
//
// 【判据为什么用 dB 而不是振幅】
//
//   人耳对音量的感知是对数的。振幅从 0.01 到 0.001（掉 10 倍），
//   听起来是"掉了一档"，但用 dB 表示就是 -40 到 -60，差 20dB，很直观。
//
// 【⚠️ 一个踩过的坑，记在这里】
//
//   判断"这段是不是静音"，要看**这一段全程的平均音量（mean）**，
//   绝不能看**瞬时峰值（max）**。
//
//   真实案例：一段 25.10 秒的真静音，mean = -60.0dB（确实没声音），
//   但只要里面有一瞬间底噪尖峰，max 就能到 -26.1dB。
//   用 max 判据会得出"这段有人声"的荒谬结论 ——
//   第一版质检就栽在这里，报出 91.7% 的假误伤率。
//   改用 mean 后，误伤率是 0%。
//
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut.Core.Engine;

/// <summary>一段静音区间。</summary>
public readonly struct SilenceSpan
{
    public double StartSec { get; init; }
    public double EndSec { get; init; }
    public double DurationSec => EndSec - StartSec;
    public double MeanDb { get; init; }

    public override string ToString() => $"{StartSec:F2}~{EndSec:F2}s ({DurationSec:F2}s, {MeanDb:F1}dB)";
}

/// <summary>静音检测。</summary>
public static class SilenceDetector
{
    /// <summary>
    /// 在包络上找静音段。
    /// </summary>
    /// <param name="env">音量包络（dBFS）</param>
    /// <param name="frameSec">每帧多少秒</param>
    /// <param name="noiseDb">低于这个 dB 算静音</param>
    /// <param name="minDurSec">短于这个秒数的静音忽略不计</param>
    public static List<SilenceSpan> Detect(double[] env, double frameSec, double noiseDb, double minDurSec)
    {
        var result = new List<SilenceSpan>();
        int i = 0;
        int n = env.Length;

        while (i < n)
        {
            if (env[i] > noiseDb) { i++; continue; }   // 有声音，跳过

            // 找到一段连续低于阈值的区间
            int start = i;
            double sum = 0;
            while (i < n && env[i] <= noiseDb) { sum += env[i]; i++; }
            int end = i;

            double dur = (end - start) * frameSec;
            if (dur < minDurSec) continue;   // 太短，是正常语气停顿，不剪

            // 记录这段的平均音量 —— 质检时要靠它证明"确实没说话"
            double meanDb = (end > start) ? sum / (end - start) : -120.0;

            result.Add(new SilenceSpan
            {
                StartSec = start * frameSec,
                EndSec = end * frameSec,
                MeanDb = meanDb,
            });
        }

        return result;
    }

    /// <summary>
    /// 静音段的补集 = 有声音的段。
    /// 去重算法就在这上面跑 —— 因为"重复"只可能发生在说话的部分。
    /// </summary>
    public static List<SilenceSpan> VoicedRuns(List<SilenceSpan> silences, double totalSec)
    {
        var runs = new List<SilenceSpan>();
        double cursor = 0;

        foreach (var s in silences)
        {
            if (s.StartSec > cursor + 1e-6)
                runs.Add(new SilenceSpan { StartSec = cursor, EndSec = s.StartSec });
            cursor = Math.Max(cursor, s.EndSec);
        }
        if (cursor < totalSec - 1e-6)
            runs.Add(new SilenceSpan { StartSec = cursor, EndSec = totalSec });

        return runs;
    }
}
