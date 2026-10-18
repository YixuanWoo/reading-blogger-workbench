// ============================================================
// EditorView.xaml.cs - 嵌入式精剪编辑器逻辑
//
// 【出身】这就是原来独立窗口 MainWindow.xaml.cs，外壳从 Window
//   换成了 UserControl，铺进工作台第②步（StepFineCutView）。
//   全部剪辑能力原样保留：拖块/裁切、剪刀、淡化、音量、撤销重做、
//   素材库、手机上传、快捷键。
//
// 【与原来的差异】
//   1. 基类 Window → UserControl；没有 Closed 事件，改为
//      Shutdown()（应用退出时调用，释放音频引擎和上传服务）。
//   2. 快捷键不再挂在窗口上，而是在 Loaded 时挂到宿主窗口的
//      PreviewKeyDown（UserControl 只有焦点在内部才收到键盘事件，
//      挂宿主窗口最可靠），Unloaded 时摘掉。
//   3. 工作台交接接口 LoadPreCutProject / GetTimelineKeeps 原样保留，
//      UISmoke 冒烟测试 Part E 直接测这两个方法。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace LiteCut;

/// <summary>嵌入式精剪编辑器（工作台完整编辑器）</summary>
public partial class EditorView : UserControl
{
    // ---------- 各模块实例 ----------
    private TimelineProject? _project;   // 时间轴项目（块列表）
    private readonly AudioEngine _engine = new();  // 音频引擎
    private readonly FileService _file;   // 文件服务（素材库）
    private readonly UploadServer _upload;// 局域网上传服务
    private UndoService? _undo;           // 撤销重做服务

    // ---------- 音量滑块状态 ----------
    private double _lastAppliedVolumeDb;

    // ---------- 滚动条标志位（防止死循环） ----------
    private bool _updatingScrollBar;

