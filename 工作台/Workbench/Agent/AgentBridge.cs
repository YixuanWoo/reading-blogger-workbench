// ============================================================
// AgentBridge.cs - 工作台 ↔ Agent 的桥（两条通道，界面上一套用法）
// ============================================================
//
// 【为什么要有这个东西】
//   用户的要求（2026-09-29 拍板）：分镜设计、镜头渲染都交给 Agent 做，
//   全程不离开工作台，调用过程中要有进度指示。
//
//   但"工作台调 Agent"有两种落地方式，各有适用场景，所以都实现了：
//
//   ① 直连大模型（AppConfig 里填了 BaseUrl + Key + Model 就走这条）
//      OpenAI 兼容的 /chat/completions，支持流式。
//      进度条是真的在动（能看到内容一个个 token 回来）。
//
//   ② 任务包文件（没填 key 时自动走这条）
//      工作台把任务写成 agent-request.json + prompt.md 落到项目的 stages 目录，
//      然后盯着 agent-response.json。WorkBuddy 那边的 Agent 读任务包、写回回包，
//      工作台检测到就自动回填 —— 用户始终待在工作台里。
//      进度条显示"等待 Agent 产出（已等 xx 秒）"，不会让人以为卡死了。
//
// 【两条通道对界面是同一套】
//   调用 → 进度回调 → 拿到字符串 → 解析 → 回填。切通道只改配置，不动界面代码。
//
// 【超时与取消】
//   两条通道都吃 CancellationToken。文件通道还要负责清理：
//   取消/超时后把 request 文件删掉，免得下次误读到一份没人处理的旧任务。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteCut.Workbench.Agent;

/// <summary>一次 Agent 调用进行到哪了。</summary>
public sealed class AgentProgress
{
    /// <summary>阶段名："准备任务包" / "调用中" / "等待 Agent 产出" / "解析结果"。</summary>
    public string Stage { get; set; } = "";

    /// <summary>补充说明，例如"已收到 1240 字"。</summary>
    public string Detail { get; set; } = "";

    /// <summary>0~1 的确定进度；null = 说不准，界面画转圈。</summary>
    public double? Ratio { get; set; }
}

/// <summary>Agent 调用的结果。</summary>
public sealed class AgentResult
{
    public bool Ok { get; set; }
    public string Content { get; set; } = "";
    public string Error { get; set; } = "";

    /// <summary>走的哪条通道（"http" / "file" / "test"），界面上可以提一句。</summary>
    public string Channel { get; set; } = "";

    /// <summary>补充信息（检测成功时的"模型 · 延迟"之类）。</summary>
    public string Detail { get; set; } = "";
}

/// <summary>发给 Agent 的一次任务。</summary>
public sealed class AgentRequest
{
    public string Title { get; set; } = "";

    /// <summary>
    /// 任务类型标记，写进任务包（"shot" 分镜设计 / "render" 整批渲染 / "reshoot" 单镜重渲）。
    /// 领任务的人靠它一眼分清这次要干什么活 —— 不看文件名也不会搞错。
    /// </summary>
    public string Kind { get; set; } = "";

    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";

    /// <summary>任务包落盘目录（文件通道用；项目 stages/03-shot 之类）。</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>额外元信息（项目名、镜头数……），写进任务包方便 Agent 核对。</summary>
    public Dictionary<string, string> Meta { get; set; } = new();

    /// <summary>文件通道最多等多久（分钟）。默认 15 分钟。</summary>
    public int WaitMinutes { get; set; } = 15;

    /// <summary>
    /// 请求 / 回包的文件名覆盖（留空 = 默认的 agent-request.json / agent-response.json）。
    ///
    /// 【为什么需要这个】
    ///   「整批渲染」和「单镜重渲」落在同一个目录（stages/04-render），
    ///   如果都写 agent-request.json，两份任务就会互相覆盖 ——
    ///   用户一边补渲剩下的镜头、一边调某一镜，谁都活不下来。
    ///   单镜重渲改用 agent-reshoot.json，两个任务在磁盘上各占一处，
    ///   领任务的人看一眼文件名就知道这次要干哪种活。
    /// </summary>
    public string RequestFileName { get; set; } = "";

    /// <summary>回包文件名覆盖（留空 = agent-response.json）。见 <see cref="RequestFileName"/>。</summary>
    public string ResponseFileName { get; set; } = "";

    /// <summary>
    /// 任务包里那份"人能读的提示词"的文件名覆盖（留空 = prompt.md）。
    /// 也跟着请求文件走 —— 重渲那份不要覆盖整批那份，两份都留着才查得清。
    /// </summary>
    public string PromptFileName { get; set; } = "";
}

