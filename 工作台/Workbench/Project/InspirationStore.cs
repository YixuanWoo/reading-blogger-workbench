// ============================================================
// InspirationStore.cs - 灵感池
// ============================================================
//
// 【它是什么】
//   「灵感」这一栏的内容：还没成项目、但不想忘掉的想法。
//   一条灵感可以一键"立项"，变成一个真正的项目文件夹。
//
// 【为什么存在 %APPDATA% 而不是项目目录里】
//   灵感是"还没决定要做的东西"，天生不属于任何一个项目。
//   哪天它立项了，才落进项目目录。
//
// 【为什么不做成数据库】
//   一个人的灵感条目撑死几百条，一次读进内存、改完整体写回，
//   最简单也最不容易坏。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteCut.Core.Contracts;

namespace LiteCut.Workbench.Project;

/// <summary>一条灵感。</summary>
public sealed class InspirationItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Note { get; set; } = "";
    public string Channel { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>立项之后，指向那个项目的文件夹（空 = 还没立项）</summary>
    public string ProjectDir { get; set; } = "";
}

/// <summary>灵感池的读写。整体读、整体写。</summary>
public static class InspirationStore
{
    /// <summary>存档文件</summary>
    private static string FilePath => Path.Combine(AppConfig.DataDir, "inspiration.json");

    /// <summary>存档的外壳（方便将来加版本号/迁移）</summary>
    private sealed class FileShape
    {
        public int Version { get; set; } = 1;
        public List<InspirationItem> Items { get; set; } = new();
    }

    /// <summary>读出全部灵感（按时间倒序）。读不出来就返回空列表，不抛异常。</summary>
    public static List<InspirationItem> Load()
    {
        try
        {
            var f = ContractJson.ReadFile<FileShape>(FilePath);
            if (f?.Items == null) return new List<InspirationItem>();
            return f.Items.OrderByDescending(i => i.CreatedAt).ToList();
        }
        catch
        {
            return new List<InspirationItem>();
        }
    }

    /// <summary>整体写回。</summary>
    public static void Save(IEnumerable<InspirationItem> items)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            ContractJson.WriteFile(FilePath, new FileShape { Items = items.ToList() });
        }
        catch
        {
            // 存不下不该打断用户，下次再说
        }
    }
}
