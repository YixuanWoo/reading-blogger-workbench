// ============================================================
// AppDataPaths.cs - 程序数据目录的唯一口径（外加开发/测试的隔离口）
// ============================================================
//
// 【为什么要有这个文件】
//   工作台里"跟具体项目无关"的那几份数据全挂在同一个用户目录上：
//     %APPDATA%\翊瑄的工作台\config.json          （用户设置）
//     %APPDATA%\翊瑄的工作台\inspiration.json     （灵感池）
//     %APPDATA%\翊瑄的工作台\account-review.json  （复盘账本）
//     %LOCALAPPDATA%\翊瑄的工作台\crash.log       （崩溃日志）
//   于是 bin\Debug / bin\Release 里跑起来的开发版，跟用户天天用的正式实例
//   **共用同一份文件**：跑一次调试就把真实设置改掉了（LastProjectDir /
//   ProjectRoot / 主题 / AI key 都可能被改）；开发版顺手开一个真实项目，
//   ProjectStore.Load 末尾的 EnsureAssets 还会往真实作品里写素材种子。
//   这违反公司硬规矩「测试、回归、截图一律用虚拟数据」。
//
// 【本文件只做一件事】
//   把"配置落在哪 / 日志落在哪 / 单实例锁叫什么名字"这三件事的口径收成
//   一组**纯字符串函数**：
//     · 环境变量 LITECUT_DATA_DIR 没设（或只写了空白）→ 一字不差落回今天的位置；
//     · 设了 → 落进它指定的目录，crash.log 与单实例锁的名字跟着一起走。
//
// 【为什么放在 LiteCut.Core，而且写成纯函数】
//   ① Core 的架构铁律是不许依赖 Windows，所以这里**不碰文件系统、不碰注册表、
//      不调任何 API**，只算字符串：同样的入参永远同样的出参，能直接进自检当断言。
//   ② 口径只有一处，App / 工具\自检 / 工具\隔离验证 三方算出来的落点不可能分叉。
//
// 【为什么不在 App 里读一次环境变量就完事】
//   落点被三处用到（配置目录、崩溃日志、单实例锁），分散写三份
//   Environment.GetEnvironmentVariable 一定会漏改；而且自检是控制台程序、
//   不引 WPF，验不到 App 里的私有字段。
//
// 【没设变量时"一个字节都不变"怎么保证】
//   未隔离分支返回 Path.Combine(appDataRoot, DefaultFolderName) ——
//   和改造前 AppConfig.Dir 的写法逐字等价（见 工具\自检 的同名断言）；
//   互斥名走 BaseMutexName，也是改造前写死在 App.xaml.cs 里那个名字；
//   日志目录走 Path.Combine(localAppDataRoot, DefaultFolderName)。
// ============================================================

using System;
using System.IO;

namespace LiteCut.Core.Paths;

/// <summary>
/// 程序数据目录 / 崩溃日志 / 单实例锁名字的口径（纯函数，可跨平台编译）。
/// </summary>
public static class AppDataPaths
{
    /// <summary>
    /// 隔离口的环境变量名。设了它（且不是空白）就把整个数据目录搬过去。
    /// 取这个名字的理由：项目里已有的运行开关都叫 LITECUT_*（如 LITECUT_ALLOW_MULTI_INSTANCE），
    /// 沿用同一个前缀，桌面上一眼就能认出来是谁家的变量。
    /// </summary>
    public const string DataDirEnvVar = "LITECUT_DATA_DIR";

    /// <summary>默认的文件夹名（%APPDATA% 与 %LOCALAPPDATA% 下都叫这个）。</summary>
    public const string DefaultFolderName = "翊瑄的工作台";

    /// <summary>
    /// 单实例锁的基数名字（未隔离时用的就是它）。
    /// ★ 前缀 Local\ = 只对当前登录会话生效（同一台电脑换用户登录互不影响）。
    /// ★ 这个字符串是历史写死在 App.xaml.cs 里的，**不许改**：
    ///   改了等于把"正在跑的旧版"和"新编出来的版本"当成两个程序，
    ///   两者可以同时开 —— 那正是单实例锁要挡的事故。
    /// </summary>
    public const string BaseMutexName = @"Local\LiteCut_Workbench_SingleInstance_A7F3";

    /// <summary>
    /// 这个环境变量的值算不算"要隔离"。
    /// 规则很朴素：**空白（null / "" / "   "）一律当作没设**。
    /// 为什么连空格也要吞掉：有人会写 `set LITECUT_DATA_DIR=` 或带个尾随空格，
    /// 这种值拿去拼路径会得到一个叫 " " 的目录 —— 静悄悄地跟没设一样，
    /// 却让人误以为已经隔离了（"以为隔离了"比"知道没隔离"危险得多）。
    /// </summary>
    public static bool IsIsolated(string? envValue) => !string.IsNullOrWhiteSpace(envValue);

