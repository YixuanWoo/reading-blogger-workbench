// ============================================================
// TextCutOptions.cs - 语义层（有文字时）的那几个旋钮
// ============================================================
//
// 【和 AutoCutOptions 的关系】
//
//   AutoCutOptions 管波形层（静音、波形重复），无文字时它也照样工作。
//   TextCutOptions 管文字层（填充词、半截重说、整句重复），必须有转写稿。
//
//   两者是**并列**关系，不是替代关系：
//
//     有转写稿 → 两层都跑，结果合并
//     没转写稿 → 只跑波形层，流程照常往下走
//
//   这就是为什么它单独放一个文件 ——
//   这样"whisper 哪天挂了/被卸载了"，只是这个对象不生效，
//   不会牵动波形层一行代码。
//
// 【为什么每个检测项都有独立的 AutoCut 阈值】
//
//   四类问题的"拿不准程度"天差地别：
//
//     半截重说   几乎不会错（第二句明明包含了第一句，且更完整）
//     词级口吃   基本不会错（相同的字紧接着说两遍）
//     整句重复   有一定风险（有意的重复可能是修辞上的反复）
//     话语标记   风险最高（可能承担实词义）
//
//   用同一个阈值会让保守的过度保守、激进的过度激进。
//   分开之后，界面上还能单独给每一类一个开关 —— 用户想"只去嗯啊，不动句式"
//   就勾掉另外三个。
//
// ============================================================

namespace LiteCut.Core.Engine;

/// <summary>语义层粗剪的参数。</summary>
public class TextCutOptions
{
    // ---------------- 总开关 ----------------

    /// <summary>是否启用语义层。关掉就退化成"纯波形粗剪"（等价于没有转写稿）。</summary>
    public bool Enabled { get; set; } = true;

    // ---------------- 填充词 ----------------

    /// <summary>是否处理填充词。默认开。</summary>
    public bool RemoveFillers { get; set; } = true;

    /// <summary>
    /// 是否连"话语标记"（就是、然后、这个、那个）也删。默认**关**。
    ///
    /// 这些词有可能承担实词义，删了句子会散。
    /// 关着的意思是：照样**检测出来**并在界面上标出来，
    /// 但是否动手由人点头。
    /// </summary>
    public bool RemoveDiscourseMarkers { get; set; } = false;

    /// <summary>
    /// 填充词的自动剪阈值（默认 0.88）。
    ///
    /// 卡这个值是有讲究的：一级口气词基本都在 0.90 以上（放心剪），
    /// 而歧义较大的单字（"啊""哦"）给到 0.85 —— 它们需要靠
    /// "在句首"之类的位置加成才能过线。这叫**位置不够就不动手**。
    /// </summary>
    public double FillerAutoCutConfidence { get; set; } = 0.88;

    /// <summary>删填充词时，切点前后各留多少秒（默认 0.02）。删得太干净会有"机器味"。</summary>
    public double FillerPadSec { get; set; } = 0.02;

    // ---------------- 半截重说（说了半句卡住、重头说） ----------------

    /// <summary>是否检测"半截重说"。默认开 —— 这是用户点名要的头号问题。</summary>
    public bool DetectFalseStart { get; set; } = true;

    /// <summary>两句相隔多少秒内，才可能算"卡住了重说"（默认 6 秒）。隔太久就是另起话题。</summary>
    public double FalseStartMaxGapSec { get; set; } = 6.0;

    /// <summary>
    /// 覆盖度阈值（默认 0.60）。
    /// 较短的那句有多大比例的文字出现在较长的那句里。
    /// 0.60 是实测偏保守的值 —— 真正的"半截"通常 0.85 以上。
    /// </summary>
    public double FalseStartOverlap { get; set; } = 0.60;

    /// <summary>公共前缀比例门槛（默认 0.50）。"卡住重说"的典型特征是**开头几个字一样**。</summary>
    public double FalseStartPrefix { get; set; } = 0.50;

    /// <summary>半截句至少要有几个字（默认 3）。两个字的重说通常是口吃，走词级那条路。</summary>
    public int FalseStartMinChars { get; set; } = 3;

    /// <summary>两句长度的差距至少多少比例才叫"半截"（默认 0.15）。长度差不多那就是重复。</summary>
    public double FalseStartLenGap { get; set; } = 0.15;

