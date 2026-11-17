// ============================================================
// ContractJson.cs - 契约的统一读写方式
// ============================================================
//
// 【为什么要有一个统一的入口】
//
//   契约是"给 AI 看"和"给程序看"的同一份文件，如果两边
//   对格式的理解有偏差，就会出现"AI 说它写了、程序却读不出"。
//   所以序列化选项必须只有一处定义，所有地方都用它。
//
// 【三个关键设置】
//
//   1. 中文不转义
//      System.Text.Json 默认会把"你好"写成"\u4F60\u597D"。
//      那样 AI 读起来会很费劲，而且文件也大。
//      UnsafeRelaxedJsonEscaping 让它保持中文原样。
//      （"Unsafe"指的是"如果输出到 HTML 里要注意转义"，
//        我们只写本地文件，不用担心。）
//
//   2. 枚举写成小驼峰字符串
//      Keep → "keep"，Silence → "silence"。
//      比数字好读——AI 和人都能直接看懂，也不怕枚举顺序变了。
//
//   3. 忽略 null
//      让文件干净，只保留真正有值的字段。
//
// ============================================================

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteCut.Core.Contracts;

/// <summary>
/// 契约文件的统一读写工具。
/// 所有契约的序列化/反序列化都必须走这里，不要自己 new JsonSerializerOptions。
/// </summary>
public static class ContractJson
{
    /// <summary>
    /// ★★ 无 BOM 的 UTF-8 —— 凡是要写文件给**外部工具**读的，一律用这个 ★★
    ///
    /// 【为什么必须用它，不能用 Encoding.UTF8】
    ///   .NET 的 <c>Encoding.UTF8</c> 静态实例 <c>GetPreamble()</c> 返回 BOM，
    ///   所以 <c>File.WriteAllText(p, s, Encoding.UTF8)</c> **一定会写 BOM**（EF BB BF）。
    ///   而 Python <c>json.load</c>、node、各类 JSON 解析器读带 BOM 的文件会**直接报错**
    ///   （Python 抛 "Unexpected UTF-8 BOM"）。
    ///
    /// 【踩过几次（R-13）：这就是"同一类坑反复踩"的典型案例】
    ///   ① agent-request.json ② 渲染任务包 ③ render-manifest.json
    ///   ④ shot-overrides.json ⑤ FCP7 XML
    ///   —— 所以这里放**唯一常量**，新代码直接引用，不要再自己写 <c>Encoding.UTF8</c>。
    ///
    /// 【唯一例外：故意要 BOM 的场合】
    ///   导给 Excel 打开的 CSV（Excel 不认无 BOM 的 UTF-8 中文，会乱码）——
    ///   那两处保留 <c>Encoding.UTF8</c>，是有意为之，别"顺手统一"给改掉。
    /// </summary>
    public static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>全局唯一的序列化设置。</summary>
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        // 缩进输出：文件是给人看的，也是给 AI 看的，可读性优先
        WriteIndented = true,

        // 中文不转义成 \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // null 字段直接不写，文件干净
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // ★ V1.17.0：读的时候忽略大小写。
        //
        // 【为什么必须开】
        //   契约文件（transcript.json / cutplan.json …）按约定是**小驼峰**
        //   （"schemaVersion"），但早期有两条路把同一批对象按 PascalCase 写进了
        //   project.json（`.NET` 的默认行为）。V1.17.0 给 Transcript 补上了
        //   显式 JsonPropertyName 之后，老档案里的 PascalCase 字段就**读不回来**了
        //   —— 用户打开几个月前的项目，转写稿会静默消失（不是报错，是变成空稿）。
        //
        //   打开这个开关，两种写法都能读；写出去仍然按 JsonPropertyName 的
        //   小驼峰 —— 契约一致，老数据不丢。这是"只放宽读、不改变写"的改法。
        PropertyNameCaseInsensitive = true,

