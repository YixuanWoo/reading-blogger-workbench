// ============================================================
// DebugLog.cs - 调试模式的总开关 + 事件日志（内存环形 + JSONL 落盘）
// ============================================================
//
// 【它是干什么的】
//   打开调试模式之后，工作台里发生的每一件"值得知道的事"都往这里写一条：
//     界面层 —— 谁点了哪个按钮、谁改了哪个输入框、选了下拉第几项、跳了哪一页
//     文件层 —— 哪个文件被创建 / 修改 / 删除 / 改名（见 DebugFileWatch）
//     业务层 —— 代码里主动调 DebugLog.Event("...") 打的点
//
// 【为什么要落成 JSONL】
//   一行一条 JSON。外部程序（Agent）按行读、逐条解析，不用自己写一个 JSON 解析器
//   去猜"日志从哪断的"。而且追加写，写一半崩了前面的记录也还在。
//
// 【为什么内存里还要留一份环形缓冲】
//   不改文件的场景（比如只想看看刚才点了什么）不必去读日志文件；
//   遥控口 GET /log 直接从这里取增量，比读文件快也更稳（不会读到半行）。
//
// 【和 Logger 的关系】
//   是两套东西，别混：
//     Logger    —— 常驻，给"出问题了事后翻"用（启动打点、异常）
//     DebugLog  —— 只在调试模式开，给"现在就要看"用（含结构化数据）
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteCut.Workbench.Diagnostics;

/// <summary>一条调试记录。</summary>
public sealed class DebugRecord
{
    public long Seq { get; set; }
    public string Time { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Data { get; set; }
}

/// <summary>
/// 调试日志。整个程序共用一份（静态）。
/// </summary>
public static class DebugLog
{
    /// <summary>内存里最多留多少条（超了丢最老的）。</summary>
    public const int RingSize = 8000;

    private static readonly object Gate = new();
    private static readonly List<DebugRecord> Ring = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static long _seq;
    private static bool _enabled;
    private static bool _toFile;
    private static string _file = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>调试模式开着没有。</summary>
    public static bool Enabled { get { lock (Gate) return _enabled; } }

    /// <summary>日志文件路径（没开落盘时为空串）。</summary>
    public static string FilePath { get { lock (Gate) return _file; } }

    /// <summary>当前最大序号 —— 遥控端拿它当游标，下次带 ?since=它 就能只取新增。</summary>
    public static long Seq { get { lock (Gate) return _seq; } }

    /// <summary>打开调试日志。</summary>
    /// <param name="toFile">要不要同时写文件</param>
    public static void Enable(bool toFile)
    {
        lock (Gate)
        {
            _enabled = true;
            _toFile = toFile;
            if (toFile && _file.Length == 0) _file = ResolveLogPath();
        }

        Write("event", "调试模式已打开", new
        {
            file = FilePath,
            pid = Environment.ProcessId,
        });
    }

    /// <summary>关掉调试日志（内存里的历史保留，方便关掉后还能翻）。</summary>
    public static void Disable()
    {
        Write("event", "调试模式已关闭");
        lock (Gate) _enabled = false;
    }

    /// <summary>
    /// 写一条记录。
    /// ★ 任何地方都可以调；没开调试模式时它什么都不做（所以可以放心埋点，零成本）。
    /// </summary>
    public static void Write(string kind, string message, object? data = null)
    {
        DebugRecord rec;
        bool toFile;
        string file;

        lock (Gate)
        {
            if (!_enabled) return;

            rec = new DebugRecord
            {
                Seq = ++_seq,
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                Kind = kind,
                Message = message,
                Data = data == null ? null : SafeJson(data),
            };

            Ring.Add(rec);
            if (Ring.Count > RingSize) Ring.RemoveRange(0, Ring.Count - RingSize);

            toFile = _toFile;
            file = _file;
        }

        if (toFile && file.Length > 0) AppendFile(file, rec);
    }

    /// <summary>业务代码的埋点入口（语义比 Write 更明确，读日志时一眼分得出）。</summary>
    public static void Event(string message, object? data = null) => Write("event", message, data);

    /// <summary>出错了写一条（调试模式下才记）。</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("error", message, ex == null ? null : new { type = ex.GetType().Name, ex.Message, ex.StackTrace });

    /// <summary>取一批记录。<paramref name="since"/> &gt; 0 = 只要比它新的；<paramref name="tail"/> &gt; 0 = 最多留最后几条。</summary>
    public static List<DebugRecord> Snapshot(long since, int tail)
    {
        lock (Gate)
        {
            List<DebugRecord> list = since > 0 ? Ring.FindAll(r => r.Seq > since) : new List<DebugRecord>(Ring);
            if (tail > 0 && list.Count > tail) list = list.GetRange(list.Count - tail, tail);
            return list;
        }
    }

    /// <summary>清空内存里的记录（文件不动 —— 那是"留痕"，要清自己去删文件）。</summary>
    public static void Clear()
    {
        lock (Gate) Ring.Clear();
        Write("event", "调试日志（内存部分）已清空");
    }

    // ========================================================
    // 内部
    // ========================================================

    private static string ResolveLogPath()
    {
        try
        {
            string exePath = Environment.ProcessPath!;
            string dir = Path.Combine(Path.GetDirectoryName(exePath)!, "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"debug_{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            return "";
        }
    }

    private static string SafeJson(object data)
    {
        try
        {
            return JsonSerializer.Serialize(data, JsonOpts);
        }
        catch
        {
            return "";
        }
    }

    private static void AppendFile(string file, DebugRecord rec)
    {
        try
        {
            string line = JsonSerializer.Serialize(rec, JsonOpts);
            File.AppendAllText(file, line + "\n", Utf8NoBom);
        }
        catch
        {
            // 写不进去（被占用 / 磁盘满）就算了 —— 调试日志绝不能反过来拖垮主程序
        }
    }
}
