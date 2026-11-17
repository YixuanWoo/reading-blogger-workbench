// ============================================================
// 工具\根目录守卫验证\Program.cs - 「改项目根目录」的无头断言
// ============================================================
//
// 用法（在工程根 D:\工作台工程\工作台 下）：
//     $env:LITECUT_DATA_DIR = "$env:TEMP\wb-rootguard"
//     dotnet run --project .\工具\根目录守卫验证\RootGuardVerify.csproj -c Release
//
// 断言什么（对照派卡验收）：
//   ① 等价路径不误弹框：尾部斜杠 / 大小写 / \ 与 / / 重复分隔符 / 留空 vs 显式填兜底
//   ② 真换地方要弹，文案含「没删」「改回来」且新旧路径原样列出
//   ③ 点取消 → 什么都不变：config.json 的 SHA256 与 mtime 前后一致，内存里的 ProjectRoot 未改，
//      窗口不关（Saved 仍为 false），也不碰项目列表
//   ④ 点确定 → 真的写盘
//
// 为什么断言要打「通过 N 项，失败 M 项」：
//   健康检查.ps1 用这行正则判过没过（和 工具\自检 / 工具\隔离验证 同一口径），换格式它就瞎了。
//
// 为什么必须设 LITECUT_DATA_DIR（没设就退出码 2）：
//   本工具会真的调 AppConfig.Save。不设就写进用户真实的 %APPDATA%\翊瑄的工作台 —— 污染真实配置。
//
// 为什么不 Show 窗口：
//   断言的是"保存入口"的逻辑，不是"框长什么样"。确认框本身用注入的假回调代替，
//   所以能无头跑；真弹框那部分由人工在界面上看一眼（报告里有读数）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteCut.Workbench;
using LiteCut.Workbench.Project;
using LiteCut.Workbench.Settings;