    /// <summary>自动剪阈值（默认 0.72）。这条路的判定很稳，所以阈值给得低。</summary>
    public double FalseStartAutoCutConfidence { get; set; } = 0.72;

    // ---------------- 词级口吃（同一句里几个字重复一遍） ----------------

    /// <summary>是否检测词级口吃。默认开。</summary>
    public bool DetectStutter { get; set; } = true;

    /// <summary>重复片段最长几个字（默认 6）。超过 6 个字的连续重复很罕见，多半是别的现象。</summary>
    public int StutterMaxChars { get; set; } = 6;

    /// <summary>重复片段最短几个字（默认 2）。1 个字太容易误判（"的的"）。</summary>
    public int StutterMinChars { get; set; } = 2;

    /// <summary>整个"重复两遍"的过程最长几秒（默认 3.0）。太长说明是正常的两句话。</summary>
    public double StutterMaxSec { get; set; } = 3.0;

    /// <summary>重复片段内部允许的最大间隔（默认 0.8 秒）。真卡壳通常不超过 0.5 秒。</summary>
    public double StutterMaxInnerGapSec { get; set; } = 0.8;

    /// <summary>自动剪阈值（默认 0.75）。</summary>
    public double StutterAutoCutConfidence { get; set; } = 0.75;

    // ---------------- 整句重复 ----------------

    /// <summary>是否检测"同样的话说了两遍"（跨句）。默认开。</summary>
    public bool DetectTextRepeat { get; set; } = true;

    /// <summary>相隔多少秒内算"又说了一遍"（默认 30 秒）。</summary>
    public double TextRepeatWindowSec { get; set; } = 30.0;

    /// <summary>
    /// 相似度阈值（默认 0.72，用 Dice 系数）。
    ///
    /// 比波形层的相似度阈值（0.86）低 —— 因为文字比波形更能容忍音量语速差异，
    /// 但同时相似的随机性也更高，0.72 是实测之后取的安全值。
    /// </summary>
    public double TextRepeatDice { get; set; } = 0.72;

    /// <summary>至少多少个字的句子才参与重复判定（默认 6）。太短的句子相似是巧合。</summary>
    public int TextRepeatMinChars { get; set; } = 6;

    /// <summary>自动剪阈值（默认 0.65）。这条路风险最高，阈值给得最松意味着最容易只提示不动手。</summary>
    public double TextRepeatAutoCutConfidence { get; set; } = 0.65;

    // ---------------- ⑤ 重录（同句录了好几遍） ----------------

    /// <summary>是否检测重录（默认开）。这是语义层里收益最大的一项。</summary>
    public bool DetectRetake { get; set; } = true;

    /// <summary>
    /// 两遍之间最多隔多久还算"重录"（默认 12 秒）。
    ///
    /// 实测标定（伪史论 658s，对照豆包工作的人工粗剪）：
    ///   间隔 ≤12s 时结果最准，放宽到 20s 以上会引入跨段落的误判
    ///   —— 口播里隔了半分钟再说同一个词，往往是另起一层意思，不是重录。
    /// </summary>
    public double RetakeWindowSec { get; set; } = 12.0;

    /// <summary>
    /// 自动删除的字符重叠率门槛（默认 0.75）。
    ///
    /// 参数扫描实测：0.75 时成片 429.13s，与人工粗剪的 428.92s 只差 0.21 秒。
    /// 调到 0.78 更安全（误剪减半）但会少剪 6.7 秒。
    /// </summary>
    public double RetakeCharOverlap { get; set; } = 0.75;

    /// <summary>
    /// 只提示、不自动删的字符重叠率门槛（默认 0.68）。
    /// 落在 0.68~0.75 之间的进建议清单，交给人工勾选 —— 拿不准的不动手。
    /// </summary>
    public double RetakeSuggestCharOverlap { get; set; } = 0.68;

    /// <summary>辅助判据：开头至少要有这么多比例相同（默认 0.30）。重录几乎总是从头再说一遍。</summary>
    public double RetakePrefix { get; set; } = 0.30;

    /// <summary>辅助判据：bigram 相似度门槛（默认 0.50）。和 RetakePrefix 满足其一即可。</summary>
    public double RetakeDice { get; set; } = 0.50;