public static class AgentBridge
{
    /// <summary>填了接口地址和密钥就走直连。</summary>
    public static bool UseHttp(AppConfig cfg) =>
        cfg != null
        && !string.IsNullOrWhiteSpace(cfg.AgentBaseUrl)
        && !string.IsNullOrWhiteSpace(cfg.AgentApiKey);

    /// <summary>接口地址补成完整 URL（允许只填域名）。</summary>
    public static string EndpointOf(AppConfig cfg)
    {
        string b = (cfg.AgentBaseUrl ?? "").TrimEnd('/');
        if (b.Length == 0) return "";
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        return b + "/chat/completions";
    }

    // ========================================================
    // 入口
    // ========================================================

    // watchDetail：文件通道的实时进度钩子（渲染这类长任务用）。
    //   每秒轮询时调用一次，返回一段附加进度文字（比如"已出片 12/219"），
    //   拼进进度详情里 —— 分镜那种"生成一段文字"的任务用不上它。
    public static async Task<AgentResult> CallAsync(
        AgentRequest req,
        AppConfig cfg,
        Action<AgentProgress>? onProgress = null,
        CancellationToken ct = default,
        Func<string>? watchDetail = null)
    {
        return UseHttp(cfg)
            ? await CallHttpAsync(req, cfg, onProgress, ct).ConfigureAwait(false)
            : await CallByFileAsync(req, onProgress, ct, watchDetail).ConfigureAwait(false);
    }

    // ========================================================
    // ★ 连通性检测（"Agent 已就绪"那一下）
    //   真发一次最小请求 —— 不做假检测，通就是通，不通就给人话原因。
    // ========================================================