namespace LiteCut.RootGuardVerify;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failed = new();

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  [过] " + name);
        }
        else
        {
            Failed.Add(name + (detail == null ? "" : "（" + detail + "）"));
            Console.WriteLine("  [败] " + name + (detail == null ? "" : "（" + detail + "）"));
        }
    }

    [STAThread]
    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 个别宿主不让改，忽略 */ }

        string? env = Environment.GetEnvironmentVariable("LITECUT_DATA_DIR");
        if (string.IsNullOrWhiteSpace(env))
        {
            Console.WriteLine("拒绝运行：没有设 LITECUT_DATA_DIR。");
            Console.WriteLine("这个工具会真的写 config.json —— 不设就等于写进用户真实的 %APPDATA%\\翊瑄的工作台。");
            Console.WriteLine("正确用法：");
            Console.WriteLine("  $env:LITECUT_DATA_DIR = \"$env:TEMP\\wb-rootguard\"");
            Console.WriteLine("  dotnet run --project .\\工具\\根目录守卫验证\\RootGuardVerify.csproj -c Release");
            return 2;
        }

        Console.WriteLine("根目录守卫验证 · 改「项目根目录」的提示与取消语义");
        Console.WriteLine("隔离数据目录：" + AppConfig.DataDir);
        Console.WriteLine();

        // 安全闸：确认真的落在隔离目录里（不是用户真实配置）
        string realAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "翊瑄的工作台");
        Check("数据目录已隔离（不在 %APPDATA%\\翊瑄的工作台）",
              !string.Equals(AppConfig.DataDir, realAppData, StringComparison.OrdinalIgnoreCase),
              AppConfig.DataDir);

        SectionEffective();
        SectionIsChange();
        SectionPrompt();
        SectionBehaviour();

        Console.WriteLine();
        Console.WriteLine($"根目录守卫验证结果：通过 {_passed} 项，失败 {Failed.Count} 项");
        if (Failed.Count > 0)
        {
            foreach (string f in Failed) Console.WriteLine("    · " + f);
        }
        return Failed.Count == 0 ? 0 : 1;
    }

    // ========================================================
    // ① Effective：空白 = 跟随兜底
    // ========================================================

    private static void SectionEffective()
    {
        Console.WriteLine("== ① 有效根目录（空白 = 跟随 exe 旁的「项目」） ==");

        string fallback = @"D:\工作台\项目";

        Check("配置空白 → 兜底值", RootChangeGuard.Effective("", fallback) == fallback);
        Check("配置只有空格 → 兜底值", RootChangeGuard.Effective("   ", fallback) == fallback);
        Check("配置为 null → 兜底值", RootChangeGuard.Effective(null, fallback) == fallback);
        Check("配置有值 → 原样（只去首尾空格）",
              RootChangeGuard.Effective("  D:\\我的项目  ", fallback) == @"D:\我的项目");

        // 兜底值带尾斜杠时，Effective 不能把它变成"另一个值"
        Check("兜底值带尾斜杠也照用",
              RootChangeGuard.Effective("", fallback + "\\") == fallback + "\\");
    }

    // ========================================================
    // ② IsChange：等价写法不误判
    // ========================================================

    private static void SectionIsChange()
    {
        Console.WriteLine();
        Console.WriteLine("== ② 算不算换地方（等价的写法不该弹框） ==");

        string fallback = @"D:\工作台\项目";

        // ---- 不该判成改变 ----
        Check("完全相同 → 没变",
              !RootChangeGuard.IsChange(@"D:\工作台\项目", @"D:\工作台\项目", fallback));
        Check("尾部斜杠差异 → 没变",
              !RootChangeGuard.IsChange(@"D:\工作台\项目", @"D:\工作台\项目\", fallback));
        Check("大小写差异 → 没变",
              !RootChangeGuard.IsChange(@"D:\Workbench\项目", @"d:\workbench\项目", fallback));
        Check("反斜杠与正斜杠差异 → 没变",
              !RootChangeGuard.IsChange(@"D:\工作台\项目", "D:/工作台/项目", fallback));
        Check("重复分隔符 → 没变",
              !RootChangeGuard.IsChange(@"D:\工作台\项目", @"D:\工作台\\项目", fallback));
        Check("末尾多一个正斜杠 → 没变",
              !RootChangeGuard.IsChange(@"D:\工作台\项目", "D:/工作台/项目/", fallback));
        Check("旧留空、新显式填兜底路径 → 没变",
              !RootChangeGuard.IsChange("", fallback, fallback));
        Check("旧显式填兜底路径、新留空 → 没变",
              !RootChangeGuard.IsChange(fallback, "", fallback));
        Check("盘根 D:\\ 与 D:/ → 没变",
              !RootChangeGuard.IsChange(@"D:\", "D:/", fallback));
        Check("两边都留空（都跟兜底）→ 没变",
              !RootChangeGuard.IsChange("", "   ", fallback));

        // ---- 该判成改变 ----
        Check("换成另一个目录 → 是改变",
              RootChangeGuard.IsChange(@"D:\工作台\项目", @"E:\创作\项目", fallback));
        Check("父目录 vs 子目录 → 是改变",
              RootChangeGuard.IsChange(@"D:\工作台", @"D:\工作台\项目", fallback));
        Check("留空 vs 另一个真实目录 → 是改变",
              RootChangeGuard.IsChange("", @"E:\创作\项目", fallback));
        Check("相对路径 vs 绝对路径 → 是改变（宁严勿宽）",
              RootChangeGuard.IsChange("项目", @"D:\工作台\项目", fallback));
        Check("只差一个字符 → 是改变",
              RootChangeGuard.IsChange(@"D:\工作台\项目A", @"D:\工作台\项目B", fallback));
    }

    // ========================================================
    // ③ 文案：没删 / 改回来 / 新旧路径原样
    // ========================================================

    private static void SectionPrompt()
    {
        Console.WriteLine();
        Console.WriteLine("== ③ 确认文案（必须含「没删」「改回来」） ==");

        string oldPath = @"D:\工作台\项目";
        string newPath = @"E:\1_创作素材\项目";
        string prompt = RootChangeGuard.Prompt(oldPath, newPath);

        Check("含「没删」", prompt.Contains("没删", StringComparison.Ordinal));
        Check("含「改回来」", prompt.Contains("改回来", StringComparison.Ordinal));
        Check("旧路径原样列出（且不止一次）",
              CountOf(prompt, oldPath) >= 2, "出现 " + CountOf(prompt, oldPath) + " 次");
        Check("新路径原样列出",
              CountOf(prompt, newPath) >= 1, "出现 " + CountOf(prompt, newPath) + " 次");
        Check("标题固定为「改项目根目录」", RootChangeGuard.DialogTitle == "改项目根目录");
        Check("文案不是空话（含「确定」「取消」两条路）",
              prompt.Contains("确定", StringComparison.Ordinal) &&
              prompt.Contains("取消", StringComparison.Ordinal));

        Console.WriteLine();
        Console.WriteLine("---- 确认框正文（界面读数：下面这段就是弹框里显示的字）----");
        foreach (string line in prompt.Split('\n')) Console.WriteLine("  | " + line);
        Console.WriteLine("---- 正文结束 ----");
    }

    // ========================================================
    // ④ 行为：取消什么都不变；确定真的写
    // ========================================================

    private static void SectionBehaviour()
    {
        Console.WriteLine();
        Console.WriteLine("== ④ 点「取消」什么都不变 / 点「确定」真的写 ==");

        // 界面资源要加载起来才建得了窗口（和 工具\写作台验证 同一套做法）
        var app = new App();
        app.InitializeComponent();

        string dataDir = AppConfig.DataDir;
        Directory.CreateDirectory(dataDir);
        string configPath = Path.Combine(dataDir, "config.json");

        // ---- 铺一份已知配置（落在隔离目录，不碰真实的） ----
        var cfg = AppConfig.Load();
        cfg.ProjectRoot = @"D:\工作台\项目";
        cfg.DebugPort = 18766;
        cfg.Save();

        string beforeHash = Sha256(configPath);
        long beforeMtime = File.GetLastWriteTimeUtc(configPath).Ticks;

        // ---- 建窗口（不 Show）：界面上填"另一个位置" ----
        var ui = new SettingsWindow(cfg);
        ui.ProjectRootText = @"E:\1_创作素材\项目";

        // ---- ④-1 用户点取消 ----
        int asked = 0;
        string askedText = "";
        bool result = SettingsWindow.SaveWithConfirm(cfg, ui, 18766, text =>
        {
            asked++;
            askedText = text;
            return false;          // ← 用户点了取消
        });

        Check("取消时 SaveWithConfirm 返回 false", !result);
        Check("取消时确实问了一次用户", asked == 1, "问了 " + asked + " 次");
        Check("弹框正文用的是 RootChangeGuard.Prompt",
              askedText == RootChangeGuard.Prompt(@"D:\工作台\项目", @"E:\1_创作素材\项目"));
        Check("取消后内存里的根目录未改", cfg.ProjectRoot == @"D:\工作台\项目", cfg.ProjectRoot ?? "(null)");
        Check("取消后 Saved 仍为 false（窗口不关）", !ui.Saved);
        Check("取消后配置文件哈希不变", Sha256(configPath) == beforeHash);
        Check("取消后配置文件 mtime 不变",
              File.GetLastWriteTimeUtc(configPath).Ticks == beforeMtime);

        // 顺带：磁盘上读回来的也还是老值（不是"内存没改、盘上改了"）
        var reread = AppConfig.Load();
        Check("取消后磁盘上的 ProjectRoot 未变", reread.ProjectRoot == @"D:\工作台\项目", reread.ProjectRoot ?? "(null)");

        // ---- ④-2 等价路径：不该问 ----
        asked = 0;
        ui.ProjectRootText = "d:/工作台/项目/";     // 同一个地方，只是写法不同
        bool same = SettingsWindow.SaveWithConfirm(cfg, ui, 18766, _ => { asked++; return false; });
        Check("等价路径不弹框（没问过用户）", asked == 0, "问了 " + asked + " 次");
        Check("等价路径照常保存成功", same);

        // ---- ④-3 留空 vs 显式填兜底：也不该问 ----
        cfg.ProjectRoot = "";
        ui = new SettingsWindow(cfg);
        ui.ProjectRootText = ProjectStore.FallbackRoot();
        asked = 0;
        bool blank = SettingsWindow.SaveWithConfirm(cfg, ui, 18766, _ => { asked++; return false; });
        Check("留空 vs 显式填兜底路径不弹框", asked == 0, "问了 " + asked + " 次");
        Check("留空 vs 显式填兜底路径照常保存", blank);

        // ---- ④-4 真换地方 + 用户点确定：必须真写 ----
        cfg = AppConfig.Load();                 // 重新读一份，避免上一轮留空影响
        cfg.ProjectRoot = @"D:\工作台\项目";
        cfg.Save();
        string beforeHash2 = Sha256(configPath);

        ui = new SettingsWindow(cfg);
        ui.ProjectRootText = @"E:\1_创作素材\项目";
        asked = 0;
        bool confirmed = SettingsWindow.SaveWithConfirm(cfg, ui, 18766, _ => { asked++; return true; });

        Check("真换地方会问用户", asked == 1, "问了 " + asked + " 次");
        Check("点确定后返回成功", confirmed);
        Check("点确定后内存里的根目录已更新", cfg.ProjectRoot == @"E:\1_创作素材\项目", cfg.ProjectRoot ?? "(null)");

        var after = AppConfig.Load();
        Check("点确定后磁盘上的根目录已更新", after.ProjectRoot == @"E:\1_创作素材\项目", after.ProjectRoot ?? "(null)");
        Check("点确定后配置文件确实变了（哈希）", Sha256(configPath) != beforeHash2);

        // ---- 收尾：把这份临时配置恢复成"没改过"的样子，别影响同目录的后续验证 ----
        cfg.ProjectRoot = @"D:\工作台\项目";
        cfg.Save();
    }

    // ========================================================
    // 小工具
    // ========================================================

    private static string Sha256(string path)
    {
        if (!File.Exists(path)) return "(文件不存在)";
        using var fs = File.OpenRead(path);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash);
    }

    private static int CountOf(string text, string token)
    {
        if (token.Length == 0) return 0;
        int count = 0, i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += token.Length;
        }
        return count;
    }
}
