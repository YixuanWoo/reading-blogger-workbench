// ============================================================
// Logger.cs - 简单日志系统
// ============================================================

using System;
using System.IO;
using System.Reflection;

namespace LiteCut;

/// <summary>
/// 简单日志系统：写日志到 exe 同目录的 logs 文件夹。
/// </summary>
public static class Logger
{
    private static readonly string LogFile;

    static Logger()
    {
        try
        {
            // 用 exe 真实路径（self-contained 单文件模式下 BaseDirectory 是临时目录）
            string exePath = Environment.ProcessPath!;
            string exeDir = Path.GetDirectoryName(exePath)!;
            string logDir = Path.Combine(exeDir, "logs");
            Directory.CreateDirectory(logDir);
            LogFile = Path.Combine(logDir, $"litecut_{DateTime.Now:yyyyMMdd}.log");

            File.AppendAllText(LogFile,
                $"\n========== 程序启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n");
        }
        catch
        {
            LogFile = "";
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null)
    {
        string full = message;
        if (ex != null) full += $"\n  {ex.Message}\n  {ex.StackTrace}";
        Write("ERROR", full);
    }

    private static void Write(string level, string message)
    {
        try
        {
            if (string.IsNullOrEmpty(LogFile)) return;
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}\n");
        }
        catch { }
    }
}