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

    /// <summary>写文件。自动建目录。</summary>
    public static void WriteFile<T>(string path, T value)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, Serialize(value), Utf8NoBom);
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
