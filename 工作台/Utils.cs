// ============================================================
// Utils.cs - 公用函数（全程序最"杂"但也最好懂的文件）
//
// 对应你的 Python 习惯：这个文件就是那个"单独放公共函数的 py 文件"。
// 设计原则（你提的要求）：
//   - 每个方法都是 static（静态）方法：不需要创建对象就能直接调用，
//     例如 Utils.Clamp(x, 0, 10)，类似 Python 的模块级函数。
//   - 方法之间尽量不互相调用：每个函数是独立单元，
//     改一个不影响另一个，未来拓展/复用都方便。
// ============================================================

using System;              // 基础类型
using System.Collections.Generic; // List<T>
using System.Net;          // IPAddress 等
using System.Net.NetworkInformation; // 网络接口枚举
using System.Net.Sockets;  // 网络相关类型（AddressFamily 等）
using System.Text;         // 字符串编码（网页 HTML 需要 UTF-8）

namespace LiteCut;

/// <summary>
/// 公用工具类。所有方法都是静态的，直接 Utils.方法名() 调用。
/// </summary>
public static class Utils
{
    /// <summary>
    /// 把秒数格式化成 "分:秒.毫秒"，例如 125.4567 秒 → "02:05.456"。
    /// 用于界面状态栏显示时间。
    /// </summary>
    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int totalMs = (int)Math.Round(seconds * 1000.0);
        int ms = totalMs % 1000;
        int totalSec = totalMs / 1000;
        int sec = totalSec % 60;
        int min = totalSec / 60;
        return $"{min:00}:{sec:00}.{ms:000}";
    }

    /// <summary>
    /// 生成时间戳字符串，例如 "20260919_203045"。
    /// 用于上传的音频自动命名，保证文件名不重复、能看出时间。
    /// </summary>
    public static string CreateTimestamp()
    {
        // DateTime.Now 是当前本地时间；ToString 的自定义格式见注释
        return DateTime.Now.ToString("yyyyMMdd_HHmmss");
    }

    /// <summary>
    /// 把数值夹在 [min, max] 之间。
    /// 比如 Clamp(15, 0, 10) = 10；Clamp(-3, 0, 10) = 0。
    /// 用于防止用户把音量滑块拖出范围、防止选区越界等。
    /// </summary>
    public static long Clamp(long value, long min, long max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// 浮点版夹取（逻辑同上，只是类型不同）。
    /// </summary>
    public static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// 分贝(dB)换算成线性增益倍数。
    /// 音量滑块用 dB 显示（-20 ~ +20），但音频数据里要乘的是线性倍数：
    ///   0 dB = 不变(×1.0)
    ///   +6 dB = 约 ×2.0（声音大一倍）
    ///   -6 dB = 约 ×0.5（声音小一半）
    /// 公式：倍数 = 10 的 (db/20) 次方
    /// </summary>
    public static double DbToGain(double db)
    {
        return Math.Pow(10.0, db / 20.0);
    }

    /// <summary>
    /// 线性增益倍数换算成分贝（DbToGain 的反向）。
    /// 目前界面只用了 DbToGain，这个留着备用（对称的独立函数）。
    /// </summary>
    public static double GainToDb(double gain)
    {
        if (gain <= 0) return -100.0; // 防止对 0 取对数
        return 20.0 * Math.Log10(gain);
    }

    /// <summary>
    /// 确保某个文件夹存在；不存在就创建它。
    /// 素材库目录、项目目录在启动时都会调用它。
    /// </summary>
    public static void EnsureDirectory(string path)
    {
        // Directory.CreateDirectory 在"已存在"时不会报错，可以放心调用
        System.IO.Directory.CreateDirectory(path);
    }

    /// <summary>
    /// 获取本机局域网 IPv4 地址列表。
    /// 用于告诉手机"打开哪个网址上传音频"。
    /// 只返回局域网地址（192.168.x.x / 10.x.x.x / 172.16~31.x.x），
    /// 过滤掉回环地址(127.0.0.1)和虚拟网卡地址。
    /// </summary>
    public static List<string> GetLocalIPv4Addresses()
    {
        var result = new List<string>();

        // 遍历本机所有网络接口（Wi-Fi、网线、虚拟网卡……）
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 只关心"已连接"的接口，跳过断开的
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            // 拿到该接口下所有 IP 配置
            var props = nic.GetIPProperties();
            foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                // 只取 IPv4（IPv6 地址手机用起来麻烦）
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                byte[] bytes = addr.Address.GetAddressBytes();
                // 常见的局域网地址段：
                //   192.168.x.x    家用路由器默认段
                //   10.x.x.x       大网段
                //   172.16~31.x.x  中网段
                bool isPrivate = (bytes[0] == 10) ||
                                 (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                                 (bytes[0] == 192 && bytes[1] == 168);
                if (isPrivate)
                {
                    result.Add(addr.Address.ToString());
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 把文件名里的非法字符替换成下划线。
    /// Windows 文件名不能含 \ / : * ? " &lt; &gt; |，手机上传的文件名
    /// 可能带特殊字符，入库前必须清理。
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "audio.m4a";
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            // 是合法字符就保留，非法字符换成 _
            bool isInvalid = false;
            foreach (char bad in invalid)
            {
                if (c == bad) { isInvalid = true; break; }
            }
            sb.Append(isInvalid ? '_' : c);
        }
        string result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "audio.m4a" : result;
    }

    /// <summary>
    /// 组合函数：给上传的音频生成入库文件名。
    /// 规则：时间戳_原文件名，例如 "20260919_203045_我的录音.m4a"。
    /// 它调用了上面两个独立函数（CreateTimestamp + SanitizeFileName），
    /// 两个被调函数各自独立，不影响它们单独使用。
    /// </summary>
    public static string MakeUploadFileName(string originalName)
    {
        return CreateTimestamp() + "_" + SanitizeFileName(originalName);
    }

    /// <summary>
    /// 生成手机端上传页面（一段完整的 HTML 字符串）。
    /// 手机用 Safari 打开局域网地址时看到的就是这个页面：
    /// 一个"选择音频"按钮 + 一个上传按钮。
    /// 整个页面是内嵌字符串，不依赖任何外部文件，方便维护。
    /// </summary>
    public static string BuildUploadPageHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>工作台 · 素材上传</title>
<style>
  body { background:#14161A; color:#E8E6E3; font-family:-apple-system,sans-serif;
         display:flex; min-height:100vh; margin:0; align-items:center; justify-content:center; }
  .card { background:#1D2128; border:1px solid #333A45; border-radius:14px;
          padding:32px; width:min(90vw,420px); text-align:center; }
  h1 { font-size:20px; color:#F0D68A; margin:0 0 8px; }
  p { font-size:13px; color:#B9B4A8; margin:0 0 24px; }
  input[type=file] { display:block; margin:0 auto 16px; font-size:15px; color:#E8E6E3; }
  button { background:#D4AF37; color:#14161A; border:none; border-radius:8px;
           font-size:16px; padding:10px 28px; font-weight:bold; }
  button:disabled { opacity:.5; }
  .ok { color:#7BD88F; font-size:14px; margin-top:14px; min-height:20px; }
</style>
</head>
<body>
<div class=""card"">
  <h1>工作台 · 素材上传</h1>
  <p>选择 iPhone 里的录音（m4a），上传后自动进入电脑素材库</p>
  <input type=""file"" id=""f"" accept="".m4a,audio/mp4,audio/x-m4a,audio/*"">
  <button id=""btn"" onclick=""doUpload()"">上传</button>
  <div class=""ok"" id=""msg""></div>
</div>
<script>
async function doUpload() {
  var f = document.getElementById('f');
  var msg = document.getElementById('msg');
  if (!f.files.length) { msg.textContent = '请先选择音频文件'; return; }
  var btn = document.getElementById('btn');
  btn.disabled = true;
  msg.textContent = '上传中…';
  try {
    var form = new FormData();
    form.append('file', f.files[0]);
    var r = await fetch('/upload', { method: 'POST', body: form });
    if (r.ok) { msg.textContent = '上传成功 ✓'; }
    else { msg.textContent = '上传失败：' + (await r.text()); }
  } catch (e) {
    msg.textContent = '上传失败：请确认电脑端软件在运行';
  }
  btn.disabled = false;
}
</script>
</body>
</html>";
    }
}
