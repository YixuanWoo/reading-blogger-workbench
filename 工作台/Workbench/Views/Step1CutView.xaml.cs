// ============================================================
// Step1CutView.xaml.cs - 第一步：音频粗剪
// ============================================================
//
// 【这一页干什么】
//   加载口播素材 → 定参数 → 跑双轨粗剪 → 看波形哪里被剪了 →
//   你能双击改 → 听效果 → 导出成片或音频。
//
//   这条链走完，一期视频最耗时间的部分就做掉了。
//
// 【为什么"听一下"很重要】
//   波形上看着合理的刀，耳朵听起来可能很别扭（字被切了半）。
//   粗剪的最终裁判是耳朵不是眼睛，所以预览必须一键可达。
//
// 【播放的实现取舍】
//   我们不让 NAudio 去解 m4a/mp3 —— 那依赖系统解码器，
//   ALAC 格式（苹果无损）在没装 iTunes 的机器上会直接失败。
//
//   改成：用 ffmpeg 解出来的那份 float 数组，自己拼 wav 再播。
//   好处有三个：零兼容问题、预览与导出用的是同一份判断、
//   听粗剪结果就是"只拼保留段"，所见即所得。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiteCut.Core.Contracts;
using LiteCut.Core.Engine;
using LiteCut.Core.Media;
using LiteCut.Workbench.Agent;
using LiteCut.Workbench.Project;
using Microsoft.Win32;
using NAudio.Wave;

namespace LiteCut.Workbench.Views;

public partial class Step1CutView : UserControl
{
    private readonly WorkbenchState _s = WorkbenchWindow.State;

    private readonly ObservableCollection<SegRow> _rows = new();
    private readonly ObservableCollection<SuggestRow> _suggests = new();
    private readonly ObservableCollection<ScriptRow> _scripts = new();   // 对稿偏差（T-28）

    // ---- 播放相关 ----
    private IWavePlayer? _player;
    private AudioFileReader? _reader;
    private DispatcherTimer? _timer;
    private bool _playingCut;          // true = 在听粗剪结果；false = 在听原声
    private string _lastPreview = "";  // 上次生成的预览 wav，避免重复生成

    /// <summary>
    /// 素材库双击一个文件时，通过这个槽位把路径递过来。
    /// 因为切页会重建 UserControl，直接调方法拿不到新实例，只能走静态。
    /// 读完立刻清空，避免下次误触发。
    /// </summary>
    public static string? PendingFile { get; set; }

    public Step1CutView()
    {
        InitializeComponent();

        GridSeg.ItemsSource = _rows;
        ListSuggest.ItemsSource = _suggests;
        ListScript.ItemsSource = _scripts;
        CmbPreset.SelectedIndex = Math.Clamp(_s.Config.LastPreset, 0, 2);

        string? pending = PendingFile;
        PendingFile = null;
        // 有素材库传过来的文件 → 等界面显示完再加载（Loaded 之后解码更稳）
        if (!string.IsNullOrEmpty(pending) && File.Exists(pending))
        {
            Loaded += (_, _) => LoadFile(pending);
        }

        // 已经有数据了（比如从别的步骤切回来）→ 直接显示，不用重来
        if (_s.HasSource)
        {
            Wave.SetData(_s.Pcm, ToTupleList(_s.KeepRanges()));
            UpdateFileInfo();
            BtnAnalyze.IsEnabled = true;
            BtnPlayRaw.IsEnabled = true;
        }
        if (_s.HasPlan)
        {
            RebuildRows();
            ApplyTranscriptUiState();
        }

        ApplyTranscriptUiState();
        RefreshScriptUi();
        RefreshDecisionUi();
        RebuildScriptRows();
        Unloaded += (_, _) => StopPlayback();
    }

    private static List<(double Start, double End)> ToTupleList(List<FFmpegExporter.Range> rs) =>
        rs.Select(r => (r.Start, r.End)).ToList();

    private WorkbenchWindow? Win => WorkbenchWindow.FindOwner(this);

    // ========================================================
    // ① 加载源文件
    // ========================================================

