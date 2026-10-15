// ============================================================
// TimelineAudioRenderer.cs - 按渲染计划把时间轴拼成一整条样本流
//
// 【这个文件修的是什么】
//   原来的实现（BuildTimelineSamples）对每一块都做一次：
//       new AudioFileReader(源文件) → 定位 → 解码 → 重采样 → 拷进大列表
//
//   问题在于"每一块都重新打开一次源文件"。实测你那个项目：
//   219 块全部来自同一个 m4a，光"开 219 次解码器"就花了 791 ms；
//   加上逐块解码和往 List<float> 里一个个 Add，一次要 2.5 秒。
//   而这 2.5 秒是**按一次空格就要走一遍**（播放前现场拼）。
//
//   现在改成：
//     · 一个源文件只开一次（开着不关，后面再用接着读）
//     · 相邻的段用"顺序读"接上，不倒回不重新定位
//       （m4a 这种压缩格式重新定位要重建解码器 + 找关键帧，很贵；
//         顺读还能顺带避开定位误差造成的爆音）
//     · 先按计划算好总长，一次性 new float[]，用数组下标写
//       （不再往 List 里逐个 Add —— 2050 万个元素逐个 Add 本身就是 50 ms）
//
// 【还有一层】拼出来的结果会被 AudioEngine 按"内容指纹"缓存起来。
//   时间轴没动，就直接用上次拼好的那份 —— 第二次按空格是 0 毫秒。
// ============================================================

using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using LiteCut.Core.Timeline;

namespace LiteCut;

/// <summary>
/// 把 <see cref="AudioRenderPlan"/> 渲染成可直接播放/导出的单声道样本数组。
/// </summary>
public static class TimelineAudioRenderer
{
    /// <summary>
    /// 同源文件里，如果下一段就在前面 2 秒内，就用"读完丢掉"接上去，
    /// 而不是重新定位。超过这个距离才值得付定位的代价。
    /// </summary>
    private const double SeekInsteadOfSkipSec = 2.0;

    /// <summary>跳过/读取用的中转数组（所有实例共用，够大就不会反复分配）</summary>
    private static readonly float[] Scratch = new float[65536];

    /// <summary>
    /// 从时间轴项目造一份渲染计划（顺便把 dB 换算成线性增益）。
    /// 必须在界面线程调用：它会读时间轴的块列表做快照。
    /// </summary>
    public static AudioRenderPlan BuildPlan(TimelineProject project, int sampleRate = AudioRenderPlan.DefaultSampleRate)
    {
        var segments = new List<AudioSegment>(project.Clips.Count);
        foreach (var c in project.Clips)
        {
            segments.Add(new AudioSegment(
                c.Id,
                c.SourcePath,
                c.SourceStartSec,
                c.SourceEndSec,
                c.TimelineStartSec,
                Utils.DbToGain(c.VolumeDb),
                c.FadeInSec,
                c.FadeOutSec));
        }
        return AudioRenderPlan.Build(segments, sampleRate);
    }

    /// <summary>
    /// 渲染：返回一整条单声道样本（长度 = plan.TotalSamples）。
    /// 块之间的空隙是静音，和导出的行为一致。
    /// </summary>
    public static float[] Render(AudioRenderPlan plan)
    {
        var output = new float[plan.TotalSamples];
        if (output.Length == 0 || plan.Segments.Count == 0) return output;

        RenderSegments(plan.Segments, output, plan);
        return output;
    }

