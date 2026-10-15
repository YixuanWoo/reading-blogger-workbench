// ============================================================
// UploadServer.cs - 局域网上传服务器（iPhone → 电脑素材库）
//
// 做什么：在你电脑上开一个极小的网页服务。
//   手机连同一个 WiFi，用 Safari 打开 http://电脑IP:8124
//   会看到一个"选择音频→上传"的页面；
//   选好 iPhone 录音点上传，文件自动存进素材库文件夹。
//
// 为什么不用现成的 HttpListener？
//   HttpListener 监听局域网地址需要管理员权限注册 URL 白名单，
//   普通用户跑不起来。这里用 TcpListener 手写一个"够用就好"的
//   HTTP 服务：只支持两个请求——
//     GET  /         返回上传页面（HTML）
//     POST /upload   接收文件并保存
//   代码直白、无外部依赖，你改起来也容易。
//
// 对应你的 Python 习惯：类似你用过的 serve.py，只是多了
// 文件接收功能。
// ============================================================

using System;                    // 基础类型
using System.Collections.Generic; // Dictionary
using System.IO;                 // 文件操作
using System.Net;                // IPAddress
using System.Net.Sockets;        // TcpListener / TcpClient / NetworkStream
using System.Text;               // Encoding（字节↔文本）
using System.Threading;          // Thread

namespace LiteCut;

/// <summary>新文件上传完成的事件参数</summary>
public class FileUploadedEventArgs : EventArgs
{
    /// <summary>保存后的完整路径</summary>
    public string FilePath = "";

    /// <summary>保存后的文件名（带时间戳前缀）</summary>
    public string FileName = "";
}

/// <summary>
/// 局域网上传服务器。
/// 用法：new UploadServer(素材库路径) → Start() → 就绪。
/// 新文件入库后触发 FileUploaded 事件。
/// </summary>
public class UploadServer : IDisposable
{
    private readonly string _libraryDir;  // 素材保存目录
    private TcpListener? _listener;       // 监听器（null = 没启动）
    private Thread? _thread;              // 监听线程（后台线程，随程序退出）
    private volatile bool _running;       // 运行开关（volatile = 多线程可见）
    private const long MaxUploadBytes = 200L * 1024 * 1024; // 单文件上限 200MB

    /// <summary>实际监听端口（启动失败重试时会变）</summary>
    public int Port { get; private set; }

    /// <summary>服务是否在运行</summary>
    public bool IsRunning { get; private set; }

    /// <summary>新文件上传完成（MainWindow 订阅它，刷新素材库提示）</summary>
    public event EventHandler<FileUploadedEventArgs>? FileUploaded;

    /// <summary>构造：指定素材保存目录</summary>
    public UploadServer(string libraryDir)
    {
        _libraryDir = libraryDir;
        Utils.EnsureDirectory(libraryDir);
    }

    // ============================================================
    // 启动 / 停止
    // ============================================================

