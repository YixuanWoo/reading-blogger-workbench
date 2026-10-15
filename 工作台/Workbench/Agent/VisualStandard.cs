// ============================================================
// VisualStandard.cs - 视觉规范的加载与注入（T-26①，V1.11.3）
// ============================================================
//
// 【它解决什么问题】
//   画面标准原来写死在 ShotPrompt / RenderPrompt / ReshootPrompt 三份内置提示词里。
//   用户想改标准 = 等重新打包。V1.11.3 起标准的长相收进《视觉规范.md》，
//   三个提示词在组装时**读取这份文件注入** —— 用户改标准 = 改文件，存盘即生效。
//
// 【哪份是真的】
//   ★ 运行时读到的 exe 旁边那份 = 日常编辑的真身（改完即生效）。
//   工程夹 `文档\视觉规范.md` 只是出厂底稿：build.ps1 只在软件夹**没有**这份文件时才补种，
//   绝不覆盖 —— 否则一次打包会把用户改过的标准静默冲回出厂版。
//
// 【找不到文件怎么办】
//   返回 null，各提示词**原样走内置规则**（内置规则与规范 §一/§二 内容一致，
//   只是少了 §三拍板留档的个性部分）。绝不让"规范缺失"变成一个崩溃或空提示词。
//
// 【查找顺序】
//   ① exe 旁边（软件夹：D:\工作台\视觉规范.md）—— 运行时的正身，用户日常改这份；
//   ② 从 exe 目录往上走最多 8 级，找任何一级的 `文档\视觉规范.md`
//      —— 开发调试时 bin\Debug\net8.0-windows\... 往上 4 级就是工程夹，兜住 dev 场景
//      （dev 场景读到的是出厂底稿，属预期：调试不该被软件夹的用户改动影响）。
//   【为什么不做配置项】规范文件跟 exe 走、跟工程走，没有"用户自定义位置"的真实需求，
//   多一个配置就多一个要解释的坑（工程一贯态度：能不加的配置不加）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace LiteCut.Workbench.Agent;

public static class VisualStandard
{
    /// <summary>规范文件名（exe 旁 = 这个名字；工程夹 = `文档\` 下的同名文件）。</summary>
    public const string FileName = "视觉规范.md";

    /// <summary>本次实际读到的文件路径（提示词里要注明来源，让 Agent 和人都知道标准在哪）。</summary>
    public static string? LastSourcePath { get; private set; }

    /// <summary>
    /// 读规范全文。找不到 / 读不出来 → null（调用方跳过注入，走内置规则）。
    /// 每次组装任务包都重新读 —— 用户改了文件立刻生效，不缓存。
    /// </summary>
    public static string? TryRead()
    {
        LastSourcePath = null;
        foreach (string dir in CandidateDirs())
        {
            string path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) continue;
            try
            {
                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) continue;
                LastSourcePath = path;
                return text;
            }
            catch
            {
                // 单个候选读失败（被占用等）→ 换下一个候选，别炸
            }
        }
        return null;
    }

    /// <summary>
    /// 拼好可直接插进提示词开头的一节。没读到规范 → 返回空串（调用方判空跳过）。
    /// </summary>
    public static string BuildSection()
    {
        string? text = TryRead();
        if (text == null) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 视觉规范（单一真源 · 翊瑄拍板）");
        sb.AppendLine();
        sb.AppendLine("下面这份《视觉规范》是画面标准的**最高依据** —— 与本提示词后文的内置规则");
        sb.AppendLine("冲突时，**以视觉规范为准**（它更新更勤）。逐条照做，不要自由发挥。");
        sb.AppendLine();
        sb.AppendLine(text.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"（规范来源：{LastSourcePath} —— 修改标准请改那个文件，改完下次任务即生效。）");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>候选目录：exe 目录，然后逐级向上找带 `文档` 子目录的祖先（最多 8 级）。</summary>
    private static IEnumerable<string> CandidateDirs()
    {
        yield return AppContext.BaseDirectory;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            dir = dir.Parent;
            if (dir == null) break;
            string doc = Path.Combine(dir.FullName, "文档");
            if (Directory.Exists(doc)) yield return doc;
        }
    }
}