    /// <summary>
    /// 增量重拼：拿上一份计划和它的成品，对照新计划，只重算改动过的那几段。
    ///
    /// 【为什么值得单独做这件事】
    ///   整条重拼要 1.5 秒（大头是解码）。但"挪一个块""裁短一点""调一块音量"
    ///   这种改动，99% 的样本根本没变 —— 如果每次都整条重来，用户"拖完块马上
    ///   按空格"就要等 1.5 秒，手感还是卡。
    ///
    /// 【为什么不是"把改动那块重读一遍"】
    ///   实测 NAudio 在 m4a 上用 CurrentTime 定位，落点会偏 ±42 毫秒且方向不定。
    ///   重读出来的那块会整体挪几十毫秒，和前后块之间就出现"丢一段/重一段"。
    ///   所以这里一个字节都不读源文件 —— 全部从上一次的成品里就地推出来：
    ///        新值 = 旧值 × (新包络 / 旧包络)
    ///   既快，又不会引入任何时间偏移。
    ///
    /// 【怎么保证不拼错】
    ///   能不能推、从哪儿推多少，全由 AudioPatchLayout.Analyze 纯算术判定；
    ///   只要有一样推不出来（要更多源素材、换了源文件、出现新块）就返回 null，
    ///   让调用方整条重拼 —— 宁可慢一点，不能听错。
    /// </summary>
    public static float[]? TryPatch(AudioRenderPlan oldPlan, float[]? oldSamples, AudioRenderPlan newPlan)
    {
        if (oldSamples == null || oldSamples.Length == 0) return null;
        if (oldSamples.Length != oldPlan.TotalSamples) return null;   // 缓存和计划对不上，不可信

        var patch = AudioPatchLayout.Analyze(oldPlan, newPlan);
        if (!patch.CanPatch) return null;

        // 所有段都是照搬、而且时间轴原点和总长都没变 → 这一份原样就是新的，
        // 连数组都不用重新分配（暂停后接着播走的就是这条：0 毫秒）。
        if (patch.NothingToDo
            && oldPlan.StartSec == newPlan.StartSec
            && oldPlan.TotalSamples == newPlan.TotalSamples)
            return oldSamples;

        var output = new float[newPlan.TotalSamples];

        // 按新计划顺序干这些活：没动的整片搬，动过的按包络比重算。
        // "照搬"的活也在列表里，漏掉它们新成品就会缺一大片声音。
        // 顺序保证"后写的覆盖先写的"，和整条重拼遇到重叠段时的行为一致。
        foreach (var job in patch.Jobs)
        {
            if (job.IsCopy) CopySegment(oldPlan, oldSamples, newPlan, output, job);
            else RebuildSegment(oldPlan, oldSamples, newPlan, output, job);
        }

        return output;
    }

    /// <summary>
    /// 把一段原样搬到新成品的位置上（源区间、增益、淡化都没变）。
    /// 走 Array.Copy，不逐样本做除法 —— 这一路要处理的是"整条时间轴里
    /// 没被碰过的那 218 块"，快一个数量级。
    /// </summary>
    private static void CopySegment(AudioRenderPlan oldPlan, float[] oldSamples,
                                    AudioRenderPlan newPlan, float[] output, SegmentJob job)
    {
        var newSeg = newPlan.Segments[job.NewIndex];
        var oldSeg = oldPlan.Segments[job.OldIndex];

        int writeAt = (int)((newSeg.TimelineStartSec - newPlan.StartSec) * newPlan.SampleRate);
        int readAt = (int)((oldSeg.TimelineStartSec - oldPlan.StartSec) * oldPlan.SampleRate) + job.Offset;

        // 两边各自截到合法范围，取交集一次搬完
        int from = Math.Max(0, Math.Max(-readAt, -writeAt));
        int to = Math.Min(job.Length, Math.Min(oldSamples.Length - readAt, output.Length - writeAt));
        if (to > from)
            Array.Copy(oldSamples, readAt + from, output, writeAt + from, to - from);
    }

