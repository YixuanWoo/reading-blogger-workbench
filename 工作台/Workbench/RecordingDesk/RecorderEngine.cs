// ============================================================
// RecorderEngine.cs - 真正碰麦克风的那一层（录音 / 电平 / 试听 / 波形峰值）
// ============================================================
//
// 【为什么单独一层，不写在页面里】
//   录音是"回调线程 + 文件句柄 + 音频设备"三件容易出事的活。
//   页面只该管"按钮长什么样、现在什么状态"，出问题的地方集中在这一个文件里，
//   将来换设备、加增益、加降噪都只改这里。
//
// 【录音格式：44.1kHz / 16bit / 单声道 wav】
//   口播不需要立体声；16bit 单声道 44.1k 每分钟约 5MB，
//   一个 10 分钟的稿子 50MB，完全在可接受范围。
//   不在这里做任何降噪/压缩 —— 原始素材保持原样（"音频零处理"）。
//
// 【电平表】
//   峰值（peak）而不是平均值：口播里"有没有声音"看峰值最直接，
//   WO Mic 掉线时峰值会掉到 -90dB 以下，一眼就能看出来。
//
// 【试听】
//   直接用 NAudio 的 AudioFileReader 播 wav，不经过时间轴引擎 ——
//   这一层跟成片剪辑无关，只是"把刚才那条放出来听听"。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace LiteCut.Workbench.RecordingDesk;

/// <summary>一个可选的录音设备（名字带 Audio 前缀，避免和 System.Windows.Input.InputDevice 撞名）。</summary>
public sealed class AudioInputDevice
{
    public int Index { get; init; }
    public string Name { get; init; } = "";

    /// <summary>下拉框里显示的文字（带序号，方便区分同名设备）。</summary>
    public string Display => Name;

    public override string ToString() => Name;
}

/// <summary>麦克风录音 + 试听。一个页面一个实例，离开页面就 Dispose。</summary>
public sealed class RecorderEngine : IDisposable
{
    // ---- 录音 ----
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private readonly Stopwatch _clock = new();
    private float _peak;                       // 0~1，回调线程写、界面线程读
    private readonly List<float> _live = new();// 录音过程中的峰值序列（画实时波形）
    private readonly object _liveLock = new();
    private volatile bool _stopping;

    // ---- 试听 ----
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;

    /// <summary>试听自然结束（播完 / 被停）。</summary>
    public event EventHandler? PlaybackEnded;

    public bool IsRecording => _waveIn != null;
    public bool IsPlaying => _out != null;

    /// <summary>正在写的 wav 路径（没在录就是空）。</summary>
    public string CurrentPath { get; private set; } = "";

    /// <summary>已经录了多久（秒）。</summary>
    public double ElapsedSec => _clock.Elapsed.TotalSeconds;

    /// <summary>当前电平（dBFS，-96 表示静音）。</summary>
    public double LevelDb
    {
        get
        {
            float p = Volatile.Read(ref _peak);
            if (p <= 1e-5f) return -96;
            return Math.Max(-96, 20 * Math.Log10(p));
        }
    }

    /// <summary>录音过程中攒下来的峰值（0~1）。界面拿它画实时波形。</summary>
    public float[] SnapshotLivePeaks()
    {
        lock (_liveLock) return _live.ToArray();
    }

    // ========================================================
    // 设备
    // ========================================================