    /// <summary>
    /// 真调一次接口（最小请求，max_tokens 限到 8，成本几乎为零）。
    /// 成功返回 Ok=true，Detail 形如 "deepseek-chat · 823ms"。
    /// </summary>
    public static async Task<AgentResult> TestAsync(AppConfig cfg, CancellationToken ct = default)
    {
        if (!UseHttp(cfg))
            return new AgentResult
            {
                Ok = false,
                Channel = "test",
                Error = "未配置直连接口（接口地址/密钥任一为空）—— 当前走任务包通道",
            };

        string endpoint = EndpointOf(cfg);
        string model = string.IsNullOrWhiteSpace(cfg.AgentModel) ? "deepseek-chat" : cfg.AgentModel;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[] { new { role = "user", content = "请只回复两个字：就绪" } },
            ["temperature"] = 0,
            ["max_tokens"] = 8,
            ["stream"] = false,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // ★ 检测请求也要带密钥 —— 否则测的是"匿名访问"，不是真实调用路径
            using var content = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(cfg.AgentApiKey))
                content.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.AgentApiKey.Trim());

            using var resp = await Http.SendAsync(content, ct).ConfigureAwait(false);
            long ms = sw.ElapsedMilliseconds;

            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new AgentResult { Ok = false, Channel = "test", Error = FriendlyHttpError((int)resp.StatusCode, body, endpoint) };
            }

            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            string reply = ExtractNonStream(text);
            if (string.IsNullOrWhiteSpace(reply))
                return new AgentResult
                {
                    Ok = false,
                    Channel = "test",
                    Error = $"接口通了但返回是空的（HTTP 200）—— 这个地址可能不是对话接口：{endpoint}",
                };

            return new AgentResult
            {
                Ok = true,
                Channel = "test",
                Content = reply,
                Detail = $"{model} · {ms}ms",
            };
        }
        catch (OperationCanceledException)
        {
            return new AgentResult { Ok = false, Channel = "test", Error = "检测超时/已取消" };
        }
        catch (Exception ex)
        {
            return new AgentResult { Ok = false, Channel = "test", Error = FriendlyNetworkError(ex, endpoint) };
        }
    }

    // ========================================================
    // ★ 错误翻译 —— 接口报错时给人能看懂的判断，
    //   别让用户对着 "HTTP 401: {...json...}" 猜半天。
    // ========================================================

    public static string FriendlyHttpError(int status, string body, string endpoint)
    {
        string hint = status switch
        {
            401 or 403 => "密钥无效或没有权限 —— 检查 API Key 是否填对、有没有余额。",
            404 => "接口路径没找到 —— Base URL 一般要填到 /v1（例如 https://api.deepseek.com/v1），现在的地址是 " + endpoint,
            405 => "这个地址不接受 POST —— Base URL 多半填过了头（直接填到 /chat/completions 试试，或只填到 /v1）。",
            429 => "被限流了（余额不足或请求太频繁），稍后再试。",
            >= 500 => "服务端出错了（HTTP " + status + "），跟你的配置无关，稍后再试。",
            _ => "HTTP " + status,
        };
        return $"{hint}{Environment.NewLine}原始返回：{Trim(body, 300)}";
    }

    public static string FriendlyNetworkError(Exception ex, string endpoint)
    {
        string m = ex.Message;
        if (m.Contains("no such host", StringComparison.OrdinalIgnoreCase) ||
            (m.Contains("name", StringComparison.OrdinalIgnoreCase) &&
             (m.Contains("resolve", StringComparison.OrdinalIgnoreCase) || m.Contains("解析", StringComparison.Ordinal))))
            return $"域名解析不了：{HostOf(endpoint)} —— 检查网络，或地址是不是写错了。";
        if (m.Contains("refused", StringComparison.OrdinalIgnoreCase) || m.Contains("拒绝", StringComparison.Ordinal))
            return $"连接被拒绝：{HostOf(endpoint)} 不响应 —— 地址或端口不对，或服务没开。";
        if (m.Contains("timeout", StringComparison.OrdinalIgnoreCase) || m.Contains("超时", StringComparison.Ordinal))
            return $"连接超时：到 {HostOf(endpoint)} 的请求没有响应 —— 检查网络/代理。";
        return m;
    }

    /// <summary>
    /// ★ 桥接不可用时的**可执行**提示（2026-10-03 加）。
    ///
    /// 【为什么要单独写一条】
    ///   原来直接抛一句"连接被拒绝：127.0.0.1 不响应"——
    ///   这话**把用户引向错误的诊断方向**：他会以为地址/端口配错了、要去改配置，
    ///   而真实原因常常只是"桥接进程没在跑"这种一拉就好的事。
    ///   2026-10-03 实测：`connect ECONNREFUSED 127.0.0.1:<旧端口>` 看着像网络问题，
    ///   实际是**凭据滞后**（网关端口每会话变）—— 两件事用同一句文案报出来，用户只能猜。
    ///   所以这里按"谁没起来 / 怎么救"分类说清楚。
    /// </summary>
    public static string BridgeUnavailableHint(Exception? ex = null)
    {
        var sb = new StringBuilder();

        // ① 桥接进程本身在不在 —— 这是最常见、也最好修的一种
        bool bridgeUp;
        try { bridgeUp = BridgeAvailable(); }
        catch { bridgeUp = false; }

        if (!bridgeUp)
        {
            sb.AppendLine("桥接进程没在运行（127.0.0.1:18765 无响应）。");
            if (LastLaunchError != null)
            {
                // 看护已经试过并且失败了 —— 把它的原因原样说出来，别让用户猜
                sb.AppendLine("自动拉起也失败了：" + LastLaunchError);
            }
            else
            {
                sb.AppendLine("工作台会每 10 秒自动重试一次，通常几秒内就恢复。");
            }
            sb.AppendLine("若一直不通，请确认 WorkBuddy 已打开（没开时没有 Agent 可唤醒）。");
            return sb.ToString().TrimEnd();
        }

        // ② 桥接在，但连网关失败 —— 这才是"凭据 / 端口"那一类
        sb.AppendLine("桥接在运行，但它连不上 WorkBuddy 网关。");
        sb.AppendLine("最常见原因：网关端口每个会话都会变，而桥接还在用上一轮的旧端口。");
        sb.AppendLine("解决办法：在 WorkBuddy 里随便发一句话（会自动刷新凭据），然后重试。");
        if (ex != null)
            sb.AppendLine("原始错误：" + Trim(ex.Message, 160));
        return sb.ToString().TrimEnd();
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }


    // ========================================================
    // ① 直连大模型
    // ========================================================

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

    private static async Task<AgentResult> CallHttpAsync(
        AgentRequest req, AppConfig cfg, Action<AgentProgress>? onProgress, CancellationToken ct)
    {
        string endpoint = EndpointOf(cfg);
        string model = string.IsNullOrWhiteSpace(cfg.AgentModel) ? "deepseek-chat" : cfg.AgentModel;

        var messages = new object[]
        {
            new { role = "system", content = req.SystemPrompt },
            new { role = "user", content = req.UserPrompt },
        };

        bool stream = cfg.AgentStream;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0.7,
            ["stream"] = stream,
        };

        string json = JsonSerializer.Serialize(payload);

        // ★ per-request 的 HttpRequestMessage：Authorization 头必须每次显式带上。
        //   （曾经漏掉 —— 直连请求没带密钥，接口自然回 401，
        //     这就是"第一次点交给 Agent 弹错"的根因之一。）
        using var content = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(cfg.AgentApiKey))
            content.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.AgentApiKey.Trim());

        onProgress?.Invoke(new AgentProgress
        {
            Stage = "调用中",
            Detail = stream ? $"{model}（直连·流式）" : $"{model}（直连·非流式，生成完才一次性返回）",
        });

        try
        {
            using var resp = await Http.SendAsync(content, ct).ConfigureAwait(false);

            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new AgentResult
                {
                    Ok = false,
                    Channel = "http",
                    Error = FriendlyHttpError((int)resp.StatusCode, body, endpoint),
                };

            if (!stream) return new AgentResult { Ok = true, Channel = "http", Content = ExtractNonStream(body) };

            // 流式：一边收一边报进度，进度条才是真的在动，而不是假装转圈
            return await ReadStreamAsync(resp, model, onProgress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AgentResult { Ok = false, Channel = "http", Error = "已取消" };
        }
        catch (Exception ex)
        {
            return new AgentResult { Ok = false, Channel = "http", Error = FriendlyNetworkError(ex, endpoint) };
        }
    }

    /// <summary>逐行读 SSE，把 delta.content 接起来，顺带回调已收到的字数。</summary>
    private static async Task<AgentResult> ReadStreamAsync(
        HttpResponseMessage resp, string model, Action<AgentProgress>? onProgress, CancellationToken ct)
    {
        var sb = new StringBuilder();
        bool announced = false;

        using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(s, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            ct.ThrowIfCancellationRequested();

            string t = line.Trim();
            if (!t.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = t.Substring(5).Trim();
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0 &&
                    ch[0].TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object &&
                    d.TryGetProperty("content", out var c))
                {
                    sb.Append(c.GetString());

                    if (!announced)
                    {
                        announced = true;
                        onProgress?.Invoke(new AgentProgress { Stage = "生成中", Detail = "连接成功，模型开始输出" });
                    }

                    onProgress?.Invoke(new AgentProgress
                    {
                        Stage = "生成中",
                        Detail = $"{model} · 已收到 {sb.Length} 字",
                    });
                }
            }
            catch { /* 单行坏了不影响整体 */ }
        }

        return new AgentResult { Ok = true, Channel = "http", Content = sb.ToString() };
    }

    /// <summary>非流式返回：取 choices[0].message.content。</summary>
    private static string ExtractNonStream(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
            {
                var first = ch[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    return c.GetString() ?? "";
            }
        }
        catch { /* 解析不了就退回原文，让上层看得到 */ }

        return body;
    }

    /// <summary>流式（SSE）返回：把每个 delta.content 接起来。</summary>
    private static string ExtractSse(string body)
    {
        var sb = new StringBuilder();

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line.Substring(5).Trim();
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                {
                    var delta = ch[0].TryGetProperty("delta", out var d) ? d : default;
                    if (delta.ValueKind == JsonValueKind.Object &&
                        delta.TryGetProperty("content", out var c))
                    {
                        sb.Append(c.GetString());
                    }
                }
            }
            catch { /* 单行坏了不影响整体 */ }
        }

        return sb.Length > 0 ? sb.ToString() : body;
    }

    // ========================================================
    // ③ 桥接唤醒（任务一）：让「交给 Agent」按钮自动唤醒 WorkBuddy 会话里的 Agent，
    //    免去用户手动说"领任务"。写完任务包后 best-effort 调本地桥接 /wake，
    //    由桥接转发 CodeBuddy Gateway 把我唤醒；桥接没跑则退回手动提示。
    // ========================================================

    private const string BridgeBaseUrl = "http://127.0.0.1:18765";

    /// <summary>桥接是否在跑（同步探测 /health）。仅应在后台线程调用，避免 UI 线程死锁。</summary>
    public static bool BridgeAvailable()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BridgeBaseUrl + "/health");
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// best-effort 自动唤醒：调本地桥接 /wake，由它转发 Gateway 把我唤醒。
    /// 桥接没跑就返回 Ok=false（调用方退回"手动领任务"提示），不抛异常。
    /// </summary>
    public static async Task<AgentResult> WakeViaBridgeAsync(AgentRequest req, CancellationToken ct)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["kind"] = req.Kind,
                ["packagePath"] = req.WorkDir,
            };
            using var c = new HttpRequestMessage(HttpMethod.Post, BridgeBaseUrl + "/wake")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var r = await Http.SendAsync(c, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                string err = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new AgentResult { Ok = false, Channel = "bridge", Error = "桥接返回 " + (int)r.StatusCode + "：" + Trim(err, 200) };
            }
            return new AgentResult { Ok = true, Channel = "bridge" };
        }
        catch (Exception ex)
        {
            return new AgentResult
            {
                Ok = false,
                Channel = "bridge",
                Error = BridgeUnavailableHint(ex) + Environment.NewLine
                      + "也可以直接在 WorkBuddy 里说\"领任务\"。",
            };
        }
    }

    /// <summary>
    /// ★ 通路自检（「检测 Agent」按钮在任务包/桥接模式下走这条）。
    ///
    /// 【为什么不复用 /wake 就完事】
    ///   /wake 只回一句 HTTP 202 —— 而 202 **不代表 Agent 真处理了**
    ///   （Agent 正忙时网关会静默丢弃它，界面却照样显示"已唤醒"）。
    ///   所以自检认**收据**：唤醒消息里要求 Agent 用工具把一行含一次性 token 的
    ///   JSON 写进 <c>%LOCALAPPDATA%\LiteCut\ping-receipt.json</c>，
    ///   桥接盯着这个文件，**token 对上了才算通**。
    ///   ★ 顺带验证了"唤醒之后能不能真干活" —— 写文件必须动工具，
    ///     光会回话不算数（这正是 2026-10-01 实测查出的那个坑）。
    ///
    /// 【两段式】POST /ping 立刻拿 token（不等结果）→ 循环 GET /ping/{token}
    ///   每秒刷新一次，界面能看到"已等 xx 秒 / 正在排队"，而不是干等。
    /// </summary>
    public static async Task<AgentResult> PingViaBridgeAsync(
        Action<AgentProgress>? onProgress = null, CancellationToken ct = default)
    {
        try
        {
            using var c = new HttpRequestMessage(HttpMethod.Post, BridgeBaseUrl + "/ping")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            using var r = await Http.SendAsync(c, ct).ConfigureAwait(false);
            string body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!r.IsSuccessStatusCode)
                return new AgentResult
                {
                    Ok = false,
                    Channel = "bridge",
                    // ★ 502 = 桥接把这次自检判 failed（最常见：它手里的网关端口是旧会话的）。
                    //   原来只甩一个"桥接拒绝自检（HTTP 502）"，用户完全不知道该干什么。
                    Error = $"桥接拒绝自检（HTTP {(int)r.StatusCode}）：{Trim(FirstError(body), 200)}"
                          + Environment.NewLine + BridgeUnavailableHint(),
                };

            string token = JsonStr(body, "token");
            if (token.Length == 0)
                return new AgentResult
                {
                    Ok = false,
                    Channel = "bridge",
                    Error = "桥接没返回自检令牌：" + Trim(body, 200),
                };

            bool queued = JsonBool(body, "queued");
            var sw = Stopwatch.StartNew();

            onProgress?.Invoke(new AgentProgress
            {
                Stage = "自检中",
                Detail = queued ? "Agent 正忙 —— 已排队，等它空闲" : "唤醒已发出，等 Agent 写回收据…",
            });

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct).ConfigureAwait(false);

                using var q = new HttpRequestMessage(HttpMethod.Get, BridgeBaseUrl + "/ping/" + token);
                using var qr = await Http.SendAsync(q, ct).ConfigureAwait(false);
                string qb = await qr.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!qr.IsSuccessStatusCode)
                    return new AgentResult
                    {
                        Ok = false,
                        Channel = "bridge",
                        Error = "自检查询失败（HTTP " + (int)qr.StatusCode + "）：" + Trim(FirstError(qb), 200),
                    };

                string state = JsonStr(qb, "state");
                int sec = (int)sw.Elapsed.TotalSeconds;

                if (state == "done")
                    return new AgentResult
                    {
                        Ok = true,
                        Channel = "bridge",
                        Content = token,
                        Detail = $"{sec} 秒" + (queued ? "（含排队等待）" : ""),
                    };

                if (state == "timeout" || state == "failed")
                {
                    string msg = JsonStr(qb, "message");
                    return new AgentResult
                    {
                        Ok = false,
                        Channel = "bridge",
                        Error = (msg.Length > 0 ? msg : "自检未通过")
                              + Environment.NewLine + BridgeUnavailableHint(),
                    };
                }

                onProgress?.Invoke(new AgentProgress
                {
                    Stage = "自检中",
                    Detail = (state == "queued" ? "Agent 正忙 · 已排队 " : "已等 ") + $"{sec} 秒",
                });
            }
        }
        catch (OperationCanceledException)
        {
            return new AgentResult { Ok = false, Channel = "bridge", Error = "自检已取消 / 等超时了" };
        }
        catch (Exception ex)
        {
            return new AgentResult
            {
                Ok = false,
                Channel = "bridge",
                Error = "连不上本地桥接（" + ex.Message + "）—— 桥接没在跑？看 工具\\agent-bridge\\bridge.log",
            };
        }
    }

    private static string JsonStr(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static bool JsonBool(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
        }
        catch { }
        return false;
    }

    /// <summary>桥接的报错都在 error 字段里；没有就把原文端上来。</summary>
    private static string FirstError(string json)
    {
        string e = JsonStr(json, "error");
        return e.Length > 0 ? e : json;
    }

    /// <summary>
    /// 自启回退：若桥接没在跑，尝试用 node 拉起桥接脚本（读凭据文件，不依赖会话 env）。
    ///
    /// ★★ 2026-10-03：原来这里是**一个写死的路径 + 拉不起来就静默返回**，
    ///   那是"关过电脑后工作台再也连不上"的主要成因 ——
    ///   换盘/挪目录/只拷软件夹，脚本就找不到，而用户什么都看不到。
    ///   现在改成：<b>多候选路径逐个试 → 找到就记住 → 全找不到明确报出来</b>。
    /// </summary>
    private static readonly string[] BridgeScriptCandidates =
    {
        // ① 上次成功用过的（最优先，见 TryFindBridgeScript）
        Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\工具\agent-bridge\bridge.js"),
        // ② 源码工程夹（开发机 / 未拆分的部署）
        @"D:\工作台工程\工作台\工具\agent-bridge\bridge.js",
        // ③ 跟着可执行文件走（发布版把脚本放 exe 同级 或 同级的 工具\ 下）
        Path.Combine(AppContext.BaseDirectory, "bridge.js"),
        Path.Combine(AppContext.BaseDirectory, @"工具\agent-bridge\bridge.js"),
        Path.Combine(AppContext.BaseDirectory, @"..\工具\agent-bridge\bridge.js"),
    };

    private static readonly string[] NodeExeCandidates =
    {
        // ① 工作台自带的 node（隔离区，最稳）
        @"C:\Users\wuhao\.workbuddy\binaries\node\versions\22.22.2-3\node.exe",
        // ② PATH 上的 node
        "node.exe",
        "node",
    };

    /// <summary>上次成功拉起用的脚本路径 —— 进程内记忆，避免每次都从头试。</summary>
    private static string? _cachedScriptPath;

    /// <summary>最近一次自启失败的原因，供界面提示（null = 没失败过）。</summary>
    public static string? LastLaunchError { get; private set; }

    /// <summary>找到可用的桥接脚本路径；找不到返回 null。</summary>
    private static string? TryFindBridgeScript(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath! : null;

        if (_cachedScriptPath != null && File.Exists(_cachedScriptPath))
            return _cachedScriptPath;

        foreach (var c in BridgeScriptCandidates)
        {
            try
            {
                if (File.Exists(c)) { _cachedScriptPath = c; return c; }
            }
            catch { /* 路径含非法字符就跳过 */ }
        }
        return null;
    }

    /// <summary>找到可用的 node 可执行文件路径；找不到返回 null。</summary>
    private static string? TryFindNodeExe(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath! : null;

        foreach (var c in NodeExeCandidates)
        {
            try
            {
                if (Path.IsPathRooted(c))
                {
                    if (File.Exists(c)) return c;
                }
                else
                {
                    // PATH 上的名字：交给系统去解析
                    return c;
                }
            }
            catch { /* 忽略 */ }
        }
        return null;
    }

    /// <summary>
    /// 桥接没在跑就拉起来。返回 true = 拉起成功（或本来就在跑）。
    /// ★ 不再静默失败：把原因写进 <see cref="LastLaunchError"/>，界面能直接告诉用户怎么办。
    /// </summary>
    public static bool EnsureBridgeLaunched(string? scriptPath = null, string? nodePath = null)
    {
        if (BridgeAvailable()) { LastLaunchError = null; return true; }

        try
        {
            string? script = TryFindBridgeScript(scriptPath);
            if (script == null)
            {
                LastLaunchError =
                    "找不到桥接脚本 bridge.js（试过工程夹与 exe 同级等 " +
                    BridgeScriptCandidates.Length + " 个位置）。"
                    + "如果工作台被挪过目录，请把 agent-bridge 文件夹放到 exe 同级的 工具\\ 下。";
                return false;
            }

            string? node = TryFindNodeExe(nodePath);
            if (node == null)
            {
                LastLaunchError = "找不到可用的 node.exe —— 桥接需要它才能运行。";
                return false;
            }

            var psi = new ProcessStartInfo(node, "\"" + script + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(script) ?? "",
            };
            Process.Start(psi);
            LastLaunchError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastLaunchError = "拉起桥接失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// ★ 桥接看护心跳（2026-10-03 加）：后台常驻，每 <see cref="BridgeWatchdogIntervalMs"/>
    ///   探一次 <c>/health</c>；发现桥接不在就立刻重新拉起。
    ///
    /// 【为什么必须有它 —— 这才是"任何时候都能连通"的关键】
    ///   桥接是**独立 node 进程**，但它是被 WorkBuddy 宿主（或工作台）拉起来的，
    ///   生命周期挂在别人的进程树上：宿主重启 / WorkBuddy 崩了 / 切会话 / 电脑休眠唤醒，
    ///   桥接就没了。而工作台原来只在 <c>App.OnStartup</c> 调一次 EnsureBridgeLaunched ——
    ///   <b>工作台自己一直开着，也不会再拉第二次</b>。
    ///   于是表现就是：隔了一夜 / 重开过电脑 → 点"检测 Agent"永远报连接被拒，
    ///   而用户完全不知道要"重开工作台"才能救。
    /// </summary>
    private static readonly System.Threading.Timer BridgeWatchdog =
        new(_ => WatchdogTick(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>看护探测间隔（毫秒）。10 秒：足够快到用户察觉不到，又不至于把 CPU 拉高。</summary>
    public const int BridgeWatchdogIntervalMs = 10_000;

    private static int _watchdogBusy;

    /// <summary>启动看护。幂等 —— 调多少次都只会起一个看护循环。</summary>
    public static void StartBridgeWatchdog()
    {
        try
        {
            BridgeWatchdog.Change(BridgeWatchdogIntervalMs, BridgeWatchdogIntervalMs);
        }
        catch { /* 看护起不来不影响主程序 */ }
    }

    /// <summary>停掉看护（退出时用，避免进程拖住不走）。</summary>
    public static void StopBridgeWatchdog()
    {
        try { BridgeWatchdog.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
    }

    private static void WatchdogTick()
    {
        // 防止上一次探测没跑完就叠一个（探测本身有 20 分钟 HttpClient 超时，理论上会）
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1) return;
        try
        {
            if (BridgeAvailable()) return;   // 桥接在，什么都不用做

            bool ok = EnsureBridgeLaunched();
            if (ok)
            {
                // 拉起后稍等一下再确认，让日志能留下"起来了"的证据
                Thread.Sleep(800);
                if (BridgeAvailable())
                {
                    LogBridgeWatchdog("桥接不在，已自动重新拉起。");
                }
            }
            else
            {
                LogBridgeWatchdog("桥接不在，自动拉起失败：" + LastLaunchError);
            }
        }
        catch (Exception ex)
        {
            LogBridgeWatchdog("看护异常（已忽略）：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    /// <summary>看护自己的留痕（走工作台日志，与界面提示分开）。</summary>
    private static void LogBridgeWatchdog(string msg)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteCut", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "bridge-watchdog.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
        }
        catch { /* 记不了日志就算了 */ }
    }

    // ========================================================
    // ② 任务包文件
    // ========================================================

    private const string RequestFile = "agent-request.json";
    private const string ResponseFile = "agent-response.json";
    private const string PromptFile = "prompt.md";

    /// <summary>
    /// ★ 无 BOM 的 UTF-8。
    /// .NET 的 <c>Encoding.UTF8</c> 默认**会写 BOM**（EF BB BF），而任务包是写给
    /// **外部工具**（Agent / Python / node / 各种 JSON 解析器）读的 —— 带 BOM 时
    /// 很多解析器会直接报错（如 Python `json.load` 抛 "Unexpected UTF-8 BOM"）。
    /// 2026-10-01 实机验证查出 `agent-request.json` 就是带 BOM 的，故统一改用这个。
    /// </summary>
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(false);

    private static async Task<AgentResult> CallByFileAsync(
        AgentRequest req, Action<AgentProgress>? onProgress, CancellationToken ct,
        Func<string>? watchDetail = null)
    {
        if (string.IsNullOrEmpty(req.WorkDir))
            return new AgentResult { Ok = false, Channel = "file", Error = "没有指定任务包目录（要先打开项目）" };

        string id = Guid.NewGuid().ToString("N").Substring(0, 12);

        // 文件名可以按任务覆盖（单镜重渲用 agent-reshoot.json，见 AgentRequest.RequestFileName）
        string reqName = string.IsNullOrWhiteSpace(req.RequestFileName) ? RequestFile : req.RequestFileName;
        string resName = string.IsNullOrWhiteSpace(req.ResponseFileName) ? ResponseFile : req.ResponseFileName;

        string reqPath = Path.Combine(req.WorkDir, reqName);
        string resPath = Path.Combine(req.WorkDir, resName);

        try
        {
            Directory.CreateDirectory(req.WorkDir);

            // ★ 先把旧的回包删掉 —— 否则这次可能读到上一次的结果，
            //   表现为"我明明改了要求，回填的还是老内容"。
            if (File.Exists(resPath)) File.Delete(resPath);

            var packet = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["kind"] = req.Kind,
                ["title"] = req.Title,
                ["createdAt"] = DateTime.Now.ToString("o"),
                ["status"] = "pending",
                ["system"] = req.SystemPrompt,
                ["user"] = req.UserPrompt,
                ["meta"] = req.Meta,
            };

            File.WriteAllText(reqPath, JsonSerializer.Serialize(packet, new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

            // 顺手写一份人能读的提示词，方便直接查看/手动粘给别的 Agent。
            //   名字可以按任务覆盖（重渲用 agent-reshoot-prompt.md），
            //   免得两份任务的人读版互相覆盖。
            string promptName = !string.IsNullOrWhiteSpace(req.PromptFileName)
                ? req.PromptFileName
                : PromptFile;

            File.WriteAllText(Path.Combine(req.WorkDir, promptName),
                "# " + req.Title + "\n\n## 系统提示词\n\n" + req.SystemPrompt + "\n\n## 任务\n\n" + req.UserPrompt,
                Encoding.UTF8);

            // ★ 任务一：best-effort 自动唤醒。桥接可达 → 唤醒 Agent；不可达 → 提示手动领任务。
            var wake = await WakeViaBridgeAsync(req, ct).ConfigureAwait(false);
            onProgress?.Invoke(new AgentProgress
            {
                Stage = "等待 Agent 产出",
                Detail = wake.Ok
                    ? "任务包已写出，已通过桥接自动唤醒 Agent，等待产出…"
                    : "任务包已写出：" + req.WorkDir + (wake.Error.Length > 0 ? "（" + wake.Error + "）" : ""),
            });

            var deadline = DateTime.Now.AddMinutes(Math.Max(1, req.WaitMinutes));
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (File.Exists(resPath))
                {
                    string text = File.ReadAllText(resPath, Encoding.UTF8);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(text);
                            var root = doc.RootElement;

                            string rid = root.TryGetProperty("id", out var v) ? v.GetString() ?? "" : "";
                            string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                            string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";

                            // id 对不上 = 上一次任务留下的旧回包，跳过继续等
                            if (rid.Length > 0 && rid != id) { /* 继续等 */ }
                            else if (status == "error")
                            {
                                Cleanup(reqPath, resPath);
                                return new AgentResult { Ok = false, Channel = "file", Error = error };
                            }
                            else if (status == "done" || content.Length > 0)
                            {
                                Cleanup(reqPath, resPath);
                                return new AgentResult { Ok = true, Channel = "file", Content = content };
                            }
                        }
                        catch
                        {
                            // 回包还没写完（写到一半就被我们读了）—— 再等一轮
                        }
                    }
                }

                int sec = (int)sw.Elapsed.TotalSeconds;

                // 实时进度钩子（渲染：数输出目录里的 MP4 + 读 Agent 写的进度文件）。
                //   钩子在后台线程跑，只做文件 IO，绝不能碰界面。
                string extra = "";
                try { extra = watchDetail?.Invoke() ?? ""; } catch { /* 钩子坏了不影响等待 */ }

                onProgress?.Invoke(new AgentProgress
                {
                    Stage = "等待 Agent 产出",
                    Detail = $"已等 {sec / 60}分{sec % 60:00}秒 —— 任务包在 {Path.GetFileName(req.WorkDir)}"
                             + (extra.Length > 0 ? " · " + extra : ""),
                });

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            Cleanup(reqPath, resPath);
            return new AgentResult { Ok = false, Channel = "file", Error = $"等了 {req.WaitMinutes} 分钟没等到回包（可能 Agent 没在跑）" };
        }
        catch (OperationCanceledException)
        {
            Cleanup(reqPath, resPath);
            return new AgentResult { Ok = false, Channel = "file", Error = "已取消" };
        }
        catch (Exception ex)
        {
            return new AgentResult { Ok = false, Channel = "file", Error = ex.Message };
        }
    }

    private static void Cleanup(string reqPath, string resPath)
    {
        try { if (File.Exists(reqPath)) File.Delete(reqPath); } catch { }
        try { if (File.Exists(resPath)) File.Delete(resPath); } catch { }
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
