// ============================================================
// 工具\隔离验证\Program.cs - 「数据目录真的隔开了吗」
// ============================================================
//
// 用法（在工程根 D:\工作台工程\工作台 下）：
//     dotnet run --project .\工具\隔离验证\IsolationProbe.csproj -c Release -- --probe
//     $env:LITECUT_DATA_DIR="$env:TEMP\wb-isolation-write"
//     dotnet run --project .\工具\隔离验证\IsolationProbe.csproj -c Release -- --write
//
// 两个模式的分工：
//   --probe  纯口径 + 本次进程的真实接线，**一个字节都不写**（健康检查的门禁项）
//   --write  必须设了 LITECUT_DATA_DIR；用程序自己的 Store 真写三份文件，
//            证明落点确实在隔离目录、真实 AppData 那份没被动
//
// 为什么断言要打「通过 N 项，失败 M 项」：
//   健康检查.ps1 用这行正则判过没过（和 工具\自检 同一口径），换格式它就瞎了。
//
// 为什么 --write 没设环境变量就拒绝（退出码 2）：
//   不设就等于落在 %APPDATA%\翊瑄的工作台 —— 那会把虚拟数据写进用户真实配置，
//   正是这张卡要堵的洞。宁可拒绝，也不"顺手写一下"。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LiteCut.Core.Paths;
using LiteCut.Workbench;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Review;