    /// <summary>
    /// 启动服务：从 8124 端口开始尝试，被占用就顺延。
    /// 成功返回 true；全被占用返回 false（概率极低）。
    /// </summary>
    public bool Start()
    {
        // 顺延尝试一串端口（避开你 serve.py 用的 8123）
        for (int p = 8124; p <= 8143; p++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, p); // 监听所有网卡
                _listener.Start();
                Port = p;
                IsRunning = true;

                _thread = new Thread(ListenLoop) { IsBackground = true };
                _thread.Start();
                return true;
            }
            catch
            {
                // 这个端口被占，试下一个
            }
        }
        IsRunning = false;
        return false;
    }

    /// <summary>停止服务（程序退出时调用）</summary>
    public void Stop()
    {
        _running = false;
        IsRunning = false;
        try { _listener?.Stop(); } catch { /* 已停止则忽略 */ }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        Stop();
    }

    // ============================================================
    // 监听线程：循环等待手机连接
    // ============================================================

    /// <summary>后台线程主体：一直等新连接，来一个处理一个</summary>
    private void ListenLoop()
    {
        _running = true;
        while (_running && _listener != null)
        {
            try
            {
                TcpClient client = _listener.AcceptTcpClient();
                // 丢给线程池处理，自己立刻回去等下一个连接
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
            catch
            {
                break; // 监听器被关闭（Stop 时）或出错，结束循环
            }
        }
    }

    // ============================================================
    // 处理一个连接（一个手机请求）
    // ============================================================

    /// <summary>处理单个请求：读请求头 → 按方法分发给 GET/POST</summary>
    private void HandleClient(object? state)
    {
        var client = (TcpClient)state!;
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            try
            {
                stream.ReadTimeout = 15000;

                // 1. 读请求行，例如：POST /upload HTTP/1.1
                string? requestLine = ReadLine(stream);
                if (requestLine == null) return;
                string[] parts = requestLine.Split(' ');
                string method = parts.Length > 0 ? parts[0] : "";
                string path = parts.Length > 1 ? parts[1] : "/";

                // 2. 读所有请求头（直到空行），存进字典
                var headers = new Dictionary<string, string>();
                while (true)
                {
                    string? line = ReadLine(stream);
                    if (line == null) break;
                    if (line.Length == 0) break; // 空行 = 头部结束
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        string key = line.Substring(0, colon).Trim();
                        string value = line.Substring(colon + 1).Trim();
                        headers[key] = value;
                    }
                }

                // 3. 分发
                if (method == "GET" && path == "/")
                {
                    SendResponse(stream, 200, "text/html", Utils.BuildUploadPageHtml());
                }
                else if (method == "POST" && path == "/upload")
                {
                    HandleUpload(stream, headers);
                }
                else
                {
                    SendResponse(stream, 404, "text/plain", "not found");
                }
            }
            catch (Exception ex)
            {
                // 请求解析失败：尽力回一句，然后断开
                try { SendResponse(stream, 400, "text/plain", "请求错误: " + ex.Message); }
                catch { /* 连接可能已断开，忽略 */ }
            }
        }
    }

    // ============================================================
    // POST /upload：解析上传并保存
    // ============================================================

    /// <summary>处理文件上传：按 Content-Length 读完整数据，解析 multipart，保存</summary>
    private void HandleUpload(NetworkStream stream, Dictionary<string, string> headers)
    {
        // 1. 提取 Content-Length（上传数据总字节数）
        string lengthText = headers.TryGetValue("Content-Length", out var cl) ? cl : "0";
        int contentLength = int.TryParse(lengthText, out int n) ? n : 0;

        // 2. 提取 multipart 的 boundary（分隔标记）
        string contentType = headers.TryGetValue("Content-Type", out var ct) ? ct : "";
        int bi = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        string boundary = bi >= 0 ? contentType.Substring(bi + 9).Trim('"', ' ') : "";

        // 3. 基础校验
        if (contentLength <= 0 || boundary.Length == 0)
        {
            SendResponse(stream, 400, "text/plain", "无效请求：缺少文件数据");
            return;
        }
        if (contentLength > MaxUploadBytes)
        {
            SendResponse(stream, 400, "text/plain", "文件太大（超过 200MB）");
            return;
        }

        // 4. 把整个 body 读进内存（手机录音一般几十 MB，安全）
        byte[] body = new byte[contentLength];
        int readTotal = 0;
        while (readTotal < contentLength)
        {
            int r = stream.Read(body, readTotal, contentLength - readTotal);
            if (r <= 0) break;
            readTotal += r;
        }
        if (readTotal < contentLength) Array.Resize(ref body, readTotal);

        // 5. 解析 multipart 得到文件名和文件数据
        try
        {
            var (fileName, data) = ParseMultipart(body, boundary);
            if (data.Length == 0)
            {
                SendResponse(stream, 400, "text/plain", "没有收到文件内容");
                return;
            }

            // 6. 生成入库文件名（时间戳_原名）并保存
            string savedName = Utils.MakeUploadFileName(Path.GetFileName(fileName));
            string savePath = Path.Combine(_libraryDir, savedName);
            File.WriteAllBytes(savePath, data);

            // 7. 通知外面（MainWindow 刷新素材库）
            FileUploaded?.Invoke(this, new FileUploadedEventArgs
            {
                FilePath = savePath,
                FileName = savedName
            });

            SendResponse(stream, 200, "text/plain", "OK 已保存：" + savedName);
        }
        catch (Exception ex)
        {
            SendResponse(stream, 400, "text/plain", "解析失败：" + ex.Message);
        }
    }

    // ============================================================
    // multipart 解析（浏览器上传的标准格式）
    // ============================================================

    /// <summary>
    /// 从 multipart 报文里取出"文件名"和"文件数据"。
    /// multipart 的结构（浏览器自动生成）：
    ///
    ///   --boundary\r\n
    ///   Content-Disposition: form-data; name="file"; filename="xx.m4a"\r\n
    ///   Content-Type: audio/x-m4a\r\n
    ///   \r\n
    ///   <这里是文件二进制数据>\r\n
    ///   --boundary--\r\n
    ///
    /// 我们要做的：找到第一个 \r\n\r\n（头和数据的分界），
    /// 数据到下一个 --boundary 之前结束。
    /// </summary>
    private static (string fileName, byte[] data) ParseMultipart(byte[] body, string boundary)
    {
        byte[] delim = Encoding.ASCII.GetBytes("--" + boundary); // 分隔线

        // 找到第一个分隔线的位置（part 头从这里开始）
        int partStart = IndexOf(body, delim, 0);
        if (partStart < 0)
            throw new InvalidOperationException("找不到 multipart 分隔符");

        // 找到 \r\n\r\n：头结束、数据开始
        int headerEnd = IndexOf(body, new byte[] { 0x0D, 0x0A, 0x0D, 0x0A }, partStart);
        if (headerEnd < 0)
            throw new InvalidOperationException("找不到数据开始位置");
        int dataStart = headerEnd + 4;

        // 找到下一个分隔线：数据到这里结束
        int nextDelim = IndexOf(body, delim, dataStart);
        int dataEnd = nextDelim < 0 ? body.Length : nextDelim;

        // 去掉数据末尾的 \r\n（分隔线前的换行）
        if (dataEnd >= 2 && body[dataEnd - 2] == 0x0D && body[dataEnd - 1] == 0x0A)
            dataEnd -= 2;

        // 拷贝文件数据
        byte[] data = new byte[dataEnd - dataStart];
        Array.Copy(body, dataStart, data, 0, data.Length);

        // 从 part 头部文本里找 filename="..."
        string headerText = Encoding.UTF8.GetString(body, partStart, headerEnd - partStart);
        string fileName = ExtractFileName(headerText);

        return (fileName, data);
    }

    /// <summary>从 part 头文本里提取 filename="xxx" 的值</summary>
    private static string ExtractFileName(string headerText)
    {
        const string marker = "filename=\"";
        int i = headerText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "upload.m4a";
        int start = i + marker.Length;
        int end = headerText.IndexOf('"', start);
        if (end < 0) end = headerText.Length;
        string name = headerText.Substring(start, end - start);
        return string.IsNullOrEmpty(name) ? "upload.m4a" : name;
    }

    /// <summary>在字节数组里查找子数组第一次出现的位置；找不到返回 -1</summary>
    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        int last = haystack.Length - needle.Length;
        for (int i = from; i <= last; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    // ============================================================
    // HTTP 读写小工具
    // ============================================================

    /// <summary>从流里读一行文本（读到 \n 为止，自动去掉 \r）</summary>
    private static string? ReadLine(NetworkStream stream)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = stream.ReadByte()) >= 0)
        {
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        if (sb.Length == 0 && b < 0) return null; // 连接已关闭
        return sb.ToString();
    }

    /// <summary>发送一个 HTTP 响应（状态码 + 内容）</summary>
    private static void SendResponse(NetworkStream stream, int statusCode, string contentType, string body)
    {
        string statusText = statusCode == 200 ? "OK" : "Bad Request";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        // 响应头：状态行 + 类型 + 长度 + 关连接
        string header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n" +
                        "Content-Type: " + contentType + "; charset=utf-8\r\n" +
                        "Content-Length: " + bodyBytes.Length + "\r\n" +
                        "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }
}