    /// <summary>
    /// 配置数据目录（config.json / inspiration.json / account-review.json 的家）。
    /// </summary>
    /// <param name="envValue">LITECUT_DATA_DIR 的值（原样传进来，判空由本函数做）</param>
    /// <param name="appDataRoot">%APPDATA% 的根（正常情况下传
    ///   Environment.GetFolderPath(SpecialFolder.ApplicationData)）</param>
    /// <remarks>
    /// 未隔离 → <c>appDataRoot\翊瑄的工作台</c>（与改造前逐字一致）。
    /// 已隔离 → 规范化后的绝对路径；相对路径按 <paramref name="appDataRoot"/> 展开
    /// （★ 故意不按"当前工作目录"展开：同一个环境变量从 IDE 启动、从 PowerShell 启动、
    /// 从 bin\Debug 双击启动会落到三个不同地方，隔离就不可复现了）。
    /// 入参含非法路径字符时按 .NET 的规矩抛 ArgumentException —— 由调用方决定怎么报错。
    /// </remarks>
    public static string ResolveDataDir(string? envValue, string appDataRoot)
    {
        if (!IsIsolated(envValue)) return Path.Combine(appDataRoot, DefaultFolderName);
        return NormalizeAbsolute(envValue!, appDataRoot);
    }

    /// <summary>
    /// 崩溃日志目录（crash.log 的家）。
    /// </summary>
    /// <param name="envValue">LITECUT_DATA_DIR 的值</param>
    /// <param name="appDataRoot">%APPDATA% 的根 —— 隔离时"跟着配置目录走"用同一个基准，
    ///   这样相对路径的隔离值不会出现"配置在一个盘、日志在另一个盘"的岔子</param>
    /// <param name="localAppDataRoot">%LOCALAPPDATA% 的根（未隔离时的老位置）</param>
    /// <remarks>
    /// 未隔离 → <c>localAppDataRoot\翊瑄的工作台</c>：改造前 App.xaml.cs 的 LogPath
    /// 就是 SpecialFolder.LocalApplicationData + 翊瑄的工作台 + crash.log，一字未变。
    /// 已隔离 → 直接并进隔离目录（开发版的崩溃日志不该混进用户那份排障日志里）。
    /// </remarks>
    public static string ResolveLogDir(string? envValue, string appDataRoot, string localAppDataRoot)
    {
        if (!IsIsolated(envValue)) return Path.Combine(localAppDataRoot, DefaultFolderName);
        return ResolveDataDir(envValue, appDataRoot);
    }

    /// <summary>
    /// 单实例锁的名字。
    /// </summary>
    /// <param name="envValue">LITECUT_DATA_DIR 的值</param>
    /// <param name="appDataRoot">%APPDATA% 的根（算隔离目录用）</param>
    /// <remarks>
    /// 未隔离 → <see cref="BaseMutexName"/>，与改造前写死的那个名字**一字不差**
    ///   （行号证据见交付报告：改造前 App.xaml.cs 第 237 行、改造后仍是同一个常量）。
    /// 已隔离 → 基数名字加一段隔离目录的短哈希。为什么必须分名字：
    ///   开发版与正式版在同一个登录会话里，如果共用一把锁，先开正式版就再也打不开
    ///   开发版（"点了一下没反应"），反之亦然；分名字之后两边互不相干。
    /// 哈希只看路径（大小写不敏感，反正 Windows 路径本身不区分），同一目录永远同一个后缀。
    /// </remarks>
    public static string MutexName(string? envValue, string appDataRoot)
    {
        if (!IsIsolated(envValue)) return BaseMutexName;
        return BaseMutexName + "_" + ShortHash(ResolveDataDir(envValue, appDataRoot));
    }

    /// <summary>
    /// 一句话说明"这次的数据到底从哪来的"，给排障的人看（探针输出 / 报告）。
    /// 不返回路径的原因：路径由调用方自己拼，这里只交代来源，免得两处拼法又分叉。
    /// </summary>
    public static string DescribeSource(string? envValue)
    {
        if (!IsIsolated(envValue)) return "默认位置（%APPDATA%\\" + DefaultFolderName + "）";
        return "环境变量 " + DataDirEnvVar + "（" + envValue!.Trim() + "）";
    }

    // ========================================================
    // 内部：规范化与短哈希（都是纯字符串运算）
    // ========================================================

    /// <summary>
    /// 把用户写的路径规整成一个"可直接拿去比对"的绝对路径：
    /// 用 baseDir 展开相对段、统一成系统分隔符、去掉结尾多余的分隔符。
    /// </summary>
    private static string NormalizeAbsolute(string value, string baseDir)
    {
        string full = Path.GetFullPath(value.Trim(), baseDir);

        // 盘符根（D:\ 或 \）本身不能再砍：砍完会变成 "D:"，那是"当前盘的工作目录"的意思
        string root = Path.GetPathRoot(full) ?? "";
        if (!string.Equals(full, root, StringComparison.Ordinal))
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return full;
    }

    /// <summary>
    /// FNV-1a 32 位，取 8 位十六进制。
    /// 只用来给互斥名加后缀：不需要密码学强度，只要"同输入永远同输出、不同输入尽量不同"。
    /// （不用 System.Security.Cryptography 是刻意的 —— Core 这一层保持零依赖。）
    /// </summary>
    private static string ShortHash(string text)
    {
        uint h = 2166136261u;
        string upper = text.ToUpperInvariant();   // Windows 路径不区分大小写
        foreach (char ch in upper)
        {
            h ^= ch;
            h *= 16777619u;
        }
        return h.ToString("X8");
    }
}
