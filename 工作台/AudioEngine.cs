// ============================================================
// AudioEngine.cs - 音频引擎（按时间轴块播放/导出）
//
// 这个文件管两件事：
//   1. Play / Pause / Stop - 按时间轴块播放
//   2. ExportM4a - 按时间轴块导出成 m4a
//
// 核心思路：播放和导出前，先把时间轴上的所有块"拼"成
// 一个连续的内存音频流（块之间填静音），然后播这个流。
// 这样播放和导出逻辑统一，简单可靠。
//
// ------------------------------------------------------------
// 【为什么按空格要等一两秒 —— 以及现在是怎么解决的】
//
// 原来每次 Play() 都现场拼一遍整条音频：打开每一个源文件、逐块解码、
// 重采样、拷进大列表。实测一个 219 块的项目要 2.5 秒，而这 2.5 秒
// 每一次按空格都要重走一遍（暂停后接着播也一样），于是手感就是"卡一下"。
//
// 现在三道闸门依次把这件事解决掉：
//   ① 拼的算法本身改快了 —— 同一个源文件只开一次解码器、相邻段顺读不倒回
//      （见 TimelineAudioRenderer）
//   ② 拼好的结果按【内容指纹】缓存起来。时间轴没变 → 直接复用，0 毫秒。
//      指纹是从内容算出来的，不靠谁记得去"标记缓存脏了"，所以不会漏。
//   ③ 时间轴一改，就在后台线程把新的拼好（Prewarm）。
//      等你手指按到空格时，缓存通常已经热了。
//
// 另外两处手感细节：
//   · 播放设备不再每次重建 —— 同样的数据重新开播只挪读指针
//   · 设备缓冲从 NAudio 默认的 300ms 压到 100ms，按下到出声快很多
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using LiteCut.Core.Timeline;

namespace LiteCut;

/// <summary>
/// 播放位置变化的事件参数。
/// </summary>
public class PlaybackPositionEventArgs : EventArgs
{
    /// <summary>当前播放位置（秒，时间轴坐标系）</summary>
    public double PositionSec;
}

/// <summary>
/// 音频引擎：负责声音的进出。
/// </summary>
public class AudioEngine : IDisposable
{
    // ============================================================
    // 手感相关的两个常量
    // ============================================================

    /// <summary>
    /// 播放设备缓冲时长（毫秒）。
    ///
    /// NAudio 默认是 300 —— 意思是从"按下播放"到"真的出声"，声卡要先攒够
    /// 300ms 的数据。我们的数据全在内存里（不是现读磁盘），每 33ms 填一次
    /// 缓冲毫无压力，所以压到 100ms 也不会爆音，但按下去到出声快了一倍多。
    /// </summary>
    private const int OutputLatencyMs = 100;

    /// <summary>缓冲块数。3 块 × 33ms，容错够用。</summary>
    private const int OutputBuffers = 3;

    private WaveOutEvent? _output;
    private MemorySampleProvider? _provider;
    private TimelineProject? _project;
    private bool _isPlaying;
    private DispatcherTimer? _positionTimer;

    /// <summary>播放位置变化事件</summary>
    public event EventHandler<PlaybackPositionEventArgs>? PositionChanged;

    /// <summary>播放自然结束事件</summary>
    public event EventHandler? PlaybackStopped;

    /// <summary>当前是否在播放</summary>
    public bool IsPlaying => _isPlaying;

    // ============================================================
    // 拼好的音频缓存
    // ============================================================

    private readonly object _cacheLock = new();
    private AudioRenderPlan? _cachedPlan;
    private float[]? _cachedSamples;

    /// <summary>缓存里是否已经有一份拼好的音频（界面可以据此显示状态）</summary>
    public bool HasCachedAudio
    {
        get { lock (_cacheLock) return _cachedSamples != null; }
    }

    /// <summary>
    /// 取"整条成片"的波形峰值（供分镜页那条连续波形用）。
    ///
    /// 【为什么只在缓存命中时才返回】
    ///   现场拼整条要 1.5 秒，会把界面线程卡住。
    ///   调用方应该先 Prewarm（后台拼），再隔一会儿来问 —— 没拼好就返回 false，
    ///   界面先画个占位，等拼好了再补上。绝不在这里同步等。
    /// </summary>
    /// <param name="project">时间轴项目（内容指纹要对得上缓存才算命中）。</param>
    /// <param name="buckets">要多少个峰值柱（一般是波形条的像素宽度）。</param>
    /// <param name="peaks">每个柱的幅度 0~1。</param>
    public bool TryGetPeaks(TimelineProject project, int buckets, out float[] peaks)
    {
        peaks = Array.Empty<float>();
        if (buckets <= 0) return false;

        AudioRenderPlan plan;
        try { plan = TimelineAudioRenderer.BuildPlan(project); }
        catch { return false; }

        if (!TryGetCached(plan, out var samples) || samples.Length == 0) return false;

        // 整条音频按桶取绝对值最大的那个采样点。
        // 取最大而不是取平均：平均值会把语音的起伏抹平，画出来像一根方棍。
        peaks = new float[buckets];
        double per = (double)samples.Length / buckets;
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)(b * per);
            int to = b == buckets - 1 ? samples.Length : (int)((b + 1) * per);
            if (to <= from) to = Math.Min(from + 1, samples.Length);

