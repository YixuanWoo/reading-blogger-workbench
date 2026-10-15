// ============================================================
// AutoCutOptions.cs - 自动粗剪的旋钮
// ============================================================
//
// 这一份文件里的数字，就是你在《美的历程》那条素材上实测出来的。
// 每个数字后面都标了它是怎么来的，免得以后调参调懵。
//
// 【三档的来历】（实测：原始 608.06s，人工剪到 370.84s）
//
//   人工剪掉 237.2s，其中
//     ├─ 165.2s 是静音/气口   ← 工程层，纯算法能解决
//     └─  72.0s 是重复/口误   ← 语义层，需要听懂内容
//
//   静音层三档（差别只在"每处停顿留多久"）：
//     保守 0.70s → 成片 441.5s，残留停顿 123.5s（与人工 124.5s 差 0.8%）
//     适中 0.45s → 成片 407.2s，残留停顿  89.4s
//     激进 0.25s → 成片 369.3s，残留停顿   0.7s
//
//   ⚠️ 注意：激进档时长跟人工几乎一样（369 vs 371），但那是巧合——
//      它是"剪光停顿"换来的，人工是"保留停顿 + 剪废话"换来的。
//      真正的目标是：激进档的静音层 + 去重层 = 同时干掉 165s 和 72s。
//
// ============================================================

namespace LiteCut.Core.Engine;

/// <summary>
/// 自动粗剪的参数。界面上那几个滑动条就绑在它身上。
/// </summary>
public class AutoCutOptions
{
    // ---------------- 静音层 ----------------

    /// <summary>
    /// 低于多少 dB 算静音（默认 -30）。
    ///
    /// 别被"整片均值 -28.1dB"吓到 —— 那个均值是被大量静音拉低的。
    /// 实测人声段 mean 在 -21 ~ -27dB，真静音在 -45 ~ -60dB，
    /// 中间有 13dB 以上的空档，-30 落在空档里，安全。
    /// </summary>
    public double SilenceNoiseDb { get; set; } = -30.0;

    /// <summary>
    /// 静音持续多久才值得剪（默认 0.30 秒）。
    /// 短于这个的停顿是正常语气，剪了会把话切碎。
    /// </summary>
    public double SilenceMinDurSec { get; set; } = 0.30;

    /// <summary>
    /// 每处静音保留多少秒（默认 0.25 = 激进档）。
    /// 这是控制"节奏感"的旋钮 —— 留 0 会显得很赶，留太多会显得拖。
    /// </summary>
    public double BreathKeepSec { get; set; } = 0.25;

    /// <summary>
    /// 切点前留白（默认 0）。
    /// 如果你发现剪完有"字头被切掉"的爆音，把它调到 0.03 左右。
    /// 默认 0 是因为实测参数就是在无留白的条件下校准出来的。
    /// </summary>
    public double LeadPadSec { get; set; } = 0.0;

    /// <summary>切点后留白（默认 0）。同上，防"字尾被切掉"。</summary>
    public double TailPadSec { get; set; } = 0.0;

    // ---------------- 去重层（不需要转写，纯波形） ----------------

    /// <summary>是否检测"明显重复"。默认开。</summary>
    public bool DetectRepeats { get; set; } = true;

    /// <summary>短于多少秒的重复不值得处理（默认 1.0 秒）。太短的重复通常是口吃，剪了反而怪。</summary>
    public double RepeatMinSec { get; set; } = 1.0;

    /// <summary>长于多少秒的不当重复处理（默认 8 秒）。太长的"重复"多半是两段不同的话。</summary>
    public double RepeatMaxSec { get; set; } = 8.0;

    /// <summary>两处相隔多少秒内才算"重说"（默认 12 秒）。隔太远的相同句子不算。</summary>
    public double RepeatWindowSec { get; set; } = 12.0;

    /// <summary>
    /// 波形相似度阈值（默认 0.86）。
    ///
    /// 同一句话重说一遍，音节节奏几乎一样 → 包络相关性很高（0.9+）。
    /// 两句不同的话，哪怕长度相同，相关性通常到不了 0.86。
    /// </summary>
    public double RepeatSimilarity { get; set; } = 0.86;

    /// <summary>两段时长比要在这个范围内（默认 0.75 ~ 1.33），差太多说明不是同一句。</summary>
    public double RepeatDurRatioMin { get; set; } = 0.75;
    public double RepeatDurRatioMax { get; set; } = 1.33;

    /// <summary>至少能省多少秒才动手（默认 0.6 秒）。省得太少不值得冒误剪的风险。</summary>
    public double RepeatMinGainSec { get; set; } = 0.6;

    /// <summary>低于这个把握只提示、不自动剪（默认 0.6）。拿不准的留给人工。</summary>
    public double RepeatAutoCutConfidence { get; set; } = 0.60;

    // ---------------- 语义层（有转写稿时才会跑） ----------------

    /// <summary>
    /// 语义层的参数。有转写稿且校验通过时这一层才会真的起作用。
    ///
    /// 放在这里作为一个子对象，是为了让"这一层究竟有没有生效"显式可见：
    /// 没有转写稿时这个对象仍然存在，但根本不会被调用。
    /// </summary>
    public TextCutOptions Text { get; set; } = TextCutOptions.Balanced();

    // ---------------- 总护栏 ----------------

    /// <summary>
    /// 两层加起来最多能剪掉全片的多大比例（默认 0.75）。
    ///
    /// 这是最后一道防线。无论算法怎么判，一条 10 分钟的口语也不能被剪成 2 分钟 ——
    /// 出现这种情况只可能是某个环节的判据失灵了。
    /// 触发时不会静默，界面上会明确提示"剪得太多了，建议你复核"。
    /// </summary>
    public double MaxTotalDropRatio { get; set; } = 0.75;

    /// <summary>是否允许文本层的切点智能吸附到附近的安静点（默认开）。</summary>
    public bool SnapTextCuts { get; set; } = true;

    // ---------------- 预设 ----------------

    /// <summary>激进档：停顿几乎剪光（你选的这档）。语义层也用激进配置。</summary>
    public static AutoCutOptions Aggressive() => new()
    {
        SilenceNoiseDb = -30.0,
        SilenceMinDurSec = 0.30,
        BreathKeepSec = 0.25,
        DetectRepeats = true,
        Text = TextCutOptions.Aggressive(),
        MaxTotalDropRatio = 0.80,
    };

    /// <summary>适中档：留一点呼吸。语义层也走适中。</summary>
    public static AutoCutOptions Balanced() => new()
    {
        SilenceNoiseDb = -30.0,
        SilenceMinDurSec = 0.30,
        BreathKeepSec = 0.45,
        DetectRepeats = true,
        Text = TextCutOptions.Balanced(),
        MaxTotalDropRatio = 0.75,
    };

    /// <summary>保守档：节奏最接近人工（实测残留停顿与人工差 0.8%）。语义层只删最没争议的。</summary>
    public static AutoCutOptions Conservative() => new()
    {
        SilenceNoiseDb = -30.0,
        SilenceMinDurSec = 0.30,
        BreathKeepSec = 0.70,
        DetectRepeats = true,
        Text = TextCutOptions.Conservative(),
        MaxTotalDropRatio = 0.65,
    };
}
