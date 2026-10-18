// ============================================================
// AppConfig.cs - 记住用户的设置
// ============================================================
//
// 【为什么需要】
//   ffmpeg 路径、上次打开哪个素材目录、习惯用哪一档……
//   这些如果每次开程序都要重设，用起来会很难受。
//
//   这是"工作台"和"脚本"的一个本质区别：脚本每次从零开始，
//   工作台得记得住上一次的样子。
//
// 【存在哪】
//   %APPDATA%\翊瑄的工作台\config.json
//   放在用户目录而不是安装目录 —— 免得将来重装程序时把配置冲掉。
// ============================================================

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteCut.Workbench;

/// <summary>
/// 程序配置。新增字段只要在下面加属性就行，读写是自动的。
/// </summary>
public sealed class AppConfig
{
    /// <summary>手动指定的 ffmpeg 路径。留空表示自动搜索。</summary>
    public string FFmpegPath { get; set; } = "";

    /// <summary>上次打开的素材目录。</summary>
    public string LastMediaDir { get; set; } = "";

    /// <summary>
    /// 转写稿落盘目录（**只在没打开项目时兜底**）。
    ///
    /// 【为什么单独拎出这个目录】
    ///   转写稿不是我们自己产的 —— 它是外部 Agent（豆包 / whisper 等）写到磁盘上的。
    ///   没有一个固定约定位置，每次都要满硬盘找它刚生成的文件，
    ///   这一步的烦躁感足以让人放弃用这个功能。
    ///
    /// ★ 2026-10-01 起，稿子的**主位置**是项目夹里的 `stages\01-transcript`
    ///   （见 ProjectRef.TranscriptDir + Workbench\Agent\TranscriptPrompt.cs）——
    ///   项目自包含：整个项目夹拷走，稿子跟着一起走。
    ///   这个字段只在"没打开项目"时才拿来兜底，而且**它指向的目录必须真实存在**
    ///   才会被采用：历史默认值指向 C 盘一个早已删掉的临时目录，
    ///   不检查的话，"复制转写提示词"会指着一个不存在的地方让 Agent 去写。
    /// </summary>
    public string TranscriptDir { get; set; } = "";

    /// <summary>上次选择的粗剪档位：0=激进 1=适中 2=保守。</summary>
    public int LastPreset { get; set; } = 0;

    /// <summary>上次选择的频道的输出目录。</summary>
    public string LastOutputDir { get; set; } = "";

    /// <summary>渲染资产根目录（HyperFrames 引擎，D:\HyperFrames 真实文件夹 —— 2026-10-01 起无任何 junction）。</summary>
    public string RenderAssetRoot { get; set; } = @"D:\HyperFrames";

    // ========================================================
    // 成片画面规格（2026-09-29 用户拍板，所有视频统一）
    // ========================================================
    //
    // 【为什么放在"配置"里，而不是每个项目各存一份】
    //   用户的频道是固定视觉：所有视频都铺同一张背景图，都是横屏。
    //   每做一支视频就重设一遍背景图，属于纯粹的重复劳动；
    //   而且一旦有一支忘了设，成片风格就断了一档 —— 这种错很难当场发现。
    //   所以"底子"是全局的：改一次，以后每支视频都跟着走。
    //   每个项目仍会把当时的规格落一份 render-spec.json 到 stages 下，
    //   那是"这一次渲染到底用了什么"的凭证，方便回看和复现。
    //
    // 【渲染方案】
    //   走 HyperFrames（HTML/CSS/GSAP 编程动效 → MP4），不是 Remotion。
    //   这也意味着：分镜设计的动效必须能用网页技术做出来，
    //   所以这条会一起写进给 Agent 的内置提示词里。

    /// <summary>
    /// 成片背景图（铺在画面最底层，所有镜头共用的兜底设置）。
    /// 2026-10-01 起：优先用项目自己的「素材\公共素材\」里的图（立项时自动从
    /// exe 旁的「素材模板\」复制），这里只是新项目没素材时的兜底，不指向外部固定夹。
    /// 实测这张是 1920×1080，正好 16:9，铺满不形变。
    /// </summary>
    public string VideoBackgroundImage { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "素材模板", "grid-bg-deep-blue-grid.jpg");

    /// <summary>成片画幅：宽（横屏 16:9）。</summary>
    public int VideoWidth { get; set; } = 1920;

    /// <summary>成片画幅：高。</summary>
    public int VideoHeight { get; set; } = 1080;

    /// <summary>成片帧率。</summary>
    public int VideoFps { get; set; } = 30;

    /// <summary>
    /// 渲染方案。取值见 <see cref="RenderEngines"/>，默认 hyperframes。
    /// </summary>
    public string RenderEngine { get; set; } = RenderEngines.HyperFrames;

    /// <summary>HyperFrames 动效模板库（做镜头时从这里挑模板改 CONFIG；引擎本体在 D:\HyperFrames）。</summary>
    public string HyperFramesTemplateDir { get; set; } = @"D:\HyperFrames\templates";