    /// <summary>短于多少字不参与重录判定（默认 6 字）。太短的片段字符重叠率没有统计意义。</summary>
    public int RetakeMinChars { get; set; } = 6;

    /// <summary>
    /// 保留更完整的那遍：若"后一遍"的长度不足"前一遍"的这个比例（默认 0.70），就不删前一遍。
    ///
    /// 【为什么需要】
    ///   有些相邻片段是"完整句 + 它的半截前缀"，字符重叠率 1.0、开头完全相同，
    ///   看起来完美符合重录特征。但半截的那一遍才是废的，完整句必须留着。
    ///   实测这条规则救下了一处 37.8 秒间隔的误判（比值 0.67）。
    ///
    /// 【为什么是 0.70 而不是更高】
    ///   0.75 会把真重录也拦下来：口吃导致的重录版本本来就比原版短
    ///   （"你还得假设出土的文物和探石寺年" 15 字 → "出出土的文物的探石寺子" 11 字，
    ///   比值 0.73，是货真价实的重录，不该被放过）。
    ///   0.70 正好卡在"口吃重录 0.73"和"半截前缀误判 0.67"之间。
    /// </summary>
    public double RetakeKeepLongerRatio { get; set; } = 0.70;

    /// <summary>重录达到多少把握才自动剪（默认 0.70）。</summary>
    public double RetakeAutoCutConfidence { get; set; } = 0.70;

    // ---------------- 通用护栏 ----------------

    /// <summary>
    /// 短于多少毫秒的问题段不值得单独剪（默认 0.12 秒）。
    /// 剪一个比一次呼吸还短的片段，听感上只会觉得"有个断层"。
    /// </summary>
    public double MinIssueSec { get; set; } = 0.12;

    /// <summary>
    /// 语义层最多能剪掉全片的多大比例（默认 0.35）。
    ///
    /// 这是最重要的**安全开关**：
    /// 万一转写稿完全对不上、或者整篇被误判，
    /// 波形层 + 语义层叠起来的总删除量还有 MaxTotalDropRatio 兜底（在 AutoCutEngine 里）。
    /// 这里先给语义层自己设一道，防止它单干坏事。
    /// </summary>
    public double MaxTextDropRatio { get; set; } = 0.35;

    // ---------------- 预设 ----------------

    /// <summary>激进：什么都敢动手（对应波形层的 Aggressive，但你仍要在界面上过一遍）。</summary>
    public static TextCutOptions Aggressive() => new()
    {
        RemoveFillers = true,
        RemoveDiscourseMarkers = true,
        FillerAutoCutConfidence = 0.80,
        DetectFalseStart = true,
        FalseStartAutoCutConfidence = 0.65,
        DetectStutter = true,
        StutterAutoCutConfidence = 0.70,
        DetectTextRepeat = true,
        TextRepeatAutoCutConfidence = 0.60,
        MaxTextDropRatio = 0.40,
    };

    /// <summary>适中（推荐起步）：口服填充词 + 明显的半截重说，句式不动。</summary>
    public static TextCutOptions Balanced() => new()
    {
        RemoveFillers = true,
        RemoveDiscourseMarkers = false,
        FillerAutoCutConfidence = 0.88,
        DetectFalseStart = true,
        FalseStartAutoCutConfidence = 0.72,
        DetectStutter = true,
        StutterAutoCutConfidence = 0.75,
        DetectTextRepeat = true,
        TextRepeatAutoCutConfidence = 0.70,
        MaxTextDropRatio = 0.30,
    };

    /// <summary>保守：只删最没争议的填充音，其它一律只标注。</summary>
    public static TextCutOptions Conservative() => new()
    {
        RemoveFillers = true,
        RemoveDiscourseMarkers = false,
        FillerAutoCutConfidence = 0.93,   // 只有嗯/呃/唔这一档最确定的才删
        DetectFalseStart = true,
        FalseStartAutoCutConfidence = 0.85,
        DetectStutter = true,
        StutterAutoCutConfidence = 0.88,
        DetectTextRepeat = true,
        TextRepeatAutoCutConfidence = 0.95,   // 基本不自动删，只提示
        MaxTextDropRatio = 0.20,
    };
}