            float mx = 0;
            for (int i = from; i < to; i++)
            {
                float v = Math.Abs(samples[i]);
                if (v > mx) mx = v;
            }
            peaks[b] = mx;
        }
        return true;
    }

    private bool TryGetCached(AudioRenderPlan plan, out float[] samples)
    {
        lock (_cacheLock)
        {
            if (_cachedPlan != null && _cachedSamples != null && _cachedPlan.Fingerprint == plan.Fingerprint)
            {
                samples = _cachedSamples;
                return true;
            }
        }
        samples = Array.Empty<float>();
        return false;
    }

    private void StoreCache(AudioRenderPlan plan, float[] samples)
    {
        lock (_cacheLock)
        {
            _cachedPlan = plan;
            _cachedSamples = samples;
        }
    }

    // ============================================================
    // 后台预热
    // ============================================================

    private readonly object _warmLock = new();
    private AudioRenderPlan? _warmPending;
    private bool _warmRunning;

    /// <summary>
    /// 在后台把"当前时间轴对应的整条音频"先拼好，这样下一次按空格是"立刻响"。
    ///
    /// 必须在界面线程调用（要先做一次时间轴快照）。缓存已经命中时什么都不做。
    /// 连续多次调用只会跑最后一次 —— 中途来的新请求会顶掉还没开始跑的旧请求。
    /// </summary>
    public void Prewarm(TimelineProject project)
    {
        AudioRenderPlan plan;
        try { plan = TimelineAudioRenderer.BuildPlan(project); }
        catch { return; }

        if (TryGetCached(plan, out _)) return;
        if (plan.TotalSamples <= 0) return;

        lock (_warmLock)
        {
            _warmPending = plan;
            if (_warmRunning) return;   // 已经有一个在跑，它自己会看到最新的请求
            _warmRunning = true;
        }

        Task.Run(WarmLoop);
    }

    /// <summary>预热线程：一直跑"最新的那一份"，跑完没人插队就收工。</summary>
    private void WarmLoop()
    {
        while (true)
        {
            AudioRenderPlan plan;
            lock (_warmLock)
            {
                if (_warmPending == null) { _warmRunning = false; return; }
                plan = _warmPending;
                _warmPending = null;
            }

            float[] samples;
            try
            {
                // 走和播放完全相同的取样本逻辑：能增量重拼就增量重拼
                samples = GetSamples(plan);
            }
            catch
            {
                // 渲染整体失败（理论上不会，单段错误在渲染器里已经吞掉了）。
                // 收工，别在这里空转。
                lock (_warmLock) { _warmPending = null; _warmRunning = false; }
                return;
            }
            _ = samples;

            lock (_warmLock)
            {
                // 跑的过程中又来了新请求 → 继续跑最新的那一份。
                // （刚拼好的这份已经存进缓存了，指纹对得上就还能用上）
                if (_warmPending == null)
                {
                    _warmRunning = false;
                    return;
                }
            }
        }
    }

    // ============================================================
    // 静态工具：读素材信息
    // ============================================================

    /// <summary>
    /// 获取素材文件的时长（秒）。
    /// 用 NAudio 的 AudioFileReader 读时长。
    /// </summary>
    public static double GetDurationSec(string path)
    {
        using var reader = new AudioFileReader(path);
        return reader.TotalTime.TotalSeconds;
    }

    // ============================================================
    // 播放
    // ============================================================

    /// <summary>播放时间轴项目</summary>
    public void Play(TimelineProject project)
    {
        _project = project;

        // 拿到整条音频：缓存命中就直接用，没命中才现场拼
        var plan = TimelineAudioRenderer.BuildPlan(project);
        float[] samples = GetSamples(plan);

        // 从哪个样本开始播（开头留了空地、播放线停在末尾之外，都在这里夹正）
        long startSample = ComputeStartSample(project, plan.SampleRate, samples.Length);

        // 设备还开着、而且喂的还是同一份样本 → 只把读指针挪过去再开播。
        // 这条分支管的就是"暂停后又按空格"和"从头再放一遍"：
        // 不需要重建播放设备，更不需要把整条音频重拼一遍。
        //
        // 【为什么判的是"同一份数组"，而不是"设备还开着"就够了】
        //   编辑过时间轴之后，拼出来的是一份**新数组**，而设备缓冲里还压着最多 100ms
        //   的旧声音。要换数据就得先把设备缓冲冲掉（WaveOutEvent.Stop），可是 Stop 会
        //   引发 PlaybackStopped —— 实测它**不保证同步**（有时 Stop 返回后才发），
        //   于是"按下播放"马上又被当成"播完了"：红线冻住、播放键弹回。
        //   所以换数据一律退到下面重建设备那条路（实测 12~33 ms/次，代价明确、行为确定）。
        //   想省这几十毫秒的话，路子是"后台预先开好一个空闲设备"，但那是另一件事了 ——
        //   别用"摘掉事件处理器再装回去"绕，那是在跟异步时序赌。
        if (_output != null && _provider != null && ReferenceEquals(_provider.Samples, samples))
        {
            _provider.Position = startSample;
            _output.Play();
            _isPlaying = true;
            StartPositionTimer();
            return;
        }

        StopOutput();

        _provider = new MemorySampleProvider(samples, plan.SampleRate) { Position = startSample };

        _output = new WaveOutEvent
        {
            DesiredLatency = OutputLatencyMs,
            NumberOfBuffers = OutputBuffers
        };
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_provider);
        _output.Play();
        _isPlaying = true;

        StartPositionTimer();
    }

    /// <summary>
    /// 取整条音频：缓存命中直接用；没命中先试增量重拼，实在不行才整条重拼。
    ///
    /// 这个方法同时服务播放（界面线程）和后台预热（后台线程），所以它不碰
    /// 播放设备，只碰缓存 —— 加锁的只有缓存那几行。
    /// </summary>
    private float[] GetSamples(AudioRenderPlan plan)
    {
        if (TryGetCached(plan, out var cached)) return cached;

        // 时间轴改过了 → 先试着只重拼改动的那几段（通常几十毫秒），
        // 而不是整条重来（1.5 秒）。"拖完块马上按空格"走的就是这条快路。
        AudioRenderPlan? oldPlan = null;
        float[]? oldSamples = null;
        lock (_cacheLock)
        {
            if (_cachedPlan != null && _cachedSamples != null)
            {
                oldPlan = _cachedPlan;
                oldSamples = _cachedSamples;
            }
        }

        if (oldPlan != null && oldSamples != null)
        {
            var patched = TimelineAudioRenderer.TryPatch(oldPlan, oldSamples, plan);
            if (patched != null)
            {
                StoreCache(plan, patched);
                return patched;
            }
        }

        var samples = TimelineAudioRenderer.Render(plan);
        StoreCache(plan, samples);
        return samples;
    }

    /// <summary>
    /// 算出"该从第几个样本开始播"。
    ///
    /// 样本流是从第一块的起点（project.StartSec）铺起的，所以：
    ///     样本下标 = (时间轴秒数 - StartSec) × 采样率
    ///
    /// 【为什么必须夹范围 —— 这里修的是一个真实 bug】
    ///   时间轴开头可能是空白：把第一块往右拖、或者把前面几块删掉，都会在
    ///   第一块前面留出一段空地。这时播放线还停在空地里，减出来是负数；
    ///   拿负下标去取样本会直接越界抛异常，声音一点都放不出来。
    ///   反过来，播放线停在末尾之后时下标又超出样本总数，一按播放就"立刻播完"。
    ///
    ///   夹法：
    ///     · 停在开头空地 → 从第一块开头开始播（和导出一致：导出同样不保留前导空白）
    ///     · 停在末尾之外 → 从头重播
    ///
    /// 顺带把 project.PlayheadSec 也挪到真正开始播的位置，
    /// 免得红线停在空地里、和耳朵听到的对不上。
    /// </summary>
    private static long ComputeStartSample(TimelineProject project, int sampleRate, int sampleCount)
    {
        double startAt = project.PlayheadSec;
        if (double.IsNaN(startAt) || startAt < project.StartSec)
            startAt = project.StartSec;

        double contentEnd = project.StartSec + project.TotalDurationSec;
        if (contentEnd > project.StartSec && startAt >= contentEnd)
            startAt = project.StartSec;

        project.PlayheadSec = startAt;

        long pos = (long)((startAt - project.StartSec) * sampleRate);
        if (pos < 0) pos = 0;
        if (pos > sampleCount) pos = sampleCount;
        return pos;
    }

    /// <summary>暂停</summary>
    public void Pause()
    {
        if (!_isPlaying) return;
        _output?.Pause();
        _isPlaying = false;
        StopPositionTimer();
    }

    /// <summary>停止并归零</summary>
    public void Stop()
    {
        StopOutput();
        if (_project != null) _project.PlayheadSec = 0;
    }

    // ============================================================
    // 导出
    // ============================================================

    /// <summary>
    /// 把时间轴项目导出成 m4a 文件。
    /// 固定 48kHz。
    /// </summary>
    public void ExportM4a(TimelineProject project, string outputPath)
    {
        // 走同一份缓存：刚放下鼠标就导出时，不用把整条音频再拼一遍
        var plan = TimelineAudioRenderer.BuildPlan(project);
        float[] samples = GetSamples(plan);

        string tempWav = Path.Combine(Path.GetTempPath(), "LiteCut_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // 重采样到 48kHz
            var source = new MemorySampleProvider(samples, plan.SampleRate);
            var resampled = new WdlResamplingSampleProvider(source, 48000);
            WaveFileWriter.CreateWaveFile16(tempWav, resampled);

            using var reader = new AudioFileReader(tempWav);
            MediaFoundationEncoder.EncodeToAac(reader, outputPath, 128000);
        }
        finally
        {
            if (File.Exists(tempWav)) File.Delete(tempWav);
        }
    }

    // ============================================================
    // 内部实现
    // ============================================================

    private void StopOutput()
    {
        StopPositionTimer();
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _provider = null;
        _isPlaying = false;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _isPlaying = false;
        StopPositionTimer();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    private void StartPositionTimer()
    {
        if (_positionTimer == null)
        {
            _positionTimer = new DispatcherTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(100);
            _positionTimer.Tick += (s, e) =>
            {
                if (_provider != null && _project != null)
                {
                    // 样本流是从第一块起点铺开的，要加回 StartSec 才是时间轴上的绝对位置。
                    // （不加回的话：开头有空地时红线会比声音慢 StartSec 秒，
                    //   再按一次播放又会踩到负数下标 —— bug 会复发）
                    double sec = _project.StartSec + (double)_provider.Position / _provider.SampleRate;
                    _project.PlayheadSec = sec;
                    PositionChanged?.Invoke(this, new PlaybackPositionEventArgs { PositionSec = sec });
                }
            };
        }
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Stop();
    }

    /// <summary>
    /// 丢掉缓存里的整条样本。
    ///
    /// 【什么时候必须调】
    ///   源文件被**原地重建**之后（V1.17.0：录音台每次「应用到下游」都会重写
    ///   `素材\录音\vo.wav`）。缓存只按"时间轴指纹"判定命中，不知道磁盘上的
    ///   文件已经换了一版 —— 不清的话，精剪里听到的、看到的还是上一版母带，
    ///   而且**看不出哪里不对**（波形还在、能播、长度也差不多）。
    ///   这类"静默用旧数据"的坑，改一次查半天，所以宁可显式清一遍。
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedPlan = null;
            _cachedSamples = null;
        }
    }

    public void Dispose()
    {
        StopOutput();
        _positionTimer?.Stop();

        lock (_cacheLock)
        {
            _cachedPlan = null;
            _cachedSamples = null;
        }
    }
}

/// <summary>
/// 播放专用数据源：从连续样本数组读取。
/// </summary>
internal class MemorySampleProvider : ISampleProvider
{
    private readonly float[] _samples;
    private long _position;
    private readonly WaveFormat _format;

    /// <summary>底层样本数组。播放复用判断"是不是同一份数据"要靠它比对引用。</summary>
    public float[] Samples => _samples;

    public long Position
    {
        get => _position;
        set => _position = value;
    }

    public int SampleRate => _format.SampleRate;

    public MemorySampleProvider(float[] samples, int sampleRate)
    {
        _samples = samples;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    }

    public WaveFormat WaveFormat => _format;

    public int Read(float[] buffer, int offset, int count)
    {
        // 防御：负下标取样本会抛越界异常。
        // 正常不该出现（Play 里已经把起点夹好了），但这段代码跑在声卡的回调线程上，
        // 一旦抛异常整个程序就没了 —— 所以这里再兜一层。
        if (_position < 0) _position = 0;

        var samples = _samples;
        int written = 0;
        while (written < count && _position < samples.Length)
        {
            buffer[offset + written] = samples[(int)_position];
            _position++;
            written++;
        }
        return written;
    }
}