    // ---------- 音频预热（防抖计时器） ----------
    /// <summary>
    /// 时间轴内容一变就要把整条音频重新拼一遍。但一次操作（比如拖块）
    /// 可能连着触发好几个"内容变了"，所以等动静停下来 400ms 再拼，
    /// 免得同一个中间状态白拼好几遍。
    /// </summary>
    private readonly DispatcherTimer _audioPrewarmTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };

    // ---------- 快捷键宿主 ----------
    private Window? _hookedWindow;

    /// <summary>写日志到文件</summary>
    private void WriteLog(string message)
    {
        try
        {
            string exePath = Environment.ProcessPath!;
            string exeDir = Path.GetDirectoryName(exePath)!;
            string logFile = Path.Combine(exeDir, "logs", $"litecut_{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] [INFO] {message}\n");
        }
        catch { }
    }

    /// <summary>
    /// 素材库文件夹。2026-10-01 起不再指向任何外部固定目录：
    /// 当前打开了项目 → 用项目的「素材」夹（项目素材待在项目肚子里）；
    /// 没开项目 → 退到 exe 旁的「素材模板」夹（软件自带，自包含）。
    /// </summary>
    private static string LibraryDir
    {
        get
        {
            var proj = LiteCut.Workbench.WorkbenchWindow.Session.Current;
            if (proj != null && !string.IsNullOrWhiteSpace(proj.AssetsDir))
                return proj.AssetsDir;
            return Path.Combine(AppContext.BaseDirectory, "素材模板");
        }
    }

    /// <summary>构造：初始化所有服务、启动上传服务器、挂事件</summary>
    public EditorView()
    {
        InitializeComponent();

        // 1. 文件服务 + 上传服务器
        _file = new FileService(LibraryDir);
        _upload = new UploadServer(LibraryDir);
        if (_upload.Start())
        {
            var ips = Utils.GetLocalIPv4Addresses();
            string ip = ips.Count > 0 ? ips[0] : "未连接局域网";
            UploadHint.Text = "手机上传: http://" + ip + ":" + _upload.Port;
        }
        else
        {
            UploadHint.Text = "上传服务启动失败（端口被占用）";
        }

        // 2. 新建时间轴项目 + 撤销服务
        _project = new TimelineProject();
        Wave.SetProject(_project);
        _undo = new UndoService(_project);

        // 时间轴内容一变（增删/切开/撤销重做/打开项目还原），
        // 就让音频引擎在后台把整条音频重新拼好。
        // 这样按下空格是"立刻响"，而不是等它现场拼一两秒。
        // 拖块和裁切不走这里（它们只发 ClipChanged），单独在下面接一条。
        _project.Changed += (s, e) => ScheduleAudioPrewarm();

        // 预热防抖：动静停了才真的开拼
        _audioPrewarmTimer.Tick += (s, e) =>
        {
            _audioPrewarmTimer.Stop();
            PrewarmAudio();
        };

        // 波形交互：拖块/裁切结束后，记录撤销快照
        Wave.ClipChanged += (s, e) =>
        {
            _undo?.CaptureSnapshot();
            ScheduleAudioPrewarm();
        };

        // 选中变化（点选/框选/边缘点击）→ 刷新按钮可用状态
        Wave.SelectionChanged += (s, e) => UpdateButtonStates();

        // 拖动落点被拒绝（空档比这一整组窄）→ 明确告诉用户"放不下"，而不是默默弹回去
        Wave.DragRejected += (s, e) =>
        {
            const string msg = "这里放不下：这个空档比整组窄，块已弹回原位";
            var host = Workbench.WorkbenchWindow.FindOwner(this);
            if (host != null) host.Status(msg);
            else UploadHint.Text = msg;
        };

        // 视口变化时更新滚动条
        Wave.ViewportChanged += (s, e) => UpdateScrollBar();

        // 3. 监听上传事件 → 刷新素材库
        _upload.FileUploaded += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                UploadHint.Text = "已收到: " + e.FileName;
                LoadLibraryList();
            });
        };

        // 4. 素材库列表加载
        LoadLibraryList();

        // 5. 素材库列表：鼠标按住拖动时拖到时间轴
        LibraryList.MouseMove += LibraryList_MouseMove;

        // 6. 音量滑块：拖动松手才生效
        VolumeSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnVolumeDragCompleted));
        VolumeSlider.PreviewKeyUp += OnVolumeSliderKeyUp;

        // 播放时，引擎更新播放位置 → 视图跟随红线（只有播放中才会触发，未播放时不动视图）
        _engine.PositionChanged += (s, e) =>
        {
            Wave.FollowPlayhead();
        };

        // 播放自然结束，更新按钮
        _engine.PlaybackStopped += (s, e) =>
        {
            PlayButton.Content = "▶ 播放";
        };

        // 7. 快捷键：挂到宿主窗口（Loaded/Unloaded 反复摘挂，不会重复）
        Loaded += (s, e) => HookKeyboard();
        Unloaded += (s, e) =>
        {
            UnhookKeyboard();

            // 切走精剪页时如果还在播放，必须暂停 ——
            // 否则人会走到第③步了，第②步的音频还在耳机里响
            if (_engine.IsPlaying)
            {
                _engine.Pause();
                PlayButton.Content = "▶ 播放";
            }
        };

        // 8. 启动时按钮状态
        UpdateButtonStates();
    }

    // ============================================================
    // 生命周期
    // ============================================================

    /// <summary>
    /// 应用退出时释放引擎和上传服务。
    /// 编辑器随工作台常驻（缓存实例），所以不需要窗口级的 Closed。
    /// </summary>
    public void Shutdown()
    {
        _audioPrewarmTimer.Stop();
        _engine.Dispose();
        _upload.Dispose();
    }

    // ============================================================
    // 音频预热（让空格"跟手"的关键）
    // ============================================================
    //
    // 【为什么需要它】
    //   播放前要把时间轴上的所有块拼成一整条音频。这一步耗时跟块数成正比
    //   （你那个项目 219 块），如果等按下空格才开始拼，手感就是"卡一下"。
    //   所以改成：时间轴一改，就在后台先拼好；等你按空格时直接命中。
    //
    //   AudioEngine 内部按"内容指纹"判断缓存是否还有效 ——
    //   所以这里不用判断"这次改动会不会影响声音"，多调几次也只是白看一次指纹。

    /// <summary>安排一次预热（防抖：最后一次改动之后 400ms 才真的开始拼）。</summary>
    private void ScheduleAudioPrewarm()
    {
        _audioPrewarmTimer.Stop();
        _audioPrewarmTimer.Start();
    }

    private void PrewarmAudio()
    {
        if (_project == null || _project.Clips.Count == 0) return;
        _engine.Prewarm(_project);
    }

    /// <summary>时间轴上是否有块（精剪页用来判断要不要铺数据）。</summary>
    public bool IsEmpty => _project == null || _project.Clips.Count == 0;

    // ============================================================
    // 快捷键挂载（挂宿主窗口，保证焦点在任何地方都生效）
    // ============================================================

    private void HookKeyboard()
    {
        if (_hookedWindow != null) return;   // 已挂过
        _hookedWindow = Window.GetWindow(this);
        if (_hookedWindow != null)
            _hookedWindow.PreviewKeyDown += OnHostPreviewKeyDown;
    }

    private void UnhookKeyboard()
    {
        if (_hookedWindow == null) return;
        _hookedWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
        _hookedWindow = null;
    }

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e) => HandleShortcutKey(e);

    // ============================================================
    // 素材库（左侧常驻面板）
    // ============================================================

    /// <summary>重新读取素材库文件夹，刷新左侧列表</summary>
    private void LoadLibraryList()
    {
        var items = _file.ListLibrary();
        LibraryList.Items.Clear();
        foreach (var item in items)
        {
            LibraryList.Items.Add(item);
        }
    }

    /// <summary>「刷新」按钮</summary>
    private void OnRefreshLibraryClick(object sender, RoutedEventArgs e)
    {
        LoadLibraryList();
    }

    /// <summary>双击素材：插到时间轴末尾</summary>
    private void OnLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryList.SelectedItem is LibraryItem item)
        {
            InsertClipAtPlayhead(item.FilePath, item.FileName);
        }
    }

    /// <summary>素材库列表鼠标移动：按住左键拖动到时间轴</summary>
    private void LibraryList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (LibraryList.SelectedItem is not LibraryItem item) return;
        DragDrop.DoDragDrop(LibraryList, item.FilePath, DragDropEffects.Copy);
    }

    /// <summary>
    /// 右侧时间轴收到拖放：把素材插到时间轴末尾。
    /// </summary>
    private void OnWaveDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.StringFormat) is not string path) return;
        if (!File.Exists(path)) return;

        string fileName = Path.GetFileName(path);
        InsertClipAtPlayhead(path, fileName);
    }

    /// <summary>水平滚动条拖动：调整时间线视口位置</summary>
    private void OnWaveScroll(object sender, ScrollEventArgs e)
    {
        if (_updatingScrollBar) return;  // 防止死循环
        // 滚动条值就是视口起点（秒）
        Wave.SetViewStart(e.NewValue);
    }

    /// <summary>更新滚动条的范围和位置（随缩放变化）</summary>
    private void UpdateScrollBar()
    {
        _updatingScrollBar = true;  // 防止设置 Value 时触发 Scroll 事件
        try
        {
            var (viewStart, secPerPixel, viewWidth) = Wave.GetViewport();
            double totalDuration = _project != null ? _project.TotalDurationSec : 60;
            double viewDuration = viewWidth * secPerPixel;  // 当前视口能看到多少秒

            WaveScrollBar.Minimum = 0;

            // 如果所有内容都能显示，滚动条禁用（滑块最大）
            if (viewDuration >= totalDuration)
            {
                WaveScrollBar.Maximum = totalDuration;
                WaveScrollBar.ViewportSize = totalDuration;
                WaveScrollBar.Value = 0;
                WaveScrollBar.IsEnabled = false;
            }
            else
            {
                WaveScrollBar.Maximum = totalDuration - viewDuration;
                WaveScrollBar.ViewportSize = viewDuration;
                WaveScrollBar.Value = viewStart;
                WaveScrollBar.IsEnabled = true;
            }
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }

    /// <summary>
    /// 右键移除素材（只删"映射"，不删磁盘文件）：
    ///   - 如果被时间轴引用 → 提示先删掉引用块
    ///   - 没被引用 → 把路径记入"已移除清单"，素材区不再显示；文件原地不动
    /// </summary>
    private void OnLibraryDeleteClick(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not LibraryItem item) return;

        // 检查是否被时间轴引用
        if (_project != null && _project.IsSourceUsed(item.FilePath))
        {
            // 找出具体是哪几个块在引用，帮用户定位（而不是只说不让删）
            var usedBy = _project.Clips
                .Where(c => string.Equals(c.SourcePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"  · 「{c.SourceName}」 时间轴 {c.TimelineStartSec:0.0}s ~ {c.TimelineEndSec:0.0}s")
                .ToList();
            string detail = usedBy.Count > 0 ? string.Join("\n", usedBy) : "  （判定被引用，但未列出具体块）";
            MessageBox.Show(
                "素材「" + item.FileName + "」正被时间轴上的块引用，不能删除：\n\n" +
                detail + "\n\n" +
                "请先在时间轴上选中并删除这些块（Delete 键），再回来删除素材。",
                "精剪 - 无法删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            "从素材库移除：" + item.FileName + "？\n" +
            "（只移除素材库中的条目，磁盘上的源文件不会动）",
            "精剪 - 移除素材",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // 只删"映射"：把路径记入已移除清单，列表不再显示；磁盘文件原地不动、不进回收站
        try
        {
            _file.RemoveMapping(item.FilePath);
            LoadLibraryList();
            UploadHint.Text = "已移除: " + item.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show("移除失败：" + ex.Message, "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============================================================
    // 时间轴操作
    // ============================================================

    /// <summary>
    /// 把素材插入到时间轴末尾。
    /// 读素材时长 → 创建块 → 加到时间轴。
    /// </summary>
    private void InsertClipAtPlayhead(string filePath, string fileName)
    {
        if (_project == null) return;

        try
        {
            double duration = AudioEngine.GetDurationSec(filePath);

            // 插入位置 = 时间轴末尾（所有块之后）。
            // 注意：以前是"插到当前播放线位置"——如果你第二次拖入同一段完整素材，
            // 新块会叠在剪辑好的块上面；导出按块顺序写入，后写入的完整块会把剪辑结果
            // 整个覆盖掉，导出的就变成了"原素材"。所以改成插到末尾，绝不重叠。
            double insertSec = _project.Clips.Count == 0
                ? 0
                : _project.Clips.Max(c => c.TimelineEndSec);

            var clip = new TimelineClip
            {
                SourcePath = filePath,
                SourceName = fileName,
                SourceStartSec = 0,
                SourceEndSec = duration,
                SourceTotalSec = duration,  // 记录源素材总时长（裁切不能超出它）
                TimelineStartSec = insertSec,
                IsSelected = true
            };

            _project.PlayheadSec = insertSec;  // 红线跳到新块起点，方便立刻看到它
            WriteLog($"插入块: {fileName} 时长 {duration:F1}s 位置 {insertSec:F1}s");
            // 加到项目
            _project.ClearSelection();
            _undo?.CaptureSnapshot();  // 记录撤销快照
            _project.AddClip(clip);

            UpdateButtonStates();
            Wave.FitToContent();  // 自动缩放到合适级别
            UpdateScrollBar();  // 更新滚动条范围
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入失败：" + ex.Message, "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============================================================
    // 工作台交接接口
    // ============================================================
    //
    // 工作台的流程是「算法粗剪 → 人工精剪 → 分镜渲染」。
    // 精剪这一步复用编辑器的全部能力（拖块/裁切/淡化/撤销/试听），
    // 数据怎么进出就靠下面两个方法 —— 只传"保留区间"这种最小信息，
    // 不引入工作台的数据类型，两个模块保持解耦。

    /// <summary>
    /// 工作台 → 编辑器：把「粗剪后的保留区间」铺成时间轴块。
    ///
    /// 每个保留区间变成一个块，首尾相接排在时间轴上；
    /// 块的 SourceStart/End 指回原始文件的位置，所以后续裁切、拖拽
    /// 可以自然恢复被算法剪掉的内容（往两边拖就能"捞回来"）。
    /// </summary>
    public void LoadPreCutProject(string audioPath, IReadOnlyList<(double Start, double End)> keeps)
    {
        if (_project == null || _undo == null || keeps.Count == 0) return;

        try
        {
            // 播放中重铺 = 播放线程正在读的块列表被清空重建，必须先停
            // （「还原为粗剪」和「粗剪重跑后重进本页」都会走到这里）
            if (_engine.IsPlaying)
            {
                _engine.Stop();
                PlayButton.Content = "▶ 播放";
            }

            double total = AudioEngine.GetDurationSec(audioPath);
            string name = System.IO.Path.GetFileName(audioPath);

            _project.ClearSelection();
            _project.Clips.Clear();
            _undo.Reset();

            double cursor = 0;
            foreach (var k in keeps)
            {
                double a = Math.Max(0, k.Start);
                double b = Math.Min(total, k.End);
                if (b - a < 0.05) continue;   // 忽略过碎的段

                var clip = new TimelineClip
                {
                    SourcePath = audioPath,
                    SourceName = name,
                    SourceStartSec = a,
                    SourceEndSec = b,
                    SourceTotalSec = total,
                    TimelineStartSec = cursor,
                };
                cursor += clip.SourceDurationSec;
                _project.AddClip(clip);
            }

            _project.PlayheadSec = 0;
            Wave.SetProject(_project);
            Wave.FitToContent();
            _undo.CaptureSnapshot();
            UpdateButtonStates();
            UpdateScrollBar();
            WriteLog($"工作台交接：{keeps.Count} 段保留区间，铺开总长 {cursor:F1}s");
        }
        catch (Exception ex)
        {
            MessageBox.Show("从工作台接收粗剪数据失败：" + ex.Message, "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 源音频马上要被**原地重建**（V1.17.0：录音台「应用到下游」重写 vo.wav）。
    ///
    /// 【为什么只做两件事】
    ///   ① 停播放 —— 播放线程还在读样本数组，重建期间不该让它继续跑；
    ///   ② 清样本缓存 —— 缓存只认时间轴指纹，不认磁盘上的文件换没换。
    ///      不清的话，重铺时间轴之后听到的还是上一版母带，而且完全看不出异常。
    ///   不清块、不重置撤销：紧接着 `ClearTimeline()` 会做，这里只负责"松手"。
    /// </summary>
    public void ReleaseAudio()
    {
        try
        {
            if (_engine.IsPlaying)
            {
                _engine.Stop();
                PlayButton.Content = "▶ 播放";
            }
            _engine.InvalidateCache();
        }
        catch { /* 释放失败不该挡住重建母带 —— 后面 Replace 会报真正的原因 */ }
    }

    /// <summary>
    /// 清空编辑器的时间轴（换了源文件时用）。
    ///
    /// 【为什么不能靠 LoadPreCutProject(path, 空列表) 来清】
    ///   那个方法开头有 `keeps.Count == 0 → return`，空列表是"什么都不做"，
    ///   不是"清空"。所以单独给一个方法，语义才不含糊。
    ///
    /// 不动播放设备、不动音量/淡化设置，只把块清掉。
    /// </summary>
    public void ClearTimeline()
    {
        if (_project == null || _undo == null) return;

        if (_engine.IsPlaying)
        {
            _engine.Stop();
            PlayButton.Content = "▶ 播放";
        }

        _project.ClearSelection();
        _project.Clips.Clear();
        _project.PlayheadSec = 0;
        _undo.Reset();

        Wave.SetProject(_project);
        UpdateButtonStates();
        UpdateScrollBar();
        WriteLog("工作台换了源文件 —— 旧时间轴已清空（旧块仍留在项目档案里）");
    }

    /// <summary>
    /// 编辑器 → 工作台：把当前时间轴读回成保留区间（按时间轴顺序）。
    /// 用户精剪后的块序、边界、增删都反映在这里。
    /// </summary>
    public List<(double Start, double End)> GetTimelineKeeps()
    {
        var res = new List<(double Start, double End)>();
        if (_project == null) return res;

        foreach (var c in _project.Clips.OrderBy(c => c.TimelineStartSec))
        {
            if (c.SourceDurationSec > 0.01)
                res.Add((c.SourceStartSec, c.SourceEndSec));
        }
        return res;
    }

    // ============================================================
    // 存档 / 还原（保存项目、打开项目时用）
    // ============================================================
    //
    // 和上面两个"工作台交接"方法的区别：
    //   GetTimelineKeeps  → 只给下游"保留区间"（最小信息，够分镜/渲染用）
    //   CaptureTimeline   → 给自己的存档，信息全（每块边界/音量/淡入淡出/视口）
    //   只存区间的话，下次打开你手工挪过的边界、调过的音量就全没了。

    /// <summary>把当前时间轴导出成一份完整快照。</summary>
    public TimelineSnapshot CaptureTimeline()
    {
        var snap = new TimelineSnapshot();
        if (_project == null) return snap;

        foreach (var c in _project.Clips.OrderBy(c => c.TimelineStartSec))
            snap.Clips.Add(TimelineSnapshot.Clone(c));

        snap.PlayheadSec = _project.PlayheadSec;

        var (viewStart, secPerPixel, _) = Wave.GetViewport();
        snap.ViewStartSec = viewStart;
        snap.SecPerPixel = secPerPixel;
        return snap;
    }

    /// <summary>
    /// 照着快照还原时间轴（打开项目时用）。整条时间轴被替换，撤销历史清空。
    /// 返回找不到源文件的块数 —— 源文件被移走/改名时不能让编辑器整个哑掉。
    /// </summary>
    public int RestoreTimeline(TimelineSnapshot snap)
    {
        if (_project == null || _undo == null || snap == null) return 0;

        // 播放中重铺 = 播放线程正在读的块列表被清空重建，必须先停
        if (_engine.IsPlaying)
        {
            _engine.Stop();
            PlayButton.Content = "▶ 播放";
        }

        _project.ClearSelection();
        _project.Clips.Clear();
        _undo.Reset();

        int missing = 0;
        foreach (var c in snap.Clips)
        {
            if (c.SourceDurationSec <= 0.001) continue;   // 空块直接丢
            if (string.IsNullOrWhiteSpace(c.SourcePath) || !File.Exists(c.SourcePath))
                missing++;

            _project.Clips.Add(TimelineSnapshot.Clone(c));
        }

        _project.PlayheadSec = Math.Max(0, snap.PlayheadSec);
        _project.NotifyChanged();
        _project.NotifySelectionChanged();

        Wave.SetProject(_project);
        // 用存档里的缩放，而不是 FitToContent —— 那样会把你上次的缩放级别冲掉
        Wave.SetViewport(snap.ViewStartSec, snap.SecPerPixel);

        _undo.CaptureSnapshot();
        UpdateButtonStates();
        UpdateScrollBar();
        WriteLog($"还原时间轴：{_project.Clips.Count} 块，缺源文件 {missing} 个");

        return missing;
    }

    /// <summary>「导入」按钮：选文件 → 复制到素材库 → 左边列表出现</summary>
    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        string? path = _file.OpenFileDialog();
        if (path == null) return;

        try
        {
            string fileName = Path.GetFileName(path);
            string destPath = Path.Combine(_file.LibraryDir, fileName);

            if (File.Exists(destPath))
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                destPath = Path.Combine(_file.LibraryDir,
                    baseName + "_" + Utils.CreateTimestamp() + ext);
            }

            File.Copy(path, destPath, overwrite: false);
            LoadLibraryList();
            UploadHint.Text = "已导入: " + Path.GetFileName(destPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入失败：" + ex.Message, "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>「剪刀」按钮：在播放线位置切开选中块</summary>
    private void OnScissorsClick(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        if (_project.SelectedCount == 0)
        {
            MessageBox.Show("请先选中要切的块", "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _undo?.CaptureSnapshot();  // 记录撤销快照
        WriteLog($"剪刀切分: 位置 {_project.PlayheadSec:F1}s");
        _project.SplitClipAt(_project.PlayheadSec);
        UpdateButtonStates();
    }

    /// <summary>「删除块」按钮：从时间轴删除选中块（不删源文件）</summary>
    private void OnDeleteClipClick(object sender, RoutedEventArgs e)
    {
        if (_project == null || _project.SelectedCount == 0) return;
        _undo?.CaptureSnapshot();  // 记录撤销快照
        WriteLog($"删除块: {_project.SelectedCount} 个");
        _project.RemoveSelectedClips();
        UpdateButtonStates();
    }

    /// <summary>「淡入」按钮：给选中块加 0.5 秒淡入</summary>
    private void OnFadeInClick(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        _undo?.CaptureSnapshot();
        foreach (var clip in _project.Clips)
        {
            if (clip.IsSelected) clip.FadeInSec = 0.5;
        }
        // 这里必须走 NotifyChanged 而不是只刷新波形：
        // "内容变了"要广播出去，音频预热的钩子就挂在它上面。
        // （只刷新波形的话，改了淡入淡出按空格听到的还是没淡化的旧声音）
        _project.NotifyChanged();
    }

    /// <summary>「淡出」按钮：给选中块加 0.5 秒淡出</summary>
    private void OnFadeOutClick(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        _undo?.CaptureSnapshot();
        foreach (var clip in _project.Clips)
        {
            if (clip.IsSelected) clip.FadeOutSec = 0.5;
        }
        _project.NotifyChanged();
    }

    /// <summary>「清除选择」按钮</summary>
    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        _project?.ClearSelection();
        UpdateButtonStates();
    }

    /// <summary>「撤销」按钮</summary>
    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        _undo?.Undo();
        UpdateButtonStates();
    }

    /// <summary>「重做」按钮</summary>
    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        _undo?.Redo();
        UpdateButtonStates();
    }

    // ============================================================
    // 音量调整（作用于选中块）
    // ============================================================

    private void OnVolumeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VolumeText.Text = Math.Round(e.NewValue, 1) + " dB";
    }

    private void OnVolumeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        ApplyVolumeChange();
    }

    private void OnVolumeSliderKeyUp(object sender, KeyEventArgs e)
    {
        ApplyVolumeChange();
    }

    private void ApplyVolumeChange()
    {
        if (_project == null || _project.SelectedCount == 0) return;
        double delta = VolumeSlider.Value - _lastAppliedVolumeDb;
        if (Math.Abs(delta) < 0.01) return;

        // 对所有选中块加音量偏移
        foreach (var clip in _project.Clips)
        {
            if (clip.IsSelected)
            {
                clip.VolumeDb += delta;
            }
        }
        _lastAppliedVolumeDb = VolumeSlider.Value;
        _project.NotifyChanged();   // 同上：音量变了也要广播，否则预热不知道缓存作废了
    }

    // ============================================================
    // 播放控制
    // ============================================================

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_project == null || _project.Clips.Count == 0) return;

        if (_engine.IsPlaying)
        {
            _engine.Pause();
            PlayButton.Content = "▶ 播放";
        }
        else
        {
            _engine.Play(_project);
            PlayButton.Content = "⏸ 暂停";
            Wave.FollowPlayhead();  // 播放开始时立即把红线带回视野（若在屏幕外）
        }
    }

    // ============================================================
    // 导出
    // ============================================================

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_project == null || _project.Clips.Count == 0) return;

        string? path = _file.SaveFileDialog("口播音频_精剪版");
        if (path == null) return;

        try
        {
            _engine.Stop();
            WriteLog($"导出: {path}  块数 {_project.Clips.Count}");
            _engine.ExportM4a(_project, path);
            MessageBox.Show("已导出：" + path, "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "精剪",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============================================================
    // 快捷键
    // ============================================================

    private void HandleShortcutKey(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && e.Key == Key.Z)
        {
            OnUndoClick(this, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y)
        {
            OnRedoClick(this, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A && _project != null)
        {
            _project.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _project != null)
        {
            OnDeleteClipClick(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            OnPlayClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ============================================================
    // 界面状态
    // ============================================================

    private void UpdateButtonStates()
    {
        bool hasClips = _project != null && _project.Clips.Count > 0;
        bool hasSelection = _project != null && _project.SelectedCount > 0;

        PlayButton.IsEnabled = hasClips;
        ExportButton.IsEnabled = hasClips;
        CutButton.IsEnabled = hasSelection;   // 剪刀
        DeleteClipButton.IsEnabled = hasSelection;
        VolumeSlider.IsEnabled = hasSelection;
    }
}