    /// <summary>窗口是否最大化启动。</summary>
    public bool StartMaximized { get; set; } = true;

    // ========================================================
    // ★ V1.18.0 界面主题
    // ========================================================
    //
    // 【为什么要能换】
    //   2026-10-18 用户原话："黑色主题白天看起来费眼睛"。
    //   这套界面从第一版就是深色，因为夜里剪片子舒服；但白天光线足的时候
    //   深底配灰字对比度天然偏低，看久了累。于是做成两套：
    //     夜空黑（默认，原来的样子） / 晨光白（明亮清爽，白底深字）
    //
    // 【存在哪】
    //   存的是主题 Id（"night" / "day"），见 Theming/Theme.cs。
    //   读不认识的 Id 会自动退回夜空黑 —— 所以将来加主题不会让老配置失效。

    /// <summary>界面主题 Id：night = 夜空黑（默认），day = 晨光白。</summary>
    public string ThemeId { get; set; } = "night";

    /// <summary>
    /// 项目根目录。每个项目是它下面的一个子文件夹。
    /// 留空 = 用默认值（exe 同文件夹下的「项目」，见 ProjectStore.FallbackRoot）。
    /// 2026-10-01 拍板：软件用到的文件必须在 exe 旁边 —— 项目数据不散落在外部目录。
    ///
    /// ★ 2026-10-15 V1.14.0 起这个值可以在「设置 → 路径配置」里改
    ///   （以前只能改 config.json 或靠代码默认值）。
    /// </summary>
    public string ProjectRoot { get; set; } = "";

    /// <summary>
    /// 立项素材种子目录。新建项目时把这个目录里的文件复制进项目的
    /// `素材\公共素材\`（现在只有一张标准背景图）。
    ///
    /// 留空 = exe 旁的 `素材模板\`（<see cref="ProjectStore.FallbackSeedDir"/>）。
    /// ★ V1.14.0 之前这里是写死的，设置里改了没地方生效。
    /// </summary>
    public string SeedDir { get; set; } = "";

    // ========================================================
    // ★ V1.14.0 调试模式（给 Agent 用）
    // ========================================================
    //
    // 【调试模式解决什么问题】
    //   改工作台的功能之后，"跑起来点一遍"这一步以前只能靠无头测试工程
    //   或人肉截图 —— 两者都慢、也看不见中间发生了什么。
    //   打开调试模式之后：
    //     ① 界面上所有按钮点击 / 输入 / 选择 / 换页 / 文件增删改，
    //        全部写进一份 JSONL 日志（一行一条，外部程序好解析）；
    //     ② 工作台在 127.0.0.1 上开一个小遥控口，外部（Agent）可以直接
    //        「查控件清单 / 点按钮 / 填输入框 / 勾选 / 选下拉 / 跳页」——
    //        像真人一样操作界面，不必截图也不必猜坐标。
    //
    // 【为什么默认关着】
    //   它会在本机开一个监听端口。虽然是 127.0.0.1（只有本机能连）、
    //   而且每次启动都要显式打开，但"默认不开一个端口"是更稳妥的默认值。

    /// <summary>是否打开调试模式（会开本地遥控口 + 写调试日志）。</summary>
    public bool DebugEnabled { get; set; }

    /// <summary>调试遥控口监听端口（只绑 127.0.0.1）。默认 18766 —— 避开桥接的 18765。</summary>
    public int DebugPort { get; set; } = 18766;

    /// <summary>调试日志是否落文件（<exe目录>\logs\debug_YYYYMMDD.log）。关掉就只在内存里留最近几百条。</summary>
    public bool DebugLogToFile { get; set; } = true;

    // ========================================================
    // ★ V1.13.0 归档（整包搬去移动硬盘）
    // ========================================================
    //
    // 【归档是什么】
    //   项目做完之后点「归档」：整包搬到移动硬盘做归档存储，
    //   本地只留一个小桩（project.json + 文本 + 封面 + 复盘数据），
    //   逐镜 mp4 这类"渲染得出来的画面"本地和硬盘都不留。
    //   目的只有一个：本地电脑保持干净，不随项目增多越来越重。
    //
    // 【为什么落点不做"频道"这一级】
    //   用户 2026-10-15 拍板：项目夹下面直接就是项目名，
    //   `E:\1_创作素材\项目\<项目名>\` 里面就是工程。简单、好找。

    /// <summary>
    /// 归档落点根目录。每个项目是它下面的一个子文件夹（直接是项目名，不再分层）。
    /// 默认 = 文件总库的创作素材项目区。
    /// </summary>
    public string ArchiveRoot { get; set; } = @"E:\1_创作素材\项目";

