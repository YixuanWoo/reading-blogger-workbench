// ============================================================
// UiRemoteServer.cs - 调试模式下开在本机的"遥控口"
// ============================================================
//
// 【它是给谁用的】
//   给 Agent（也就是我）用的。以前要验一个界面功能，只能：
//     ① 写一个无头工程把页面 new 出来断言 —— 看不见真的界面；
//     ② 或者人肉截图 —— 慢，而且看不见"点了以后发生了什么"。
//   有了这个口子，Agent 可以：
//     GET  /ui/tree      看清现在屏幕上有哪些按钮、输入框、下拉
//     POST /ui/click     真的把某个按钮点下去
//     POST /ui/input     往某个输入框打字
//     POST /ui/toggle    勾选 / 取消勾选
//     POST /ui/select    选下拉 / 列表的第几项
//     POST /ui/nav       跳到某一页
//     GET  /log          读刚才发生了什么的流水账
//   于是"跑起来点一遍"这件事，从"迂回"变成"直接"。
//
// 【为什么自己写 HTTP，不用 HttpListener】
//   HttpListener 在 Windows 上绑 URL 往往要管理员权限或 netsh urlacl。
//   而单文件 exe 是用户双击跑的，弹一个"需要管理员"就废了。
//   TcpListener 绑 127.0.0.1 完全不需要权限 —— 代价只是自己解析几十行 HTTP。
//
// 【安全边界（很重要）】
//   · 只绑 127.0.0.1 —— 外网 / 局域网都连不进来
//   · 只在调试模式打开时才监听，关掉立刻停
//   · 端口可从设置里改（默认 18766，避开桥接的 18765）
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace LiteCut.Workbench.Diagnostics;

public sealed class UiRemoteServer
{
    private readonly WorkbenchWindow _window;
    private readonly int _port;

    private TcpListener? _listener;
    private volatile bool _running;
    private string _endpoint = "";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>正在监听吗。</summary>
    public bool IsRunning => _running;

    /// <summary>形如 http://127.0.0.1:18766/（没开时为空串）。</summary>
    public string Endpoint => _endpoint;

    /// <summary>上次启动失败的原因（端口被占之类）。</summary>
    public string? LastError { get; private set; }

    public UiRemoteServer(WorkbenchWindow window, int port)
    {
        _window = window;
        _port = port <= 0 || port > 65535 ? 18766 : port;
    }

    // ========================================================
    // 开关
    // ========================================================

    public bool Start()
    {
        if (_running) return true;

        try
        {
            var l = new TcpListener(IPAddress.Loopback, _port);
            l.Start();
            _listener = l;
            _running = true;
            _endpoint = $"http://127.0.0.1:{_port}/";
            LastError = null;

            var th = new Thread(AcceptLoop) { IsBackground = true, Name = "LiteCut-UiRemote" };
            th.Start();

            DebugLog.Event("遥控口已打开", new { endpoint = _endpoint });
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _endpoint = "";
            DebugLog.Error($"遥控口打开失败（端口 {_port}）", ex);
            return false;
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        try { _listener?.Stop(); } catch { }
        _listener = null;
        _endpoint = "";

        DebugLog.Event("遥控口已关闭");
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch
            {
                break;   // 监听器被关掉 → 正常退出
            }

            ThreadPool.QueueUserWorkItem(_ => ServeSafe(client));
        }
    }