    private void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择口播音频或视频",
            Filter = "媒体文件|*.m4a;*.mp3;*.wav;*.mp4;*.mov;*.mkv;*.flac;*.aac|所有文件|*.*",
        };

        if (!string.IsNullOrWhiteSpace(_s.Config.LastMediaDir) &&
            Directory.Exists(_s.Config.LastMediaDir))
            dlg.InitialDirectory = _s.Config.LastMediaDir;

        if (dlg.ShowDialog() != true) return;
        LoadFile(dlg.FileName);
    }

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length > 0) LoadFile(files[0]);
    }

    /// <summary>
    /// 加载一个媒体文件：探测 + 解码成 PCM。
    /// 这是整个界面唯一会明显慢一步（几秒）的动作，所以放后台线程。
    /// </summary>
    private async void LoadFile(string path)
    {
        if (!File.Exists(path)) return;

        // ========================================================
        // 【2026-09-29 修】决策要不要作废，只看"源文件换没换"
        // ========================================================
        //
        // 以前这里是无条件清空 Plan / FineCutKeeps / Shots。看着合理（"换了素材，
        // 旧决策当然作废"），但它漏了一件事：**打开项目也会走到这里**。
        //   ProjectSession.ApplyToState() 把源文件路径排进 PendingFile，
        //   本页一构造就自动解码 → 于是每次开项目都把上一次的粗剪决策、
        //   "精剪已接管下游"的标记、分镜表全部清掉；紧接着自动保存又把这个
        //   空状态写回磁盘。
        //
        //   用户看到的现象（2026-09-29 报的）：
        //     · "重启后粗剪页像是没剪过的状态" —— 决策真的被写没了
        //     · "点了应用到下游，下游什么也没变" —— 其实是生效了又被擦掉
        //
        // 现在改成：**同一个源文件重新解码，决策原样保留**。
        // 只有真的换了素材，才作废和它绑在一起的那些判断。
        // （判定与清理本身在 WorkbenchState.ApplyDecodedSource 里，那是纯数据操作，
        //   能被无头验证直接断言；这里只管界面上的连带清理。）
        string previous = _s.SourcePath;
        bool switchedSource = !WorkbenchState.IsSameSource(previous, path);

        _s.SourcePath = path;
        _s.Config.LastMediaDir = Path.GetDirectoryName(path) ?? "";
        _s.SaveConfig();

        if (switchedSource)
        {
            // 界面侧：段列表和建议清单属于旧素材，先清掉（状态侧的清理在解码完成后一起做）
            _rows.Clear();
            _suggests.Clear();
            // 通知精剪页：它缓存的那个编辑器里还压着旧文件的时间轴，下次进页要清掉
            StepFineCutView.PendingEditorReset = true;
        }
        ApplyTranscriptUiState();

        TxtFileInfo.Text = $"正在解码：{Path.GetFileName(path)} …";
        BtnAnalyze.IsEnabled = false;
        ProgBar.Visibility = Visibility.Visible;
        ProgBar.IsIndeterminate = true;
        Win?.Status("正在用 ffmpeg 解码音频…");

        string ffmpegOk = FFmpegLocator.TryFind() ? "ok" : "no";
        string thePath = path;

        var result = await Task.Run(() =>
        {
            try
            {
                if (ffmpegOk != "ok")
                    return new LoadResult { Error = "找不到 ffmpeg，无法解码音频" };

                var probe = FFmpegDecoder.Probe(thePath);
                if (!probe.HasAudio && !probe.HasVideo)
                    return new LoadResult { Error = "这个文件里既没有视频轨也没有音频轨" };

                var dec = new FFmpegDecoder();
                var pcm = dec.Decode(thePath, 16000);
                return new LoadResult { Pcm = pcm, Probe = probe };
            }
            catch (Exception ex)
            {
                return new LoadResult { Error = ex.Message };
            }
        });

        ProgBar.IsIndeterminate = false;
        ProgBar.Visibility = Visibility.Collapsed;

        if (result.Error != null)
        {
            TxtFileInfo.Text = "解码失败：" + result.Error;
            Win?.Status("解码失败");
            MessageBox.Show(result.Error, "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _s.ApplyDecodedSource(path, result.Pcm!, result.Probe, switchedSource);

        // 有决策就按决策上色；没有（刚导入新素材）就整条都画成"保留"
        Wave.SetData(_s.Pcm, ToTupleList(_s.KeepRanges()));
        BtnAnalyze.IsEnabled = true;
        BtnPlayRaw.IsEnabled = true;
        UpdateFileInfo();

        // 决策保住了 → 把段列表/统计按它重新铺出来（构造时虽然铺过一次，
        // 但那是在 Pcm 还是空的、TxtFileInfo 还没填的状态下铺的）
        if (!switchedSource && _s.HasPlan)
        {
            RebuildRows();
            UpdateStats();
        }

        string dur = FormatTime(_s.Pcm!.DurationSec);
        string tail = _s.Probe.HasVideo ? " · 有画面，可导出视频" : " · 纯音频";
        if (!switchedSource && _s.HasPlan)
            Win?.Status($"已载入 {_s.SourceFileName}（{dur}）{tail} · 保留上次的粗剪决策"
                        + $"（{_s.Plan!.Segments.Count(s => s.Action == CutAction.Keep)} 段保留）");
        else
            Win?.Status($"已加载 {_s.SourceFileName}（{dur}）{tail}");

        RefreshDecisionUi();
        RefreshScriptUi();
    }

    private void UpdateFileInfo()
    {
        if (_s.Pcm == null) return;

        string dur = FormatTime(_s.Pcm.DurationSec);
        string kind = _s.Probe.HasVideo ? "视频+音频" : "仅音频";
        TxtFileInfo.Text = $"{_s.SourceFileName}   时长 {dur}   采样率 {_s.Pcm.SampleRate}Hz   {kind}";
    }

    // ========================================================
    // ② 转写稿
    // ========================================================
    //
    // 【这一环是"外包"的，界面上要成对出现】
    //   转写不由工作台干（要模型 / 算力 / 联网），是交给外部 Agent 的。
    //   所以这一步在界面上其实是两半：
    //     ①「复制转写提示词」—— 把"要什么、写到哪、什么格式"说全，粘给 Agent；
    //     ②「导入转写稿」   —— 把 Agent 写好的稿子读回来。
    //   两半必须指向**同一个位置**（<项目夹>\stages\01-transcript\transcript.json），
    //   否则就是"写这边、找那边"，白折腾 —— 这是这一环唯一容易出错的地方。
    //
    // 【为什么提示词要内置，不让用户自己写】
    //   里面最要紧的一条是"要原始 ASR，不要整理后的成稿"。
    //   这条一旦没说清，Agent 十有八九交一份通顺的整理稿回来，
    //   而粗剪拿到整理稿会几乎失效（实测：486.84s vs 430.44s，差 56 秒）。
    //   这种失败在界面上看不出来（稿子读进去了、也有句子），只能靠事前写死要求。

    /// <summary>「复制转写提示词」—— 生成提示词并塞进剪贴板。</summary>
    private void BtnCopyTranscriptPrompt_Click(object sender, RoutedEventArgs e)
    {
        var project = WorkbenchWindow.Session.Current;
        string outPath = TranscriptPrompt.OutputPathOf(_s.Config, project);

        // 顺手把目标目录建出来：Agent 一写就成，少一个"目录不存在"的失败点
        try
        {
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* 建不出来也不该拦住复制 —— 提示词里已经写了让 Agent 自己建 */ }

        string text = TranscriptPrompt.Build(_s.SourcePath, _s.Pcm?.DurationSec ?? 0, outPath);

        if (CopyText(text))
        {
            Win?.Status($"已复制转写提示词 —— 粘给 Agent 即可，它会写到 {outPath}");
            if (string.IsNullOrWhiteSpace(_s.SourcePath))
                Win?.Status("⚠ 还没加载音频：提示词里的音频路径留了空，记得先加载音频再复制。");
        }
        else
        {
            MessageBox.Show(
                "剪贴板被别的程序占着，连着试了几次都没写进去。\n\n" +
                "等一两秒再点一次就好 —— 这不是按钮坏了，是 Windows 剪贴板同一时刻\n" +
                "只允许一个程序占用。",
                "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 往剪贴板写文本，失败自动重试。
    ///
    /// 【为什么不能只写一句 Clipboard.SetText 就完事】
    ///   Windows 的剪贴板是全局独占资源：输入法、截图工具、剪贴板历史，
    ///   任何一个正开着它就抛 CLIPBRD_E_CANT_OPEN。这不是异常情况，是常态，
    ///   而且发生时机完全随机 —— 如果不管，用户偶发点一次没反应，只会以为按钮坏了。
    ///   隔 90ms 重试几次能盖掉绝大多数偶发占用。
    /// </summary>
    private static bool CopyText(string text)
    {
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(90); }
        }
        return false;
    }

    private void BtnTranscript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择转写稿（★要「原始 ASR」，不是整理后的成稿）",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            FileName = TranscriptPrompt.FileName,   // 约定文件名，预填上省一次手打
        };

        // 默认落在约定位置 —— 外部 Agent 就往这里写，不用到处找。
        // ★ 优先项目内的 stages\01-transcript（"复制提示词"让 Agent 写的就是它），
        //   没开项目时才退回全局配置 / 上次的素材目录。
        string conv = TranscriptPrompt.OutputDirOf(_s.Config, WorkbenchWindow.Session.Current);
        if (Directory.Exists(conv))
            dlg.InitialDirectory = conv;
        else if (Directory.Exists(_s.Config.LastMediaDir))
            dlg.InitialDirectory = _s.Config.LastMediaDir;

        if (dlg.ShowDialog() != true) return;

        var res = TranscriptImporter.LoadFile(dlg.FileName);
        if (res.Transcript == null)
        {
            MessageBox.Show("读不出来：" + string.Join(Environment.NewLine, res.Notes),
                            "转写稿导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _s.Transcript = res.Transcript;
        _s.TranscriptPath = dlg.FileName;
        ApplyTranscriptUiState();

        int n = res.Transcript.Segments.Count;
        Win?.Status($"转写稿已加载：{n} 句，语义层会启用");

        if (res.Notes.Count > 0)
            Win?.Status("导入说明：" + string.Join("；", res.Notes));

        // ---- 防呆：拿错稿子是最容易犯、也最没提示的错 ----
        //
        // 这是真实踩过的坑：外部 Agent 交付的是「整理后的成稿」，
        // 重录和卡壳已被它提前删干净。算法拿着一份没有废话的稿子，
        // 表现就是"只剪了空白" —— 用户会以为算法不行，其实是原料不对。
        //
        // 判据：ASR 的碎片粒度通常在 2~4 秒/句。
        // 若平均句长超过 8 秒且总句数偏少，基本可以断定是整理稿。
        double avg = n > 0 ? res.Transcript.Segments.Average(s => Math.Max(0.1, s.EndSec - s.StartSec)) : 0;
        double totalSec = res.Transcript.Segments.Count > 0 ? res.Transcript.Segments[^1].EndSec : 0;
        if (totalSec > 60 && (avg > 8.0 || (totalSec > 300 && n < 80)))
        {
            Win?.Status($"⚠ 这看起来像是「整理后的成稿」（{n} 句，平均 {avg:F1}s/句）——" +
                        "重录和卡壳可能已被提前删掉，粗剪会只剪空白。请改用原始 ASR。");
            MessageBox.Show(
                $"这份稿子平均 {avg:F1} 秒一句，总共只有 {n} 句。\n\n" +
                "ASR 的原始输出通常 2~4 秒一句、几百个片段，\n" +
                "而\"整理后的成稿\"会把重录和卡壳提前删掉。\n\n" +
                "如果你要剪掉卡壳和重录，需要的是**原始 ASR**：\n" +
                "  · 同一句话说了 4 遍 → 4 遍都要在文件里\n" +
                "  · 半截话、口吃、试录 → 全部保留\n\n" +
                "回粗剪页点一下就成：「复制转写提示词」——\n" +
                "提示词里已经写好\"要原始 ASR、不要整理稿\"这条要求。",
                "可能拿到了整理稿（粗剪会失效）", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClearTranscript_Click(object sender, RoutedEventArgs e)
    {
        _s.Transcript = null;
        _s.TranscriptPath = "";
        ApplyTranscriptUiState();
        Win?.Status("已移除转写稿 —— 下次分析只走波形轨");
    }

    private void ApplyTranscriptUiState()
    {
        bool has = _s.Transcript != null;
        BtnClearTranscript.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ChkFalseStart.IsEnabled = has;
        // 没有稿子时，"半截重说"这类判断无从下手 —— 灰掉而不是偷偷失效
        if (!has) ChkFalseStart.IsChecked = false;
        ChkFiller.IsEnabled = has;
        if (!has) ChkFiller.IsChecked = false;
    }

    // ========================================================
    // ②·五 对稿（T-28）—— 拿写作台的 script.md 当标准答案
    // ========================================================
    //
    // 【这一环和"转写稿"是完全两件事，别混】
    //   转写稿（transcript.json）：录音**说了什么字**。它由外部 Agent 转写而来。
    //   稿子（script.md）        ：你**本来打算说什么**。它在写作台里写。
    //   对稿 = 拿后者去对前者，于是能回答"这句是说错了重录吗"。
    //
    // 【为什么界面上要单独显示一行状态】
    //   用户不可能记得"这个项目到底有没有稿子"。没稿子时对稿静默不生效，
    //   如果不显示，用户会以为"对稿功能没做出来"。
    //   有稿子时显示句数，也是给一个"它确实读到了"的确认。

    /// <summary>当前项目的 script.md 路径。没开项目 / 没稿子 → 空串。</summary>
    private static string CurrentScriptPath()
    {
        var r = WorkbenchWindow.Session?.Current;
        if (r == null) return "";
        try
        {
            string p = Path.Combine(r.Dir, "script.md");
            return File.Exists(p) ? p : "";
        }
        catch { return ""; }
    }

    /// <summary>读稿子正文。读不到就返回空串 —— 等价于"没稿子"，不算错误。</summary>
    private static string ReadScriptMarkdown()
    {
        string p = CurrentScriptPath();
        if (p.Length == 0) return "";
        try { return File.ReadAllText(p); } catch { return ""; }
    }

    /// <summary>刷新工具栏那一格「对稿：…」。</summary>
    private void RefreshScriptUi()
    {
        string p = CurrentScriptPath();
        if (p.Length == 0)
        {
            TxtScriptStatus.Text = "对稿：未找到稿子";
            TxtScriptStatus.Foreground = Res("TextFaint", 0x6B7280);
            return;
        }

        int n = ScriptAligner.ParseScript(ReadScriptMarkdown()).Count;
        TxtScriptStatus.Text = $"对稿：稿子 {n} 句";
        TxtScriptStatus.Foreground = Res("Ok", 0x22C55E);
    }

    /// <summary>刷新右下角「对稿偏差」列表。</summary>
    private void RebuildScriptRows()
    {
        _scripts.Clear();
        var sc = _s.Plan?.Script;

        if (sc == null || !sc.Available)
        {
            TxtScriptHint.Text = "未找到稿子（script.md）—— 写作台写好稿子后自动生效";
            return;
        }

        // 稿子有、但对不上（改过版本 / 拿错文件）→ 说明为什么没采用，别硬着头皮列假偏差
        if (!sc.Used)
        {
            TxtScriptHint.Text = sc.Warnings.Count > 0
                ? "未采用：" + sc.Warnings[0]
                : "对稿未采用（可信度不足）";
            return;
        }

        if (sc.Deviations != null)
            foreach (var d in sc.Deviations)
                _scripts.Add(new ScriptRow(d));

        TxtScriptHint.Text = _scripts.Count == 0
            ? $"稿子 {sc.SentenceCount} 句 · 全部对上，无偏差"
            : $"稿子 {sc.SentenceCount} 句 · 对上 {sc.AlignedCount} 句 · 偏差 {_scripts.Count} 处";
    }

    /// <summary>取主题资源里的画刷；取不到就用兜底色（保证任何主题下都不会崩）。</summary>
    private Brush Res(string key, uint fallbackRgb)
    {
        if (TryFindResource(key) is Brush b) return b;
        return new SolidColorBrush(Color.FromRgb(
            (byte)(fallbackRgb >> 16), (byte)(fallbackRgb >> 8), (byte)fallbackRgb));
    }

    // ========================================================
    // ③ 跑分析
    // ========================================================

    private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbPreset.SelectedIndex < 0) return;
        _s.Config.LastPreset = CmbPreset.SelectedIndex;
        _s.SaveConfig();
    }

    private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Pcm == null) return;

        var pcm = _s.Pcm;
        string src = _s.SourcePath;

        // ---- 组装参数 ----
        AutoCutOptions opt = CmbPreset.SelectedIndex switch
        {
            0 => AutoCutOptions.Aggressive(),
            1 => AutoCutOptions.Balanced(),
            _ => AutoCutOptions.Conservative(),
        };

        // 没有转写稿 → 语义层这几项自动关掉（它们必须有文字才能判断）
        bool hasText = _s.Transcript != null;
        opt.Text.Enabled = hasText && ((ChkFiller.IsChecked ?? false) || (ChkFalseStart.IsChecked ?? false));
        opt.Text.RemoveFillers = hasText && (ChkFiller.IsChecked ?? false);
        opt.Text.DetectFalseStart = hasText && (ChkFalseStart.IsChecked ?? false);
        opt.Text.DetectStutter = hasText && (ChkFalseStart.IsChecked ?? false);
        opt.Text.DetectTextRepeat = hasText && (ChkFalseStart.IsChecked ?? false);
        opt.DetectRepeats = ChkRepeat.IsChecked ?? false;

        BtnAnalyze.IsEnabled = false;
        ProgBar.Visibility = Visibility.Visible;
        ProgBar.IsIndeterminate = true;
        Win?.Status("正在分析 …");

        var t = _s.Transcript;

        // ★ 对稿（T-28）：把写作台那份稿子（script.md）一并交给分析。
        //   读不到就是空串，分析引擎会自动忽略 —— 老项目一行都不用改。
        string scriptMd = ReadScriptMarkdown();

        var plan = await Task.Run(() => AutoCutEngine.Analyze(pcm, opt, t, "workbench", src, scriptMd));

        ProgBar.IsIndeterminate = false;
        ProgBar.Visibility = Visibility.Collapsed;
        BtnAnalyze.IsEnabled = true;

        _s.Plan = plan;
        _s.LastOptions = opt;

        RebuildRows();
        Wave.SetData(pcm, ToTupleList(_s.KeepRanges()));
        UpdateStats();
        RefreshDecisionUi();

        // ---- 顺便说说这一轮到底走了哪几条轨 ----
        var sb = new StringBuilder();
        sb.Append($"粗剪完成：{FormatTime(plan.Stats.OriginalSec)} → {FormatTime(plan.Stats.FinalSec)}");
        sb.Append($"，剪掉 {plan.Stats.RemovedSec:F1}s");
        if (plan.Transcript != null && plan.Transcript.Available)
            sb.Append($" · 语义层已生效（对齐度 {plan.Transcript.Score:P0}）");
        else
            sb.Append(" · 仅波形轨");
        Win?.Status(sb.ToString());

        if (plan.Transcript?.Warnings.Count > 0)
            Win?.Status("转写稿提示：" + string.Join("；", plan.Transcript.Warnings));

        // ---- 对稿这一轮到底有没有用上 ----
        var sc = plan.Script;
        if (sc != null && sc.Available)
        {
            if (sc.Used)
                Win?.Status($"对稿已生效：稿子 {sc.SentenceCount} 句 · 对上 {sc.AlignedCount} 句"
                            + (sc.RetakeCount > 0 ? $" · 认出 {sc.RetakeCount} 处重录" : " · 无重录")
                            + (sc.AdLibCount > 0 ? $" · {sc.AdLibCount} 处稿外发挥" : ""));
            else
                Win?.Status("对稿提示：" + (sc.Warnings.Count > 0
                    ? string.Join("；", sc.Warnings)
                    : "稿子没对上（可能没转写稿），本轮不按稿子改剪法"));
        }
    }

    // ========================================================
    // ④ 列表与统计
    // ========================================================

    private void RebuildRows()
    {
        _rows.Clear();
        if (_s.Plan == null) { RebuildScriptRows(); return; }

        foreach (var seg in _s.Plan.Segments)
            _rows.Add(new SegRow(seg));

        _suggests.Clear();
        if (_s.Plan.Suggestions != null)
        {
            foreach (var sg in _s.Plan.Suggestions)
                _suggests.Add(new SuggestRow(sg));
        }

        bool hasSeg = _rows.Count > 0;
        BtnRestoreAll.IsEnabled = hasSeg;
        BtnRestoreSilence.IsEnabled = hasSeg;
        BtnExportAudio.IsEnabled = hasSeg;
        BtnExportVideo.IsEnabled = hasSeg && _s.Probe.HasVideo;
        BtnExportJson.IsEnabled = hasSeg;
        BtnPlayCut.IsEnabled = hasSeg;
        BtnApplySuggest.IsEnabled = _suggests.Count > 0;

        TxtSegCount.Text = hasSeg
            ? $"共 {_rows.Count} 段：保留 {_s.Plan!.Stats.KeepCount}，剪掉 {_s.Plan.Stats.DropCount}" +
              (_suggests.Count > 0 ? $"，另有 {_suggests.Count} 处待你确认" : "")
            : "";

        TxtExportHint.Text = hasSeg ? "导出前可以随时双击修改" : "粗剪之后才能导出";

        RebuildScriptRows();
    }

    private void UpdateStats()
    {
        if (_s.Plan == null) return;
        var st = _s.Plan.Stats;

        TxtOrig.Text = FormatTime(st.OriginalSec);
        TxtFinal.Text = FormatTime(st.FinalSec);
        TxtRemoved.Text = $"-{FormatTime(st.RemovedSec)}  ({_s.Plan.Segments.Count(s => s.Action == CutAction.Drop)} 处)";
        double ratio = st.OriginalSec > 0 ? st.FinalSec / st.OriginalSec : 0;
        TxtRatio.Text = $"{ratio:P1}";

        var tc = _s.Plan.Transcript;
        if (tc != null && tc.Available)
            TxtMode.Text = $"双轨（对齐 {tc.Score:P0}）";
        else
            TxtMode.Text = "仅波形轨";
    }

    /// <summary>
    /// 双击列表行 → 切换保留/剪掉。
    /// 这是"人工兜底"最主要的入口：算法拿不准的地方，你一眼就能改。
    /// </summary>
    private void GridSeg_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GridSeg.SelectedItem is not SegRow row) return;

        row.Segment.Action = row.Segment.Action == CutAction.Keep ? CutAction.Drop : CutAction.Keep;
        if (row.Segment.Action == CutAction.Drop)
            row.Segment.Reason = CutReason.Manual;

        row.Refresh();

        Wave.SetKeepRanges(ToTupleList(_s.KeepRanges()));
        UpdateStats();
        RefreshDecisionUi();

        Win?.Status($"第 {row.Id} 段已改为「{row.ActionText}」");
    }

    private void GridSeg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridSeg.SelectedItem is not SegRow row) return;
        // 选中行就把波形播放头挪过去，方便对着看
        Wave.PlayheadSec = row.Segment.SourceStartSec;
        TxtPlayhead.Text = $"选中：{row.StartText} – {row.EndText}";
    }

    private void BtnRestoreAll_Click(object sender, RoutedEventArgs e) => Restore(s => true);

    private void BtnRestoreSilence_Click(object sender, RoutedEventArgs e) =>
        Restore(s => s.Reason == CutReason.Silence);

    /// <summary>
    /// 把符合条件的段恢复成保留。
    /// </summary>
    private void Restore(Func<CutSegment, bool> predicate)
    {
        if (_s.Plan == null) return;

        int n = 0;
        foreach (var seg in _s.Plan.Segments)
        {
            if (seg.Action == CutAction.Drop && predicate(seg))
            {
                seg.Action = CutAction.Keep;
                n++;
            }
        }

        foreach (var r in _rows) r.Refresh();
        Wave.SetKeepRanges(ToTupleList(_s.KeepRanges()));
        UpdateStats();
        RefreshDecisionUi();
        Win?.Status($"已还原 {n} 段");
    }

    // ========================================================
    // ④.5 把决策交给下游
    // ========================================================
    //
    // 【为什么要跟精剪页一样有一个「应用到下游」】
    //   以前粗剪是"跑完即生效"，没有显式动作。这带来两个毛病：
    //     ① 用户根本不知道"下游现在到底按哪份在剪" —— 黑箱；
    //     ② 一旦精剪接管了下游，再回头调粗剪时会很尴尬：
    //        调完到底生效了没有？会不会把精剪结果冲掉？没人说得清。
    //
    //   现在粗剪这一步也有了明确的"交棒"动作：
    //     · 调粗剪（重跑/双击改段/还原）**只改粗剪自己**，
    //       不动精剪时间轴，也不自动抢走下游控制权；
    //     · 想让下游改用粗剪结果，必须明确点一次这个按钮。
    //
    //   这样"手滑重跑了一次粗剪"最多是白跑一趟，不会毁掉精剪成果。

    /// <summary>刷新"下游当前使用"这一行的显示与按钮状态。</summary>
    private void RefreshDecisionUi()
    {
        string label = _s.ActiveDecisionLabel;

        if (_s.ActiveDecision == DecisionSource.Coarse)
        {
            TxtDecision.Text = label;
            BtnApplyDownstream.Content = "✓ 已应用到下游";
            BtnApplyDownstream.IsEnabled = false;   // 本来就是它，不用再点
            TxtDecisionHint.Text = "分镜 / 渲染 / 组接导出都按这份粗剪结果走。";
        }
        else if (_s.ActiveDecision == DecisionSource.Fine)
        {
            // 下游在用精剪。这里分两种情况，因为措辞完全不同：
            //   · 本项目跑过粗剪 → 随时可以切回来
            //   · 本项目根本没跑过粗剪（直接导素材手工剪的项目）→
            //     没有"粗剪结果"这个备胎，按钮必须灰着并且说清原因，
            //     否则用户会以为按钮坏了（2026-09-29 用户的真实处境）。
            TxtDecision.Text = label;
            BtnApplyDownstream.Content = "改用粗剪结果";
            BtnApplyDownstream.IsEnabled = _s.HasPlan;
            TxtDecisionHint.Text = _s.HasPlan
                ? "下游现在用的是精剪时间轴 —— 点右边按钮可切回粗剪（精剪结果会保留，回精剪页再点一次就能切回来）。"
                : "下游现在用的是精剪时间轴。本项目还没跑过粗剪，所以没有粗剪结果可切 —— 要改剪法请回精剪页。";
        }
        else
        {
            TxtDecision.Text = "尚未应用";
            BtnApplyDownstream.Content = "应用到下游";
            BtnApplyDownstream.IsEnabled = _s.HasPlan;
            TxtDecisionHint.Text = _s.HasPlan
                ? "点右边按钮，让分镜 / 渲染 / 导出都按这份粗剪结果走。"
                : "先跑一次粗剪，就有了可以交给下游的剪辑决策。";
        }

        // 顶栏那句"下游使用：…"也跟着刷 —— 应用完不必等切页才看得到变化
        Win?.RefreshDecisionIndicator();
    }

    private void BtnApplyDownstream_Click(object sender, RoutedEventArgs e)
    {
        if (!_s.HasPlan)
        {
            Win?.Status("还没有粗剪结果 —— 先点「开始粗剪」");
            return;
        }

        bool hadFineCut = _s.ActiveDecision == DecisionSource.Fine;

        // 交棒：下游改用粗剪决策。精剪时间轴本体一个字都不动。
        _s.UseCoarseDecision();
        RefreshDecisionUi();

        int keepSegs = _s.Plan!.Segments.Count(s => s.Action == CutAction.Keep);
        double total = _s.DecisionDurationSec;
        string head = $"已应用到下游：粗剪 · {keepSegs} 段 · {WorkbenchState.FormatDuration(total)}"
                      + " —— 分镜 / 渲染 / 组接导出现在都按这份在走";

        if (hadFineCut)
            head += "。你的精剪时间轴没有丢，回精剪页点一次「应用到下游」就能切回精剪结果";

        Win?.Status(head);
    }

    // ========================================================
    // ⑤ 建议清单（算法拿不准的）
    // ========================================================

    private void Suggest_Toggled(object sender, RoutedEventArgs e)
    {
        BtnApplySuggest.IsEnabled = _suggests.Any(x => x.IsAccepted);
    }

    private void BtnApplySuggest_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Plan == null) return;

        int n = 0;
        foreach (var row in _suggests.Where(x => x.IsAccepted))
        {
            PunchDrop(_s.Plan.Segments, row.Item.SourceStartSec, row.Item.SourceEndSec);
            n++;
        }

        _suggests.Clear();
        RebuildRows();
        Wave.SetKeepRanges(ToTupleList(_s.KeepRanges()));
        UpdateStats();
        RefreshDecisionUi();
        Win?.Status($"已按勾选剪掉 {n} 处");
    }

    /// <summary>
    /// 在决策文件里"打一个洞"：把 [a,b] 这段改成剪掉。
    ///
    /// 为什么不能直接加一段 Drop：
    ///   Segments 是覆盖全片、首尾相接的区间列表。
    ///   在里面插一个区间会打破这个前提，后面的 ComputeTimeline 就算错了。
    ///   所以要做的是"切割"——把一个 Keep 段拆成两截，中间那截标成剪掉。
    /// </summary>
    private static void PunchDrop(List<CutSegment> segs, double a, double b)
    {
        var result = new List<CutSegment>();

        foreach (var seg in segs)
        {
            double s = seg.SourceStartSec, e = seg.SourceEndSec;

            // 不相交，或这段已经是剪掉的：原样保留
            if (b <= s || a >= e || seg.Action == CutAction.Drop)
            {
                result.Add(seg);
                continue;
            }

            // ---- 有重叠 ----
            // 左边剩下的部分
            if (a > s + 0.001)
                result.Add(Clone(seg, s, Math.Min(a, e)));

            // 中间：要剪掉的部分
            double cutS = Math.Max(s, a);
            double cutE = Math.Min(e, b);
            if (cutE > cutS)
            {
                var mid = Clone(seg, cutS, cutE);
                mid.Action = CutAction.Drop;
                mid.Reason = CutReason.Manual;
                mid.Confidence = 1.0;
                mid.Note = "用户在建议清单里确认删除";
                result.Add(mid);
            }

            // 右边剩下的部分
            if (b < e - 0.001)
                result.Add(Clone(seg, Math.Max(s, b), e));
        }

        segs.Clear();
        segs.AddRange(result);
    }

    private static CutSegment Clone(CutSegment src, double start, double end) => new()
    {
        Id = src.Id,
        SourceStartSec = start,
        SourceEndSec = end,
        Action = src.Action,
        Reason = src.Reason,
        Confidence = src.Confidence,
        Text = src.Text,
        Note = src.Note,
        UserEdited = true,
    };

    // ========================================================
    // ⑥ 播放
    // ========================================================

    private void BtnPlayRaw_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Pcm == null) return;
        string tmp = Path.Combine(Path.GetTempPath(), "yixuan_raw.wav");
        WavWriter.Write16(tmp, _s.Pcm.Samples, _s.Pcm.SampleRate);
        StartPlay(tmp, false);
        TxtPlayState.Text = "正在听：原始素材";
    }

    private void BtnPlayCut_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Pcm == null) return;

        var ranges = ToTupleList(_s.KeepRanges());
        if (ranges.Count == 0)
        {
            MessageBox.Show("没有要保留的内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 用"当前决策"生成预览：你改了哪一段，正文马上能听出来差别
        string key = string.Join("|", ranges.Select(r => $"{r.Start:F2}-{r.End:F2}"));
        if (key != _lastPreview)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "yixuan_cut.wav");
            WavWriter.WriteConcat(tmp, _s.Pcm, ranges);
            _lastPreview = key;
        }

        StartPlay(Path.Combine(Path.GetTempPath(), "yixuan_cut.wav"), true);
        TxtPlayState.Text = "正在听：粗剪结果（这就是成片的音轨）";
    }

    private void StartPlay(string wavPath, bool isCut)
    {
        StopPlayback();

        try
        {
            _reader = new AudioFileReader(wavPath);
            _player = new WaveOutEvent();
            _player.Init(_reader);
            _playingCut = isCut;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _player.PlaybackStopped += (_, _) =>
            {
                Win?.Dispatcher.Invoke(() => TxtPlayState.Text = "播放结束");
            };
            _player.Play();

            BtnStop.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("播放失败：" + ex.Message, "播放", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_reader == null || _s.Pcm == null) return;

        double t = _reader.CurrentTime.TotalSeconds;

        // 听粗剪结果时，播放位置是"成片时间"，要反算回源文件的哪儿，
        // 播放头才能落在波形正确的位置上。
        if (_playingCut)
        {
            double src = TimelineToSource(t);
            Wave.PlayheadSec = src;
            TxtPlayhead.Text = $"成片 {FormatTime(t)} ← 原素材 {FormatTime(src)}";
        }
        else
        {
            Wave.PlayheadSec = t;
            TxtPlayhead.Text = $"原素材 {FormatTime(t)}";
        }
    }

    /// <summary>
    /// 成片时间 → 源文件时间。播放播放头需要这个换算。
    ///
    /// 【为什么要换算】
    ///   听粗剪结果时，播放器走到第 3 秒，那是"成片的第 3 秒"。
    ///   但波形画的是源素材，源文件里对应的可能是第 7 秒（前面剪掉 4 秒）。
    ///   不换算的话，播放头会停在错的地方。
    /// </summary>
    private double TimelineToSource(double tlSec)
    {
        if (_s.Plan == null) return tlSec;

        foreach (var t in _s.Plan.ComputeTimeline())
        {
            if (tlSec >= t.TimelineStartSec && tlSec < t.TimelineEndSec)
            {
                double offset = tlSec - t.TimelineStartSec;
                return t.SourceStartSec + offset;
            }
        }
        return tlSec;
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        _timer?.Stop();
        _timer = null;

        if (_player != null)
        {
            try { _player.Stop(); _player.Dispose(); } catch { }
            _player = null;
        }
        _reader?.Dispose();
        _reader = null;

        BtnStop.IsEnabled = false;
        TxtPlayState.Text = "";
    }

    // ========================================================
    // ⑦ 导出
    // ========================================================

    private void BtnExportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Plan == null) return;

        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_粗剪.m4a";
        var dlg = new SaveFileDialog
        {
            Title = "导出剪好的音频",
            Filter = "音频|*.m4a;*.mp3;*.wav",
            FileName = def,
            InitialDirectory = PickInitialDir(),
        };
        if (dlg.ShowDialog() != true) return;

        DoExport(dlg.FileName, withVideo: false);
    }

    private void BtnExportVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Plan == null) return;

        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_粗剪.mp4";
        var dlg = new SaveFileDialog
        {
            Title = "导出剪好的视频",
            Filter = "视频|*.mp4",
            FileName = def,
            InitialDirectory = PickInitialDir(),
        };
        if (dlg.ShowDialog() != true) return;

        DoExport(dlg.FileName, withVideo: true);
    }

    private string PickInitialDir()
    {
        if (!string.IsNullOrWhiteSpace(_s.Config.LastOutputDir) &&
            Directory.Exists(_s.Config.LastOutputDir))
            return _s.Config.LastOutputDir;
        return Path.GetDirectoryName(_s.SourcePath) ?? "";
    }

    private async void DoExport(string outPath, bool withVideo)
    {
        if (_s.Plan == null) return;

        _s.Config.LastOutputDir = Path.GetDirectoryName(outPath) ?? "";
        _s.SaveConfig();

        var opt = new ExportOptions
        {
            WithVideo = withVideo,
            WithAudio = true,
            Progress = new Progress<string>(line => Win?.Status(TrimLine(line))),
        };

        string src = _s.SourcePath;
        var ranges = _s.KeepRanges().ToList();

        Win?.Status("正在导出…");
        BtnExportAudio.IsEnabled = false;
        BtnExportVideo.IsEnabled = false;

        var res = await Task.Run(() => FFmpegExporter.ExportRanges(src, ranges, outPath, opt));

        BtnExportAudio.IsEnabled = true;
        BtnExportVideo.IsEnabled = _s.HasPlan && _s.Probe.HasVideo;

        if (!res.Success)
        {
            MessageBox.Show(res.Error + Environment.NewLine + Environment.NewLine +
                            "ffmpeg 输出片段：" + Environment.NewLine + res.LogTail,
                            "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Win?.Status("导出失败");
            return;
        }

        _s.LastExportPath = outPath;
        string polite = res.ElapsedSec > 0 && _s.Plan != null
            ? $"（耗时 {res.ElapsedSec:F1}s，{_s.Plan.FinalDurationSec / res.ElapsedSec:F1}× 实时）"
            : "";
        Win?.Status($"导出完成：{Path.GetFileName(outPath)} {polite}");
        TxtExportHint.Text = $"已导出 {Path.GetFileName(outPath)}{polite}";

        // 导出完顺便打开所在文件夹，省得你去翻
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{outPath}\"",
                UseShellExecute = true,
            });
        }
        catch { /* 打不开就算了，不影响导出结果 */ }
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Plan == null) return;

        string def = Path.GetFileNameWithoutExtension(_s.SourcePath) + "_粗剪.json";
        var dlg = new SaveFileDialog
        {
            Title = "导出决策文件",
            Filter = "JSON|*.json",
            FileName = def,
            InitialDirectory = PickInitialDir(),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            ContractJson.WriteFile(dlg.FileName, _s.Plan);
            Win?.Status($"决策文件已保存到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ========================================================
    // 小工具
    // ========================================================

    /// <summary>
    /// 把秒数写成 mm:ss.d 或 h:mm:ss。
    /// </summary>
    internal static string FormatTime(double sec)
    {
        if (sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{(ts.Milliseconds / 100):D1}";
    }

    /// <summary>
    /// ffmpeg 的输出行很长，状态栏放不下。截一段关键的显示。
    /// </summary>
    private static string TrimLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        string t = line.Trim();
        return t.Length > 110 ? t[^110..] : t;
    }

    private sealed class LoadResult
    {
        public PcmData? Pcm { get; set; }
        public MediaProbe Probe { get; set; } = new();
        public string? Error { get; set; }
    }
}

// ============================================================
// 列表里的一行
// ============================================================

/// <summary>
/// DataGrid 的一行。包一层是为了让界面能收到变化通知 ——
/// 直接把 CutSegment 塞进表格，改了 Action 界面是不会刷新的。
/// </summary>
public sealed class SegRow : INotifyPropertyChanged
{
    public CutSegment Segment { get; }

    public SegRow(CutSegment seg) => Segment = seg;

    public string Id => Segment.Id;
    public string StartText => Step1CutView.FormatTime(Segment.SourceStartSec);
    public string EndText => Step1CutView.FormatTime(Segment.SourceEndSec);
    public string DurText => Segment.DurationSec.ToString("F1") + "s";
    public string ActionText => Segment.Action == CutAction.Keep ? "保留" : "剪掉";
    public string ReasonText => ReasonName(Segment.Reason);
    public string ConfText => (Segment.Confidence * 100).ToString("F0") + "%";
    public string Text => Segment.Text ?? "";

    public Brush ActionBrush =>
        Segment.Action == CutAction.Keep
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))
            : new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    /// <summary>
    /// 数据变了之后刷一下界面。
    /// </summary>
    public void Refresh()
    {
        Changed(nameof(ActionText));
        Changed(nameof(ReasonText));
        Changed(nameof(ActionBrush));
    }

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 原因名的中文化。界面上不能让用户看 "FalseStart"。
    /// </summary>
    private static string ReasonName(CutReason r) => r switch
    {
        CutReason.Normal => "正常语句",
        CutReason.Silence => "气口/静音",
        CutReason.Filler => "填充词",
        CutReason.Repeat => "重复",
        CutReason.Stutter => "口吃",
        CutReason.FalseStart => "半截重说",
        CutReason.OffTopic => "跑题",
        CutReason.Manual => "手动删",
        CutReason.Retake => "重录",
        _ => r.ToString(),
    };
}