    /// <summary>列出所有录音设备。没有设备时返回空表（不抛异常）。</summary>
    public static List<AudioInputDevice> AudioInputDevices()
    {
        var list = new List<AudioInputDevice>();
        try
        {
            int n = WaveInEvent.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                string name;
                try { name = WaveInEvent.GetCapabilities(i).ProductName; }
                catch { name = "设备 " + i; }
                if (string.IsNullOrWhiteSpace(name)) name = "设备 " + i;
                list.Add(new AudioInputDevice { Index = i, Name = name.Trim() });
            }
        }
        catch
        {
            // 没装声卡 / 驱动异常 —— 返回空表，让界面提示"没有可用麦克风"
        }
        return list;
    }

    // ========================================================
    // 录音
    // ========================================================

    /// <summary>
    /// 开始录到一个 wav。失败会抛异常（调用方负责提示用户）。
    ///
    /// ★ 用 WaveInEvent（waveIn API）而不是 WasapiCapture：
    ///   WO Mic 是虚拟声卡，走 waveIn 兼容性最好；WASAPI 对虚拟设备偶发"能枚举但没数据"。
    /// </summary>
    public void Start(string wavPath, int deviceIndex)
    {
        if (_waveIn != null) throw new InvalidOperationException("已经在录了");

        string? dir = Path.GetDirectoryName(wavPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var wi = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(44100, 16, 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 4,
        };

        var writer = new WaveFileWriter(wavPath, wi.WaveFormat);

        wi.DataAvailable += (_, e) => OnData(writer, e);
        wi.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) LastError = e.Exception.Message;
        };

        lock (_liveLock) _live.Clear();
        Volatile.Write(ref _peak, 0);
        _stopping = false;
        CurrentPath = wavPath;

        wi.StartRecording();

        _waveIn = wi;
        _writer = writer;
        _clock.Restart();
    }

    private void OnData(WaveFileWriter writer, WaveInEventArgs e)
    {
        if (_stopping) return;

        try { writer.Write(e.Buffer, 0, e.BytesRecorded); }
        catch (Exception ex) { LastError = "写盘失败：" + ex.Message; return; }

        // 16bit 单声道 → 取绝对值最大的那个采样点
        float peak = 0;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short v = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            float a = Math.Abs(v) / 32768f;
            if (a > peak) peak = a;
        }

        Volatile.Write(ref _peak, peak);
        lock (_liveLock)
        {
            _live.Add(peak);
            if (_live.Count > 40000) _live.RemoveRange(0, 20000);   // 10 分钟的余量，防无限长
        }
    }

    /// <summary>停止录音，返回时长（秒）。重复调用安全。</summary>
    public double Stop()
    {
        var wi = _waveIn;
        if (wi == null) return 0;

        using var done = new ManualResetEventSlim(false);
        wi.RecordingStopped += (_, _) => done.Set();

        try { wi.StopRecording(); } catch { }

        // ★ 等的这段时间里 DataAvailable 还会来最后几包 —— 必须让它们写进文件，
        //   否则每次停录都会削掉最多一个缓冲（50ms）的尾巴。
        done.Wait(1500);

        // 现在才关写入口；之后万一还有回调也会被 OnData 挡掉
        _stopping = true;

        double dur = _clock.Elapsed.TotalSeconds;
        _clock.Stop();

        try { _writer?.Dispose(); } catch { }
        try { wi.Dispose(); } catch { }

        _writer = null;
        _waveIn = null;
        Volatile.Write(ref _peak, 0);

        LastDurationSec = dur;
        return dur;
    }

    /// <summary>最后一次录音的时长（Stop 之后有效）。</summary>
    public double LastDurationSec { get; private set; }

    /// <summary>出错了没有（写盘失败 / 设备异常）。</summary>
    public string? LastError { get; private set; }

    // ========================================================
    // 试听
    // ========================================================

    /// <summary>播放一个 wav。会先停掉上一次。</summary>
    public void Play(string wavPath)
    {
        StopPlay();

        var reader = new AudioFileReader(wavPath);
        var output = new WaveOutEvent { DesiredLatency = 90, NumberOfBuffers = 3 };

        output.PlaybackStopped += (_, _) =>
        {
            // 播完了（或被停）→ 通知界面把按钮状态收回来
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        };

        output.Init(reader);
        output.Play();

        _reader = reader;
        _out = output;
    }

    /// <summary>当前试听位置（秒）。</summary>
    public double PlayPositionSec
    {
        get
        {
            var r = _reader;
            if (r == null) return 0;
            try { return r.CurrentTime.TotalSeconds; } catch { return 0; }
        }
    }

    /// <summary>当前试听那条的总时长（秒）。</summary>
    public double PlayTotalSec
    {
        get
        {
            var r = _reader;
            if (r == null) return 0;
            try { return r.TotalTime.TotalSeconds; } catch { return 0; }
        }
    }

    /// <summary>停止试听。</summary>
    public void StopPlay()
    {
        var o = _out;
        _out = null;

        if (o != null)
        {
            try { o.Stop(); } catch { }
            try { o.Dispose(); } catch { }
        }

        var r = _reader;
        _reader = null;
        try { r?.Dispose(); } catch { }
    }

    // ========================================================
    // 读 wav：时长 / 峰值
    // ========================================================

    /// <summary>读一个 wav 的时长（秒）。读不出来返回 0。</summary>
    public static double DurationOf(string path)
    {
        try
        {
            using var r = new AudioFileReader(path);
            return r.TotalTime.TotalSeconds;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 把一个 wav 压成 N 个峰值柱（0~1），给波形条画图用。
    /// 流式读，不把整条音频一次性塞进内存。
    /// </summary>
    public static float[] PeaksOf(string path, int buckets)
    {
        if (buckets <= 0) return Array.Empty<float>();
        try
        {
            using var r = new AudioFileReader(path);
            long total = r.Length / 4;                    // 32bit float 样本数
            if (total <= 0) return Array.Empty<float>();

            var peaks = new float[buckets];
            var buf = new float[8192];
            long pos = 0;
            int read;
            while ((read = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    int b = (int)(pos * buckets / total);
                    if (b >= buckets) b = buckets - 1;
                    float a = Math.Abs(buf[i]);
                    if (a > peaks[b]) peaks[b] = a;
                    pos++;
                }
            }
            return peaks;
        }
        catch { return Array.Empty<float>(); }
    }

    /// <summary>把实时峰值序列重采样成 N 个柱（录音过程中画波形）。</summary>
    public static float[] Resample(float[] src, int buckets)
    {
        if (src.Length == 0 || buckets <= 0) return Array.Empty<float>();

        var outArr = new float[buckets];
        double per = (double)src.Length / buckets;
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)(b * per);
            int to = Math.Min(src.Length, (int)((b + 1) * per));
            if (to <= from) to = Math.Min(src.Length, from + 1);
            float mx = 0;
            for (int i = from; i < to; i++) if (src[i] > mx) mx = src[i];
            outArr[b] = mx;
        }
        return outArr;
    }

    public void Dispose()
    {
        try { if (IsRecording) Stop(); } catch { }
        try { StopPlay(); } catch { }
    }
}