namespace LiteCut.IsolationProbe;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failed = new();

    /// <summary>本进程起来的那一刻（--write 用它判"真实文件有没有被本次写过"）。</summary>
    private static readonly DateTime StartUtc = DateTime.UtcNow;

    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string? Env => Environment.GetEnvironmentVariable(AppDataPaths.DataDirEnvVar);

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 个别宿主不让改，忽略 */ }

        string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
        if (mode != "--probe" && mode != "--write")
        {
            Console.WriteLine("用法：IsolationProbe --probe | --write");
            Console.WriteLine("  --probe  只算不写：口径 + 本次进程的实际接线（健康检查门禁）");
            Console.WriteLine("  --write  必须设了 " + AppDataPaths.DataDirEnvVar + "；用程序自己的 Store 真写三份文件");
            return 2;
        }

        Console.WriteLine("工作台隔离验证 · " + AppDataPaths.DataDirEnvVar);
        Console.WriteLine("本次进程：" + (AppDataPaths.IsIsolated(Env) ? "【隔离模式】" : "【未隔离 · 走默认位置】"));
        Console.WriteLine("来源：" + AppDataPaths.DescribeSource(Env));

        if (mode == "--probe")
        {
            Console.WriteLine();
            Probe();
        }
        else
        {
            if (!AppDataPaths.IsIsolated(Env))
            {
                Console.WriteLine();
                Console.WriteLine("[拒绝] 没设 " + AppDataPaths.DataDirEnvVar + "，这个模式会真写文件。");
                Console.WriteLine("       不设就等于落在 " + Path.Combine(Roaming, AppDataPaths.DefaultFolderName) + "（用户真实配置）。");
                Console.WriteLine("       先设一个可写目录再跑，例如：");
                Console.WriteLine("       $env:" + AppDataPaths.DataDirEnvVar + "=\"$env:TEMP\\wb-isolation-write\"");
                return 2;
            }
            Console.WriteLine();
            Write();
        }

        Console.WriteLine();
        Console.WriteLine($"隔离验证结果：通过 {_passed} 项，失败 {Failed.Count} 项");
        foreach (string f in Failed) Console.WriteLine($"  失败：{f}");
        return Failed.Count == 0 ? 0 : 1;
    }

    // ============================================================
    // --probe：口径 + 本次进程的真实接线
    // ============================================================

    private static void Probe()
    {
        // 样例隔离目录：拿 %TEMP% 下一个**不会被真用到**的名字算，不建它
        string sample = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wb-isolation-probe-sample"));

        Console.WriteLine("== 一、口径（纯函数，与本次进程的环境变量无关） ==");

        string legacyData = AppDataPaths.ResolveDataDir(null, Roaming);
        Check("未隔离：配置落点 = %APPDATA%\\翊瑄的工作台（与改造前逐字相同）",
            legacyData == Path.Combine(Roaming, AppDataPaths.DefaultFolderName), legacyData);

        string isoData = AppDataPaths.ResolveDataDir(sample, Roaming);
        Check("已隔离：落点就是指定的那个目录", isoData == sample, isoData);

        Check("互斥名：未隔离 = 历史写死的那个名字，一字不差",
            AppDataPaths.MutexName(null, Roaming) == @"Local\LiteCut_Workbench_SingleInstance_A7F3",
            AppDataPaths.MutexName(null, Roaming));

        Check("互斥名：隔离后与正式版不同名（两边的锁不互相挡）",
            AppDataPaths.MutexName(sample, Roaming) != AppDataPaths.BaseMutexName,
            AppDataPaths.MutexName(sample, Roaming));

        string legacyLogDir = AppDataPaths.ResolveLogDir(null, Roaming, LocalAppData);
        Check("未隔离：crash.log 目录 = %LOCALAPPDATA%\\翊瑄的工作台（与改造前逐字相同）",
            legacyLogDir == Path.Combine(LocalAppData, AppDataPaths.DefaultFolderName), legacyLogDir);

        Check("已隔离：crash.log 跟着隔离目录走",
            AppDataPaths.ResolveLogDir(sample, Roaming, LocalAppData) == sample,
            AppDataPaths.ResolveLogDir(sample, Roaming, LocalAppData));

        Console.WriteLine();
        Console.WriteLine("== 二、本次进程的实际接线（读程序自己在用的值） ==");

        string expectData = AppDataPaths.ResolveDataDir(Env, Roaming);
        string expectLog = Path.Combine(AppDataPaths.ResolveLogDir(Env, Roaming, LocalAppData), "crash.log");
        string expectMutex = AppDataPaths.MutexName(Env, Roaming);

        Check("AppConfig.DataDir 与口径算出来的一致",
            AppConfig.DataDir == expectData, AppConfig.DataDir + " vs " + expectData);

        Check("App.CrashLogPath 与口径算出来的一致",
            App.CrashLogPath == expectLog, App.CrashLogPath + " vs " + expectLog);

        if (!AppDataPaths.IsIsolated(Env))
        {
            // 这一段就是验收里的「不设 LITECUT_DATA_DIR 时与今天完全相同」
            Check("未隔离：程序配置落点逐字等于 %APPDATA%\\翊瑄的工作台",
                AppConfig.DataDir == Path.Combine(Roaming, AppDataPaths.DefaultFolderName),
                AppConfig.DataDir);

            Check("未隔离：程序 crash.log 逐字等于 %LOCALAPPDATA%\\翊瑄的工作台\\crash.log",
                App.CrashLogPath == Path.Combine(LocalAppData, AppDataPaths.DefaultFolderName, "crash.log"),
                App.CrashLogPath);

            Check("未隔离：单实例锁名就是历史那个名字",
                expectMutex == @"Local\LiteCut_Workbench_SingleInstance_A7F3", expectMutex);
        }
        else
        {
            Check("已隔离：程序配置落点就在隔离目录里",
                AppConfig.DataDir == AppDataPaths.ResolveDataDir(Env, Roaming), AppConfig.DataDir);

            Check("已隔离：程序 crash.log 就在隔离目录里",
                App.CrashLogPath == Path.Combine(AppConfig.DataDir, "crash.log"), App.CrashLogPath);

            Check("已隔离：单实例锁名带上了隔离目录的后缀（与正式版分开）",
                expectMutex.StartsWith(AppDataPaths.BaseMutexName + "_", StringComparison.Ordinal)
                && expectMutex != AppDataPaths.BaseMutexName, expectMutex);
        }

        Console.WriteLine();
        Console.WriteLine("落点清单：");
        Console.WriteLine("  配置/账本/灵感池：" + AppConfig.DataDir);
        Console.WriteLine("  崩溃日志        ：" + App.CrashLogPath);
        Console.WriteLine("  单实例锁名      ：" + expectMutex);
    }

    // ============================================================
    // --write：真写三份文件（只许在隔离目录里发生）
    // ============================================================

    private static void Write()
    {
        string dir = AppConfig.DataDir;
        string realDir = Path.Combine(Roaming, AppDataPaths.DefaultFolderName);

        Console.WriteLine("== 落点 ==");
        Console.WriteLine("  隔离目录：" + dir);
        Console.WriteLine("  真实目录：" + realDir + "（本次一个字都不许写）");
        Console.WriteLine();

        // ① 先确认自己没写错地方 —— 这条错了后面全白干
        Check("落点在隔离目录里，不是用户真实目录",
            dir != realDir && Path.IsPathRooted(dir), dir);

        // ② 配置：走 AppConfig.Save（程序自己那条写盘路径）
        Console.WriteLine();
        Console.WriteLine("== 二、config.json（AppConfig.Save） ==");
        AppConfig cfg = AppConfig.Load();
        bool okCfg = cfg.Save();
        Check("AppConfig.Save() 报告写成功", okCfg, cfg.LastSaveError ?? "未知原因");
        string cfgPath = Path.Combine(dir, "config.json");
        Check("config.json 落在隔离目录", File.Exists(cfgPath), cfgPath);

        // ③ 复盘账本：走 AccountReviewStore（落点取 Store 自己那个 FilePath）
        Console.WriteLine();
        Console.WriteLine("== 三、account-review.json（AccountReviewStore.Save） ==");
        var review = new AccountReviewFile();
        review.Records.Add(new AccountRecord
        {
            Platform = "探针虚拟平台",
            StatsDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Fans = 1234,
            TotalPlay = 5678,
            TotalLike = 9012,
            Fans30d = 34,
            Note = "隔离验证写入的虚拟数据，不是真实账号",
        });
        bool okReview = AccountReviewStore.Save(review);
        string reviewPath = AccountReviewStore.FilePath;
        Check("AccountReviewStore.Save() 报告写成功", okReview);
        Check("account-review.json 落在隔离目录（路径取自 Store 自己）",
            File.Exists(reviewPath) && Path.GetDirectoryName(reviewPath) == dir, reviewPath);

        // ④ 灵感池：走 InspirationStore
        Console.WriteLine();
        Console.WriteLine("== 四、inspiration.json（InspirationStore.Save） ==");
        bool okInsp = InspirationStore.Save(new[]
        {
            new InspirationItem
            {
                Title = "探针虚拟灵感（隔离验证）",
                Note = "隔离验证写入的虚拟数据，不是真实灵感",
                Channel = "虚拟",
            },
        });
        string inspPath = Path.Combine(dir, "inspiration.json");
        Check("InspirationStore.Save() 报告写成功", okInsp);
        Check("inspiration.json 落在隔离目录", File.Exists(inspPath), inspPath);

        // ⑤ 崩溃日志：路径取 App.CrashLogPath（程序自己写日志用的那个属性）
        Console.WriteLine();
        Console.WriteLine("== 五、crash.log（App.CrashLogPath） ==");
        string logPath = App.CrashLogPath;
        bool okLog = true;
        string logErr = "";
        try
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 探针：crash.log 跟着隔离目录走（工具\\隔离验证 --write）{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex) { okLog = false; logErr = ex.Message; }
        Check("crash.log 写在隔离目录里（路径取自 App.CrashLogPath，不是另算一份）",
            okLog && File.Exists(logPath) && Path.GetDirectoryName(logPath) == dir,
            logErr.Length > 0 ? logErr : logPath);

        // ⑥ 用户真实目录：本次有没有被写
        Console.WriteLine();
        Console.WriteLine("== 六、用户真实目录：本次一个字都没写 ==");
        foreach (string name in new[] { "config.json", "account-review.json", "inspiration.json" })
        {
            string real = Path.Combine(realDir, name);
            Check($"{name}：真实那份没被本次写过（mtime 早于本次启动）",
                NotWrittenSinceStart(real), DescribeReal(real));
        }
        string realLog = Path.Combine(LocalAppData, AppDataPaths.DefaultFolderName, "crash.log");
        Check("crash.log：真实那份没被本次写过（mtime 早于本次启动）",
            NotWrittenSinceStart(realLog), DescribeReal(realLog));

        // ⑦ 落盘清单
        Console.WriteLine();
        Console.WriteLine("落盘清单（全部在隔离目录里）：");
        foreach (string p in new[] { cfgPath, reviewPath, inspPath, logPath })
        {
            string size = File.Exists(p) ? new FileInfo(p).Length + " 字节" : "（不存在）";
            Console.WriteLine("  " + p + " —— " + size);
        }
    }

    /// <summary>
    /// 文件不存在 = 本次没造出来（算过）；存在且 mtime 早于本次启动 = 本次没写过。
    /// 留 1 秒余量，免得被文件系统时间精度和时钟抖动误判。
    /// </summary>
    private static bool NotWrittenSinceStart(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            return File.GetLastWriteTimeUtc(path) < StartUtc.AddSeconds(-1);
        }
        catch
        {
            return false;   // 读不到就按"有问题"处理，不许乐观
        }
    }

    private static string DescribeReal(string path)
    {
        try
        {
            if (!File.Exists(path)) return "不存在（本次没造出来）";
            return "存在，mtime=" + File.GetLastWriteTimeUtc(path).ToString("o", CultureInfo.InvariantCulture)
                   + "（本次启动 " + StartUtc.ToString("o", CultureInfo.InvariantCulture) + "）";
        }
        catch (Exception ex)
        {
            return "读取失败：" + ex.Message;
        }
    }

    // ============================================================
    // 断言工具（与 工具\自检 同一套口径）
    // ============================================================

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [过] {name}");
            return;
        }

        string line = detail.Length == 0 ? name : $"{name}（实际：{detail}）";
        Failed.Add(line);
        Console.WriteLine($"  [败] {line}");
    }
}