    /// <summary>把一段从旧成品就地推导到新成品</summary>
    private static void RebuildSegment(AudioRenderPlan oldPlan, float[] oldSamples,
                                       AudioRenderPlan newPlan, float[] output, SegmentJob job)
    {
        var newSeg = newPlan.Segments[job.NewIndex];
        var oldSeg = oldPlan.Segments[job.OldIndex];

        int newLength = AudioPatchLayout.OutputLength(newPlan, newSeg);
        int oldLength = AudioPatchLayout.OutputLength(oldPlan, oldSeg);
        int newFadeIn = (int)(newSeg.FadeInSec * newPlan.SampleRate);
        int newFadeOut = (int)(newSeg.FadeOutSec * newPlan.SampleRate);
        int oldFadeIn = (int)(oldSeg.FadeInSec * oldPlan.SampleRate);
        int oldFadeOut = (int)(oldSeg.FadeOutSec * oldPlan.SampleRate);

        int writeAt = (int)((newSeg.TimelineStartSec - newPlan.StartSec) * newPlan.SampleRate);
        int readAt = (int)((oldSeg.TimelineStartSec - oldPlan.StartSec) * oldPlan.SampleRate) + job.Offset;

        for (int i = 0; i < job.Length; i++)
        {
            int w = writeAt + i;
            if (w < 0 || w >= output.Length) continue;

            int r = readAt + i;
            if (r < 0 || r >= oldSamples.Length) continue;

            // 旧成品里存的是 源素材样本 × 旧包络；乘上新旧包络之比就是新值
            double oldEnv = AudioPatchLayout.EnvelopeAt(job.Offset + i, oldLength, oldFadeIn, oldFadeOut, oldSeg.Gain);
            double newEnv = AudioPatchLayout.EnvelopeAt(i, newLength, newFadeIn, newFadeOut, newSeg.Gain);

            output[w] = oldEnv == 0 ? 0f : (float)(oldSamples[r] * (newEnv / oldEnv));
        }
    }