    private void ServeSafe(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 15000;
                Serve(client);
            }
        }
        catch
        {
            // 客户端半路断开是常事，不值得记
        }
    }

    // ========================================================
    // 一个请求的生命周期
    // ========================================================

    private void Serve(TcpClient client)
    {
        NetworkStream ns = client.GetStream();

        // ---- 读请求头 ----
        var head = new MemoryStream();
        var one = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int n = ns.Read(one, 0, one.Length);
            if (n <= 0) return;
            head.Write(one, 0, n);
            if (head.Length > 512 * 1024) return;    // 头不可能这么大 → 拒绝

            byte[] cur = head.ToArray();
            headerEnd = IndexOfCrLfCrLf(cur);
        }

        byte[] all = head.ToArray();
        string headText = Utf8NoBom.GetString(all, 0, headerEnd);
        string[] lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return;

        // 请求行：GET /ui/tree?x=1 HTTP/1.1
        string[] req = lines[0].Split(' ');
        if (req.Length < 2) return;
        string method = req[0].ToUpperInvariant();
        string target = req[1];

        // ---- Content-Length ----
        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            if (lines[i][..c].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(lines[i][(c + 1)..].Trim(), out contentLength);
        }

        // ---- 读 body（可能已经在缓冲区里，也可能还没到）----
        int bodyStart = headerEnd + 4;
        var body = new MemoryStream();
        int already = all.Length - bodyStart;
        if (already > 0) body.Write(all, bodyStart, already);

        while (body.Length < contentLength)
        {
            int n = ns.Read(one, 0, Math.Min(one.Length, contentLength - (int)body.Length));
            if (n <= 0) break;
            body.Write(one, 0, n);
        }

        string bodyText = Utf8NoBom.GetString(body.ToArray());

        // ---- 路由 ----
        string path = target;
        string query = "";
        int q = target.IndexOf('?');
        if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }

        int status = 200;
        string payload;

        try
        {
            payload = Route(method, path, query, bodyText, out status);
        }
        catch (Exception ex)
        {
            status = 500;
            payload = Json(new { ok = false, error = ex.Message, type = ex.GetType().Name });
            DebugLog.Error("遥控口处理请求出错 " + path, ex);
        }

        WriteResponse(ns, status, payload);
    }

    private static int IndexOfCrLfCrLf(byte[] b)
    {
        for (int i = 0; i + 3 < b.Length; i++)
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    private void WriteResponse(NetworkStream ns, int status, string body)
    {
        byte[] bytes = Utf8NoBom.GetBytes(body);
        string head =
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: *\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        ns.Write(headBytes, 0, headBytes.Length);
        ns.Write(bytes, 0, bytes.Length);
        ns.Flush();
    }

    // ========================================================
    // 路由
    // ========================================================

    private string Route(string method, string path, string query, string body, out int status)
    {
        status = 200;

        switch (path)
        {
            case "/":
            case "/health":
                return Json(new
                {
                    ok = true,
                    app = "翊瑄的工作台",
                    debug = true,
                    port = _port,
                    endpoint = _endpoint,
                    pid = Environment.ProcessId,
                    project = WorkbenchWindow.Session?.Current?.Model?.Name,
                    page = OnUi(() => _window.CurrentNavId),
                    logFile = DebugLog.FilePath,
                    logSeq = DebugLog.Seq,
                    usage = new[]
                    {
                        "GET  /ui/tree?filter=文字     列出当前界面上所有可操作控件",
                        "GET  /ui/text?filter=文字     把界面上写着的字按顺序读出来（不用截图）",
                        "POST /ui/click   {id|label|type, index}   点一个按钮/勾选框",
                        "POST /ui/input   {…, text}                往输入框打字",
                        "POST /ui/toggle  {…, value}               勾选/取消勾选",
                        "POST /ui/select  {…, index|value}         选下拉或列表项",
                        "POST /ui/nav     {id:\"step.shots\"}        跳到某一页",
                        "GET  /log?since=游标&tail=200             读事件流水",
                        "POST /log/clear                           清空内存日志",
                        "GET  /dialog                              看当前有没有弹窗",
                        "POST /dialog     {result:\"ok\"|\"cancel\"|\"yes\"|\"no\"}  关掉弹窗",
                    },
                });

            case "/ui/tree":
                return Tree(query);

            case "/ui/text":
                return TextDump(query);

            case "/ui/click":
                return Act(body, "click");

            case "/ui/input":
                return Act(body, "input");

            case "/ui/toggle":
                return Act(body, "toggle");

            case "/ui/select":
                return Act(body, "select");

            case "/ui/nav":
                return Nav(body);

            case "/log":
                return Log(query);

            case "/log/clear":
                RunUi(() => DebugLog.Clear());
                return Json(new { ok = true, cleared = true });

            case "/dialog":
                return method == "POST" ? DismissDialog(body) : ListDialogs();

            default:
                status = 404;
                return Json(new { ok = false, error = "没有这个接口：" + path });
        }
    }

    // ========================================================
    // /ui/tree
    // ========================================================

    private string Tree(string query)
    {
        var q = ParseQuery(query);
        q.TryGetValue("filter", out string? filter);

        List<UiHit> hits = OnUi(() => UiInspector.Collect()) ?? new List<UiHit>();

        var outList = new List<object>();
        foreach (var h in hits)
        {
            if (!string.IsNullOrWhiteSpace(filter))
            {
                bool match = h.Info.Label.Contains(filter!, StringComparison.OrdinalIgnoreCase)
                          || h.Info.Id.Contains(filter!, StringComparison.OrdinalIgnoreCase)
                          || h.Info.Type.Contains(filter!, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
            }

            // 只列"看得见"的 —— 藏起来的按钮点不了，列出来只会让人白找
            if (!h.Info.Visible) continue;

            outList.Add(new
            {
                id = h.Info.Id,
                type = h.Info.Type,
                label = h.Info.Label,
                enabled = h.Info.Enabled,
                x = h.Info.X,
                y = h.Info.Y,
                w = h.Info.W,
                h = h.Info.H,
                win = h.Info.Window,
            });
        }

        return Json(new
        {
            ok = true,
            page = OnUi(() => _window.CurrentNavId),
            project = WorkbenchWindow.Session?.Current?.Model?.Name,
            count = outList.Count,
            controls = outList,
        });
    }

    // ========================================================
    // /ui/text —— 读一屏字
    // ========================================================

    private string TextDump(string query)
    {
        var q = ParseQuery(query);
        q.TryGetValue("filter", out string? filter);

        var lines = OnUi(() => UiInspector.TextLines(filter)) ?? new List<string>();

        return Json(new
        {
            ok = true,
            page = OnUi(() => _window.CurrentNavId),
            project = WorkbenchWindow.Session?.Current?.Model?.Name,
            count = lines.Count,
            lines,
        });
    }

    // ========================================================
    // /ui/click | /ui/input | /ui/toggle | /ui/select
    // ========================================================

    private string Act(string body, string action)
    {
        JsonElement req;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            req = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = "请求体不是合法 JSON：" + ex.Message });
        }

        string? id = Str(req, "id");
        string? label = Str(req, "label");
        string? type = Str(req, "type");
        int index = Int(req, "index", 0);
        string? text = Str(req, "text");
        string? value = Str(req, "value");
        bool? flag = Bool(req, "value");

        string detail = "";
        string actResult = "";

        var r = OnUi(() =>
        {
            var (hit, d) = UiInspector.Find(id, label, type, index);
            detail = d;
            if (hit == null) return false;

            if (!hit.Info.Enabled)
            {
                actResult = "★ 这个控件是灰的（禁用状态），点不动 —— 一般是前一步还没做完。";
                return false;
            }

            actResult = action switch
            {
                "click" => UiInspector.Invoke(hit.Element),
                "input" => UiInspector.SetText(hit.Element, text ?? ""),
                "toggle" => UiInspector.SetToggle(hit.Element, flag ?? true),
                "select" => UiInspector.SelectIndex(hit.Element, index, value),
                _ => "未知动作",
            };

            return true;
        });

        // 控制器在后台线程，UI 操作完把结果写进日志（写日志本身是线程安全的）
        DebugLog.Write("remote", $"遥控 {action}：" + (r == true ? "成功" : "没做成"), new
        {
            id,
            label,
            type,
            index,
            text,
            value,
            detail,
            result = actResult,
        });

        return Json(new
        {
            ok = r == true,
            action,
            detail,
            result = actResult,
        });
    }

    private string Nav(string body)
    {
        string? navId = Str(ParseJson(body), "id");
        if (string.IsNullOrWhiteSpace(navId))
            return Json(new { ok = false, error = "要带 id，比如 {\"id\":\"step.shots\"}" });

        string? landed = null;

        bool targetExists = OnUi(() =>
        {
            try
            {
                if (Navigation.NavTree.Find(navId!) == null) return false;
                _window.GoTo(navId!);
                landed = _window.CurrentNavId;
                return true;
            }
            catch { return false; }
        }) == true;

        // ★ 不能只看"没抛异常" —— GoTo 遇到"这页不属于当前作用域"是会**安静地什么都不做**的
        //   （只在状态栏说一句）。所以要拿跳完之后实际的页去对，才算真的跳成功了。
        bool ok = targetExists && string.Equals(landed, navId, StringComparison.OrdinalIgnoreCase);

        string? note = null;
        if (!ok && targetExists)
            note = "没跳过去 —— 多半是作用域不对：项目内的页面要先打开一个项目才能进（当前页 " + landed + "）。";

        DebugLog.Write("remote", $"遥控跳页 → {navId}：{(ok ? "成功" : "没跳成")}", new { landed, note });

        return Json(new { ok, nav = navId, page = landed, note });
    }

    // ========================================================
    // /log
    // ========================================================

    private string Log(string query)
    {
        var q = ParseQuery(query);
        long since = 0;
        int tail = 300;
        if (q.TryGetValue("since", out var s)) long.TryParse(s, out since);
        if (q.TryGetValue("tail", out var t)) int.TryParse(t, out tail);

        var list = DebugLog.Snapshot(since, tail);

        return Json(new
        {
            ok = true,
            seq = DebugLog.Seq,
            count = list.Count,
            file = DebugLog.FilePath,
            lines = list,
        });
    }

    // ========================================================
    // 弹窗（MessageBox）处理
    // ========================================================
    //
    // 【为什么需要这个】
    //   MessageBox 是 Win32 原生对话框，不在 WPF 的窗口列表里，
    //   遥控口点不到它 —— 一旦某个流程弹出确认框，后面的遥控就全卡住了。
    //   所以这里直接枚举"本进程的原生窗口"，找到对话框（窗口类 #32770），
    //   用 PostMessage 把对应的按钮按下去。
    //   这样"点按钮 → 弹确认框 → 点确定"整条链路都能自动走完。

    private const int WM_COMMAND = 0x0111;
    private const int IDOK = 1, IDCANCEL = 2, IDYES = 6, IDNO = 7;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>对话框：句柄、标题、正文（正文是它里面那几行 Static 文字）。</summary>
    private sealed class DialogInfo
    {
        public IntPtr Handle;
        public string Title = "";
        public List<string> Texts = new();
    }

    private static List<DialogInfo> FindDialogs()
    {
        var found = new List<DialogInfo>();
        uint me = (uint)Environment.ProcessId;

        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != me) return true;

                    var cls = new StringBuilder(64);
                    GetClassName(h, cls, cls.Capacity);
                    if (cls.ToString() != "#32770") return true;   // #32770 = 标准对话框类

                    if (!IsWindowVisible(h)) return true;

                    var title = new StringBuilder(256);
                    GetWindowText(h, title, title.Capacity);

                    var d = new DialogInfo { Handle = h, Title = title.ToString() };

                    // ★ 把对话框里"写着的字"也读出来 —— 不然只知道弹了个框，
                    //   不知道它问的是什么（"统计日得填一个" vs "没有打开项目"）。
                    EnumChildWindows(h, (ch, _) =>
                    {
                        try
                        {
                            var cc = new StringBuilder(64);
                            GetClassName(ch, cc, cc.Capacity);
                            string clsName = cc.ToString();
                            if (clsName is not ("Static" or "Button")) return true;

                            var txt = new StringBuilder(1024);
                            GetWindowText(ch, txt, txt.Capacity);
                            string s = txt.ToString().Trim();
                            if (s.Length > 0) d.Texts.Add(s);
                        }
                        catch { }
                        return true;
                    }, IntPtr.Zero);

                    found.Add(d);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return found;
    }

    private string ListDialogs()
    {
        var list = FindDialogs();
        DebugLog.Write("remote", $"有 {list.Count} 个弹窗", new
        {
            titles = list.ConvertAll(d => d.Title),
            texts = list.ConvertAll(d => string.Join(" / ", d.Texts)),
        });

        return Json(new
        {
            ok = true,
            count = list.Count,
            dialogs = list.ConvertAll(d => new
            {
                title = d.Title,
                handle = d.Handle.ToInt64(),
                texts = d.Texts,
            }),
        });
    }

    private string DismissDialog(string body)
    {
        string? result = Str(ParseJson(body), "result");
        int cmd = (result ?? "ok").ToLowerInvariant() switch
        {
            "cancel" => IDCANCEL,
            "yes" => IDYES,
            "no" => IDNO,
            _ => IDOK,
        };

        var dialogs = FindDialogs();
        if (dialogs.Count == 0)
            return Json(new { ok = false, error = "当前没有弹窗" });

        foreach (var d in dialogs)
            PostMessage(d.Handle, WM_COMMAND, new IntPtr(cmd), IntPtr.Zero);

        DebugLog.Write("remote", $"遥控关闭弹窗（{result ?? "ok"}）× {dialogs.Count}",
                        new { titles = dialogs.ConvertAll(d => d.Title) });

        return Json(new { ok = true, closed = dialogs.Count, result = result ?? "ok" });
    }

    // ========================================================
    // 工具
    // ========================================================

    /// <summary>把一段活搬到 UI 线程去做（控件只能在自己线程上碰）。</summary>
    private T? OnUi<T>(Func<T> work)
    {
        try
        {
            if (_window.Dispatcher.CheckAccess()) return work();
            return _window.Dispatcher.Invoke(work, System.Windows.Threading.DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            DebugLog.Error("遥控操作在 UI 线程上失败", ex);
            return default;
        }
    }

    private void RunUi(Action work) => OnUi(() => { work(); return true; });

    private static JsonElement ParseJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int def)
    {
        if (!e.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int m)) return m;
        return def;
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.ToLowerInvariant() is "true" or "1" or "yes",
            _ => null,
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return d;

        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            if (eq < 0) d[Uri.UnescapeDataString(pair)] = "";
            else d[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return d;
    }

    private static string Json(object o) =>
        JsonSerializer.Serialize(o, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