        // 枚举写成字符串（小驼峰）
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        },
    };

    /// <summary>把对象写成 JSON 文本。</summary>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    /// <summary>从 JSON 文本读回对象。读不出来时返回 null（不抛异常）。</summary>
    public static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 写文件（序列化 + 原子落盘）。自动建目录。
    /// 内部交给 <see cref="WriteTextAtomic"/>，所以写盘语义与它完全一致。
    /// </summary>
    public static void WriteFile<T>(string path, T value)
    {
        WriteTextAtomic(path, Serialize(value));
    }

    /// <summary>
    /// 把一段文本**原子地**写到 <paramref name="path"/> —— 全项目唯一的用户数据写入口。
    ///
    /// 【为什么必须是「同目录 tmp + 换名」】
    ///   「原子」在文件系统里只有一个意思：**读的人要么看到完整的旧内容，
    ///   要么看到完整的新内容，绝看不到半截**。做到这一点的动作只有一个 ——
    ///   换名（rename / ReplaceFile）。所以判别式就三条：
    ///     · 出现 <c>File.Replace</c> → 才算原子；
    ///     · 出现 <c>File.Delete</c> 再 <c>File.Move</c> → 有「原文件已没了、新文件还没到位」
    ///       的窗口，崩溃/断电就只剩一个 .tmp（V1.21.0 之前 WritingDocumentStore 就是这样，
    ///       注释写着「原子写」，实现却少了那一半）；
    ///     · 裸 <c>File.WriteAllText(目标路径)</c> → 一律按「会写出半截/空文件」处理
    ///       （写入过程中断电就是半截，读者还以为文件是好的）。
    ///
    /// 【为什么 tmp 必须固定在同目录、同一个名字】
    ///   · 同目录 = 同卷，<c>File.Replace</c> 才成立（跨卷的换名其实是拷贝，不原子）；
    ///   · 固定名字 = 崩溃留下的 .tmp 会被下一次写入覆盖，不会越积越多，
    ///     而且失败路径能主动把它清掉（见下方 catch）。
    ///
    /// 【为什么要 WriteThrough + Flush(flushToDisk)】
    ///   StreamWriter 的 Flush 只是把缓冲区交给操作系统，内容可能还躺在系统缓存里。
    ///   断电时缓存里的东西会没，那时换名换来的是一个「名字对、内容是空的」文件。
    ///   所以先写穿到盘（WriteThrough），再显式 flushToDisk，然后才换名。
    ///
    /// 【失败怎么办：抛，不吞】
    ///   以前这里是 catch {} 吞掉 —— 那正是「账本悄悄变空」的另一半：
    ///   写失败没人知道，下次读回来是空的，程序还当成正常继续跑。
    ///   现在一律 <c>throw</c> 原样抛出，由调用方（Store / 界面）决定怎么告诉用户。
    /// </summary>
    /// <exception cref="IOException">写不进磁盘（磁盘满、只读、文件被别的程序独占等）</exception>
    public static void WriteTextAtomic(string path, string content)
    {
        string full = Path.GetFullPath(path);

        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 固定同目录同名（见上方注释：同卷 + 残留可被下次写入覆盖）
        string tmp = full + ".tmp";

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                           64 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, Utf8NoBom))
            {
                writer.Write(content);
                writer.Flush();                      // 先把 StreamWriter 自己的缓冲交出去
                fs.Flush(flushToDisk: true);         // 再让操作系统真的落盘，下一步才换名
            }

            if (File.Exists(full))
            {
                try
                {
                    File.Replace(tmp, full, null);
                }
                catch (PlatformNotSupportedException)
                {
                    // 个别网络盘 / FAT 不支持 ReplaceFile。
                    // Move + overwrite 仍然是「换名」，同样不会写出半截，只是没有备份语义。
                    File.Move(tmp, full, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, full);
            }
        }
        catch
        {
            // 尽力清掉自己的残留（固定名字意味着不清就会一直躺在那儿）；
            // 清不掉也不影响结论 —— 反正异常要原样抛出去。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>读文件。文件不存在或格式错误时返回 null。</summary>
    public static T? ReadFile<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        return Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// 把内嵌的 JSON Schema 读出来。
    ///
    /// 用途：生成给 Agent 的提示词时，把 schema 原文塞进去，
    /// 当作"必须按这个结构输出"的硬约束。
    /// 这样 AI 的输出就能被程序直接校验，不用靠猜。
    /// </summary>
    /// <param name="name">schema 文件名，如 "cutplan.schema.json"</param>
    public static string? LoadSchema(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        string resourceName = "LiteCut.Core.Schemas." + name;

        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