    /// <summary>
    /// 上次归档用的那块移动硬盘的卷序列号（十六进制）。
    ///
    /// 【为什么必须认盘】
    ///   ① 盘符会漂：插拔、换口、系统里别的盘进来，E: 可能变成 F:、G:；
    ///   ② 硬编码 E: 的风险是"照老路径搬，搬到了别的盘上"——那是灾难。
    ///   卷序列号是分区格式化时写死的，跟盘符无关，是"就是这块盘"的唯一证据。
    ///   留空 = 还没归档过，第一次归档时自动记下。
    /// </summary>
    public string ArchiveVolumeSerial { get; set; } = "";

    /// <summary>上次归档那块盘的卷标。只用于界面上显示（如 "Elements"），不参与判断。</summary>
    public string ArchiveVolumeLabel { get; set; } = "";

    /// <summary>
    /// 上次退出去时停在哪个导航项上（NavTree.Ids 里的值）。
    /// 下次启动落回同一页 —— 剪到一半关了程序，再打开还是那一页。
    /// 找不到/不属于当前作用域时会自动退到该作用域的第一项。
    /// </summary>
    public string LastNavId { get; set; } = "";

    /// <summary>
    /// 上次打开的项目文件夹。启动时如果它还在，就直接接着剪。
    /// 留空 / 文件夹没了 = 启动落在项目列表上。
    /// </summary>
    public string LastProjectDir { get; set; } = "";

    // ========================================================
    // Agent（分镜设计 / 镜头渲染都交给它做）
    // ========================================================
    //
    // 【为什么留两条路】
    //   用户的要求是"分镜、渲染都由 Agent 做，全程不离开工作台"。
    //   但"调 Agent"有两种落地方式，各有适用场景，所以都留着：
    //
    //     ① 直连大模型（填下面三个字段即启用）
    //        OpenAI 兼容的 /chat/completions，支持流式 → 进度条是真的在动。
    //       优点：一键到底，不依赖别的东西在跑。缺点：要自己有 key。
    //
    //     ② 任务包文件（没填 key 时自动用它）
    //        工作台把任务写成 json + 提示词落盘，然后盯着回包文件；
    //        WorkBuddy 那边的 Agent 读任务包、写回结果，工作台自动检测并回填。
    //        优点：真正的"由你来做"，不用掏 key。
    //
    //   两条路对界面是同一套（进度条 / 取消 / 回填），切换只看填没填 key。

    /// <summary>大模型接口地址（OpenAI 兼容），例如 https://api.deepseek.com/v1 。留空 = 走任务包文件通道。</summary>
    public string AgentBaseUrl { get; set; } = "";

    /// <summary>接口密钥。留空同上。</summary>
    public string AgentApiKey { get; set; } = "";

    /// <summary>模型名，例如 deepseek-chat 。</summary>
    public string AgentModel { get; set; } = "";

    /// <summary>是否用流式返回（能看到 token 一个个回来，进度条才是真的在动）。</summary>
    public bool AgentStream { get; set; } = true;

    /// <summary>
    /// 分镜上次用的镜头切法：0=一句一镜 1=固定节奏 2=语义合并。
    ///
    /// ★ 默认 2（语义合并）—— 2026-10-01 用户拍板。
    ///   起因：「一句一镜」在真实项目里切出 219 个镜头，每句换一次画面，看着像 PPT 翻页。
    ///   语义合并把相邻句子并成 5~9 秒的块（一个块 = 一个不切走的场景），才是短视频初切首选。
    ///   它只决定「新建项目 / 清空项目时单选按钮默认亮哪个」——进分镜页仍可随时拨回
    ///   「一句一镜」，手动合并/拆分也完全不受影响（那三个按钮跟切法无关）。
    ///
    /// ★ 这个值只在 ProjectSession.ClearState() 里被读一次（新项目的初值）。
    ///   已存在的项目一律以 project.json 存档里的 Shots.SplitMode 为准，不会被打回默认。
    /// </summary>
    public int LastShotSplitMode { get; set; } = 2;

    /// <summary>分镜上次填的"固定节奏"目标时长（秒）。</summary>
    public double LastShotPaceSec { get; set; } = 6.0;

    // ========================================================
    // 读写
    // ========================================================

    /// <summary>配置文件的存放目录。</summary>
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "翊瑄的工作台");

    /// <summary>
    /// 程序数据的根目录（%APPDATA%\翊瑄的工作台）。
    /// 配置、灵感池这类"跟具体项目无关"的东西都放这儿。
    /// </summary>
    public static string DataDir => Dir;

    /// <summary>配置文件的完整路径。</summary>
    private static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>
    /// 读配置。读不到（第一次运行 / 文件损坏）就返回一份默认值。
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppConfig();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            // 配置文件坏了不能让程序起不来 —— 直接用默认值继续
            return new AppConfig();
        }
    }

    /// <summary>
    /// 写配置。失败（比如磁盘只读）也不影响程序继续用。
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch
        {
            // 存不了就算了，下次重新设置 —— 不值得为此弹窗打断用户
        }
    }
}
