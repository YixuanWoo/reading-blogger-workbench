// ============================================================
// RenderTaskPackage.cs - 渲染任务包（render_tasks.json）的唯一写入点
// ============================================================
//
// 【为什么抽出来】
//   这份 JSON 原来只由渲染页（Step3RenderView.GenerateTaskPackage）写。
//   V1.1.0 起分镜页交接镜头表时也要重写它（镜号变了，任务表必须跟着变，
//   否则外部渲染执行者拿着一份对不上的表去渲，又是"静默错位"）。
//   抄一份到分镜页就是两处各写一遍 —— 字段一改两边走偏
//   （T-10 那次"同一个 json 两个页面写出两个值"就是这么来的）。
//
// 【它写在哪两份】
//   <渲染产物>\render_tasks.json   ← 渲染执行者读的（引擎目录/输出目录那份）
//   stages\04-render\render-tasks.json ← 工作台内部/留档那份
//   两份内容完全一样，必须一起写。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LiteCut.Workbench;

/// <summary>渲染任务包的写入。</summary>
public static class RenderTaskPackage
{
    public const string FileName = "render_tasks.json";

    /// <summary>
    /// ★ 无 BOM 的 UTF-8。任务包是写给 **Agent / 外部渲染脚本** 读的，
    /// .NET 默认的 <c>Encoding.UTF8</c> 会写 BOM（EF BB BF），很多 JSON 解析器会直接报错。
    /// （2026-10-01 实机验证：任务包文件曾带 BOM，读的时候得用 utf-8-sig 才认。）
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// 按当前镜头表重写任务包。返回实际写入的主文件路径（失败返回空串）。
    /// </summary>
    /// <param name="shots">要渲的镜头（★ 传的是**已交接版**，不是草稿）</param>
    /// <param name="outDir">渲染产物目录</param>
    /// <param name="stageDir">stages\04-render</param>
    /// <param name="cardExists">"这一镜的卡片底稿要不要生成"由调用方决定（渲染页会生成，交接时不生成）</param>
    public static string Write(IReadOnlyList<ShotRow> shots, RenderSpec spec,
                               string outDir, string stageDir,
                               string projName, double audioDurationSec,
                               string assetsDir = "")
    {
        try
        {
            Directory.CreateDirectory(outDir);

            var tasks = shots.Select(m => (object)new
            {
                index = m.Index,
                id = m.Id,
                startSec = Math.Round(m.StartSec, 3),
                endSec = Math.Round(m.EndSec, 3),
                durationSec = Math.Round(m.DurationSec, 3),
                script = m.Text,
                visualIdea = m.Description,
                motion = m.Motion,
                imageNeed = m.Image,
                imageFile = m.ImageFile,

                // ---- 插片（V1.10.0）----
                // insert = 这一段是用户手动插进来的：没有口播，script 是空的。
                // videoFile 是项目素材库里的相对名，videoPath 是它在磁盘上的**绝对路径** ——
                //   给绝对路径是为了让 Agent 能直接拷进工作区用，不用回头猜素材库在哪。
                //
                // ★ V1.10.15 / T-25：插片原声 —— silent 不再恒等于 insert。
                //   用户拨了「素材原声」开关（insertAudio=true）时，这一段要把素材的声音录进产物：
                //     · silent=false  → 别再写 data-volume="0"；
                //     · audioVolumeDb = 目标音量（dB，给人看）；
                //     · audioVolume   = 同一个音量的**线性倍数**（给 HTML 直接用，省得 Agent 做算术）。
                insert = m.IsInsert,
                silent = m.IsInsert && !m.InsertAudio,
                insertAudio = m.InsertAudio,
                audioVolumeDb = Math.Round(m.InsertVolumeDb, 2),
                audioVolume = m.InsertAudio
                    ? Math.Round(Math.Pow(10, m.InsertVolumeDb / 20.0), 4)
                    : 0.0,
                videoFile = m.VideoFile,
                videoDurationSec = Math.Round(m.VideoDurationSec, 2),
                videoPath = (m.IsInsert && m.VideoFile.Length > 0 && assetsDir.Length > 0)
                    ? Path.Combine(assetsDir, m.VideoFile.Replace('/', Path.DirectorySeparatorChar))
                    : "",

                source = $"shot_{m.Index:D3}.html",
                output = $"shot_{m.Index:D3}.mp4",
            }).ToList();

            string json = JsonSerializer.Serialize(new
            {
                project = projName,
                audioDurationSec = Math.Round(audioDurationSec, 3),
                generatedAt = DateTime.Now.ToString("o"),
                engine = spec.Engine,
                engineLabel = RenderEngines.Label(spec.Engine),
                canvas = new { width = spec.Width, height = spec.Height, fps = spec.Fps },
                backgroundImage = spec.Background,
                backgroundExists = spec.BackgroundExists,
                templateDir = spec.TemplateDir,
                engineProjectDir = spec.EngineProjectDir,
                renderStageDir = stageDir,
                outputDir = outDir,
                safetyFrames = spec.SafetyFrames,
                note = "每个镜头按 canvas 出 MP4，时长严格等于 durationSec + safetyFrames；" +
                       "画面在 backgroundImage 之上（cover 居中铺满）。index 会随拆合重排，" +
                       "id 是镜头的永久身份（对账用，别拿它命名文件）。" +
                       "insert=true 的是插片：没有口播、script 为空，按 visualIdea/motion 设计；" +
                       "videoPath 非空时可以把这段视频拷进工作区 assets/，用 <video> 铺在画面里。" +
                       "★ 插片音量看 silent 字段：silent=true（默认）写 data-volume=\"0\" 静音；" +
                       "silent=false（用户开了「素材原声」）写 data-volume=\"<audioVolume>\" + data-has-audio=\"true\"， " +
                       "audioVolume 是线性倍数（0dB=1、-6dB=0.5），audioVolumeDb 是同一个值的 dB 形式。",
                tasks,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            string main = Path.Combine(outDir, FileName);
            // ★ 无 BOM：这个文件是**给 Agent / 外部渲染脚本读**的，带 BOM 会让不少解析器直接报错。
            File.WriteAllText(main, json, Utf8NoBom);

            try
            {
                Directory.CreateDirectory(stageDir);
                File.Copy(main, Path.Combine(stageDir, "render-tasks.json"), overwrite: true);
            }
            catch { /* 留档那份写不进去不影响渲染 */ }

            return main;
        }
        catch
        {
            return "";
        }
    }
}
