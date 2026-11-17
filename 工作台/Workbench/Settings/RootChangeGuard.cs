// ============================================================
// RootChangeGuard.cs - 「改项目根目录」这件事的唯一判据与唯一文案
// ============================================================
//
// 【为什么要有这个文件（而不是在两处各写一遍 if）】
//   用户改「项目根目录」有两个入口：
//     ① 设置页 → 路径页签 → 项目根目录 → 保存
//     ② 项目页 → 「更改项目位置」→ 选文件夹
//   两个入口必须给**同一句话、同一套判断**。本项目吃过这个亏：同一件事两处实现，
//   改一处漏一处（登记册 R-16 / R-19 都是这个病）。所以判据（IsChange）与文案（Prompt）
//   只在这里写一份，两个入口都调它。
//
// 【为什么这个文件不引用 WPF】
//   它们全是纯函数（输入 → 输出），没有窗口、没有控件、没有静态状态 ——
//   这样就能在无头环境里直接断言（见 工具\根目录守卫验证），
//   而不是"为了测一句话得先弹一个窗口"。
//
// 【「留空 = 跟 exe 旁的「项目」」这条语义的真源在哪】
//   在 ProjectStore.FallbackRoot()（public，唯一的兜底实现）。
//   本文件**不复制那份字面量**：兜底值一律由调用方算好，当参数传进来。
//   哪天兜底位置要改，只改 ProjectStore 一处。
// ============================================================

using System;
using System.Text;

namespace LiteCut.Workbench.Settings;

/// <summary>
/// 改「项目根目录」的判据与文案（纯函数，无 WPF 依赖，可无头断言）。
/// </summary>
public static class RootChangeGuard
{
    /// <summary>确认框的标题。两个入口共用，别各写一句。</summary>
    public const string DialogTitle = "改项目根目录";

    /// <summary>
    /// 有效根目录：配置值是空白 → 用兜底值（exe 旁的「项目」）。
    ///
    /// 【为什么"空白"要单独算】
    ///   配置里留空不是"没有根目录"，而是"跟随软件自己"。
    ///   拿空白去跟一个真实路径比，会得出"换了地方"这种假结论 —— 用户什么也没改却被弹框。
    /// </summary>
    /// <param name="configuredRoot">配置里的值（可能是空白，可能带首尾空格）</param>
    /// <param name="fallbackRoot">兜底值。<see cref="ProjectStore.FallbackRoot"/> 是它的唯一真源。</param>
    public static string Effective(string? configuredRoot, string fallbackRoot)
    {
        string fallback = fallbackRoot ?? "";
        if (string.IsNullOrWhiteSpace(configuredRoot)) return fallback.Trim();
        return configuredRoot!.Trim();
    }

    /// <summary>
    /// 算不算"真的换了地方"：按规范化后的**有效**路径比较。
    ///
    /// 下列差异一律**不算**改变（否则用户每次点保存都被吓一跳）：
    ///   · 尾部斜杠：<c>D:\工作台\项目\</c> ＝ <c>D:\工作台\项目</c>
    ///   · 大小写：<c>D:\Workbench\项目</c> ＝ <c>d:\workbench\项目</c>（Windows 本来不分大小写）
    ///   · 分隔符写法：<c>D:/工作台/项目</c> ＝ <c>D:\工作台\项目</c>
    ///   · 重复分隔符：<c>D:\工作台\\项目</c> ＝ <c>D:\工作台\项目</c>
    ///   · 留空 vs 显式填兜底路径：两边都按 <see cref="Effective"/> 折算后再比
    /// </summary>
    public static bool IsChange(string? oldConfigured, string? newConfigured, string fallbackRoot)
    {
        string oldPath = Normalize(Effective(oldConfigured, fallbackRoot));
        string newPath = Normalize(Effective(newConfigured, fallbackRoot));
        return !string.Equals(oldPath, newPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// 比较用的规范化形式（只用于"是不是同一个地方"这个判断，不用于显示）。
    ///
    /// 【为什么不用 Path.GetFullPath】
    ///   它会抛异常（路径里有非法字符就炸），而且会把相对路径按当前工作目录展开 ——
    ///   用户随手输入的相对路径不该让一次保存崩掉。这里只做"写法差异"的归一，
    ///   不做"语义解析"：相对路径 vs 绝对路径确实会被判成不同（真实行为也不同，宁严勿宽）。
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        string s = path.Trim().Replace('/', '\\');

        var sb = new StringBuilder(s.Length);
        int i = 0;

        // UNC（\\服务器\共享）开头的两个反斜杠要保住，不能当成"重复分隔符"折叠掉
        if (s.Length >= 2 && s[0] == '\\' && s[1] == '\\')
        {
            sb.Append(@"\\");
            i = 2;
        }

        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && sb.Length > 0 && sb[^1] == '\\') continue;   // 重复分隔符折叠
            sb.Append(c);
        }

        // 尾部斜杠去掉（但 "D:\" 这种"整个盘就是根"的三字符形态要留着：
        // 循环条件 length > 3 正好让它留在 3 个字符）
        while (sb.Length > 3 && sb[^1] == '\\') sb.Length--;

        return sb.ToString().ToUpperInvariant();
    }

    /// <summary>
    /// 确认框的正文（就是用户看到的那段话）。
    ///
    /// 【这段话为什么写得这么长】
    ///   用户技术水平不高，他真正的恐惧不是"路径变了"，而是"我的项目是不是全没了"。
    ///   一句措辞含糊的"确定要改吗"拦不住他去回收站乱翻、重装软件、重新立项。
    ///   所以必须写清三件事，缺一不可：
    ///     ① 原来的项目**一个都没删**，还在老地方；
    ///     ② 列表看着空了是正常的（软件现在只看新位置）；
    ///     ③ **改回来**就能全部看到 —— 不用重新建档、不用重新导入。
    ///   新旧两个路径都要原样列出来（用户得能一眼对上自己刚填的是哪个）。
    /// </summary>
    public static string Prompt(string oldEffective, string newEffective)
    {
        string oldPath = (oldEffective ?? "").Trim();
        string newPath = (newEffective ?? "").Trim();

        return
            "要把「项目根目录」改成另一个地方：\n\n" +
            "    原来：" + oldPath + "\n" +
            "    改成：" + newPath + "\n\n" +
            "★ 原来的项目一个都没删 —— 它们还好端端地待在\n" +
            "    " + oldPath + "\n" +
            "  里面，文件一个都没少。只是这个软件以后不再去那里找项目了。\n\n" +
            "★ 所以点确定之后，项目列表会变成新位置里的东西。\n" +
            "  看着像「空了」是正常的，不是项目丢了。\n\n" +
            "★ 改回来还能看到：把项目根目录填回\n" +
            "    " + oldPath + "\n" +
            "  原来的项目就原样全回来了 —— 不用重新建档，也不用重新导入素材。\n\n" +
            "现在要改吗？点「取消」＝ 什么都不改（配置和项目列表都不动）。";
    }
}