/// <summary>
/// 建议清单里的一行。
/// </summary>
public sealed class SuggestRow : INotifyPropertyChanged
{
    public CutSuggestion Item { get; }
    private bool _accepted;

    public SuggestRow(CutSuggestion item) => Item = item;

    /// <summary>用户勾选了要剪掉它。</summary>
    public bool IsAccepted
    {
        get => _accepted;
        set { _accepted = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAccepted))); }
    }

    public string Display =>
        $"[{Step1CutView.FormatTime(Item.SourceStartSec)}–{Step1CutView.FormatTime(Item.SourceEndSec)}] " +
        $"{itemReason(Item.Reason)}：{Item.Evidence}";

    private static string itemReason(CutReason r) => r switch
    {
        CutReason.Filler => "可能是填充词",
        CutReason.Repeat => "可能是重复",
        CutReason.Stutter => "可能是口吃",
        CutReason.FalseStart => "可能是半截话",
        CutReason.Retake => "可能是重录",
        _ => "可疑",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 对稿偏差清单里的一行（T-28）。
///
/// 【为什么用不同颜色】
///   三种偏差的"严重程度"和"该不该动手"完全不同，用颜色一眼分开：
///     琥珀 = 稿上有、你没录（可能漏了内容 → 你最该注意的）
///     蓝　 = 稿上没有、你说了（临场发挥，通常不用管）
///     红　 = 同一句录了多遍（会被剪掉前几遍 → 看一眼确认没剪错）
/// </summary>
public sealed class ScriptRow
{
    public ScriptDeviation Item { get; }
    public ScriptRow(ScriptDeviation item) => Item = item;

    public string Display => Item.Kind switch
    {
        "notSpoken" => $"稿上有、没录到：{Trim(Item.Script)}",
        "adLib" => $"[{Step1CutView.FormatTime(Item.SourceStartSec)}] 稿外发挥：{Trim(Item.Spoken)}",
        "retake" => $"[{Step1CutView.FormatTime(Item.SourceStartSec)}–{Step1CutView.FormatTime(Item.SourceEndSec)}] 重录（保留后一遍）：{Trim(Item.Spoken)}",
        _ => Item.Note,
    };

    public Brush Brush => Item.Kind switch
    {
        "notSpoken" => new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
        "adLib" => new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
        "retake" => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _ => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
    };

    private static string Trim(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length > 40 ? s[..40] + "…" : s;
    }
}