    /// <summary>把一组段渲染进现成的缓冲区</summary>
    private static void RenderSegments(IReadOnlyList<AudioSegment> segments, float[] output, AudioRenderPlan plan)
    {
        // 每个源文件一个读手，渲染期间一直开着
        var open = new Dictionary<string, SourceReader>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 按给定顺序遍历：
            // 两条真实音频万一重叠，后写的覆盖先写的 —— 和旧实现的行为一致
            foreach (var seg in segments)
            {
                if (!open.TryGetValue(seg.SourcePath, out var reader))
                {
                    reader = SourceReader.TryOpen(seg.SourcePath);
                    if (reader == null) continue;      // 源文件读不了 → 这段留静音
                    open[seg.SourcePath] = reader;
                }

                reader.MixInto(seg, output, plan);
            }
        }
        finally
        {
            foreach (var r in open.Values) r.Dispose();
        }
    }

    // ============================================================
    // 单个源文件的读手
    // ============================================================

    private sealed class SourceReader : IDisposable
    {
        private readonly AudioFileReader _file;
        private readonly ISampleProvider _mono;
        private readonly int _srcRate;

        /// <summary>已经读到源文件的第几个样本。-1 = 位置未知，下一步得先定位。</summary>
        private long _cursor = -1;

        /// <summary>一段的暂存区：重复使用，避免每段都新分配大数组</summary>
        private float[] _segment = Array.Empty<float>();

        private SourceReader(string path)
        {
            _file = new AudioFileReader(path);
            _srcRate = _file.WaveFormat.SampleRate;

            // 立体声转单声道（时间轴是单声道流）
            _mono = _file.WaveFormat.Channels > 1
                ? new StereoToMonoSampleProvider(_file)
                : _file;
        }

        public static SourceReader? TryOpen(string path)
        {
            try { return new SourceReader(path); }
            catch { return null; }   // 文件被移走 / 格式不支持 → 这段留静音
        }

        /// <summary>把一段混进输出。单段失败不影响其它段。</summary>
        public void MixInto(AudioSegment seg, float[] output, AudioRenderPlan plan)
        {
            try { MixCore(seg, output, plan); }
            catch { /* 这一段跳过 */ }
        }

        private void MixCore(AudioSegment seg, float[] output, AudioRenderPlan plan)
        {
            long from = (long)(seg.SourceStartSec * _srcRate);
            long to = (long)(seg.SourceEndSec * _srcRate);
            long need = to - from;
            if (need <= 0) return;
            if (need > int.MaxValue) need = int.MaxValue;

            if (!Locate(from)) return;

            int got = ReadSegment((int)need);
            if (got <= 0) return;

            MixToOutput(seg, output, plan, got);
        }

        /// <summary>把读指针弄到 <paramref name="fromSample"/>，失败返回 false。</summary>
        private bool Locate(long fromSample)
        {
            if (_cursor == fromSample) return true;

            // 就在前面不远 → 读出来丢掉，比重新定位便宜
            if (_cursor >= 0 && fromSample > _cursor && fromSample - _cursor <= _srcRate * SeekInsteadOfSkipSec)
            {
                long skip = fromSample - _cursor;
                while (skip > 0)
                {
                    int want = (int)Math.Min(skip, Scratch.Length);
                    int read = _mono.Read(Scratch, 0, want);
                    if (read <= 0) { _cursor = -1; return false; }   // 到文件末尾了
                    skip -= read;
                    _cursor += read;
                }
                return true;
            }

            // 往回跳 / 跳得远 / 位置未知 → 只能重新定位
            try
            {
                _file.CurrentTime = TimeSpan.FromSeconds((double)fromSample / _srcRate);
                _cursor = fromSample;
                return true;
            }
            catch
            {
                _cursor = -1;
                return false;
            }
        }

        /// <summary>读 <paramref name="count"/> 个源样本进暂存区，返回实际读到的个数。</summary>
        private int ReadSegment(int count)
        {
            if (_segment.Length < count) _segment = new float[count];

            int got = 0;
            while (got < count)
            {
                int read = _mono.Read(_segment, got, count - got);
                if (read <= 0) break;
                got += read;
            }

            _cursor += got;
            return got;
        }

        /// <summary>重采样到目标采样率，加增益和淡入淡出，写进输出。</summary>
        private void MixToOutput(AudioSegment seg, float[] output, AudioRenderPlan plan, int got)
        {
            double ratio = (double)plan.SampleRate / _srcRate;

            // 输出长度完全由计划推算（而不是"实际读到多少就写多少"）。
            // 这一点是增量重拼能不能做的关键：长度可推 → "这段占了缓冲区哪一片"
            // 就算得准 → 才能只擦它、只重写它，一点不碰邻居。
            int outLength = (int)(seg.SourceLengthSec * plan.SampleRate);
            if (outLength <= 0) return;

            // 万一源文件比它声称的短（提前读到文件尾），写的就短一点，剩下留静音 ——
            // 整条重拼时那片本来也是静音，两边结果一致
            int writable = (int)Math.Ceiling(got * ratio);
            if (writable > outLength) writable = outLength;

            int writeAt = (int)((seg.TimelineStartSec - plan.StartSec) * plan.SampleRate);
            float gain = (float)seg.Gain;
            int fadeIn = (int)(seg.FadeInSec * plan.SampleRate);
            int fadeOut = (int)(seg.FadeOutSec * plan.SampleRate);
            int fadeOutStart = outLength - fadeOut;

            float[] src = _segment;

            for (int i = 0; i < writable; i++)
            {
                int w = writeAt + i;
                if (w >= output.Length) break;
                if (w < 0) continue;

                // 线性插值重采样
                double srcPos = i / ratio;
                int idx = (int)srcPos;
                if (idx >= got) idx = got - 1;
                int idxNext = idx + 1 < got ? idx + 1 : got - 1;
                double frac = srcPos - idx;
                float v = (float)(src[idx] + (src[idxNext] - src[idx]) * frac) * gain;

                if (fadeIn > 0 && i < fadeIn)
                    v *= (float)i / fadeIn;
                if (fadeOut > 0 && i >= fadeOutStart)
                    v *= (float)(outLength - i) / fadeOut;

                output[w] = v;
            }
        }

        public void Dispose()
        {
            try { _file.Dispose(); } catch { }
        }
    }
}
