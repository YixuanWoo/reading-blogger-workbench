// ============================================================
// FinalScript.cs - 成片口播稿契约（精剪 → 分镜的交接主角）
// ============================================================
//
// 【这是什么】
//   剪完之后，观众**实际会听到**的那篇稿子，每一句都带着"成片第几秒到
//   第几秒"的时间码。
//
// 【为什么非得有它】
//   分镜要回答的是"这 X 秒画面上放什么"，前提是先知道"这 X 秒在说什么"。
//   之前这一步用的是**文案稿**（你动笔写的那版）+ 按字数占比分配时间，
//   有两个毛病：
//     ① 你写稿的内容和嘴上实际说出来的往往不是一回事（临场加的两句、
//        重复说了一遍、砍掉的半句）—— 画面会配到不存在的话上；
//     ② "字数占比 ≈ 时间占比"是假设语速均匀的近似，停顿、重读、举例
//        都会让它偏。剪得越多偏得越多。
//
//   换成成片口播稿之后：镜头边界能对齐到真正的句末，时长是真实值，
//   被剪掉的话压根不会进分镜表。
//
// 【它怎么来的 —— 纯算术，不需要 AI】
//     转写稿（每个词都有源素材时间码）
//   ∩ 精剪决策（每个块引用源素材的哪一段）
//   = 保留下来的词，按"块在成片里的先后"串起来
//
//   ★ 顺序必须按块在成片里的先后，不能按源时间扫 ——
//     精剪允许挪块（把后面那句挪到前面讲），剪完的语序可能和原片不一样。
//
// 【谁来写】FinalScriptBuilder（Engine 层，纯逻辑、可无头验证）
// 【谁来读】第③步分镜设计（生成镜头 + 给 Agent 的提示词）
// ============================================================

using System.Collections.Generic;

namespace LiteCut.Core.Contracts;

/// <summary>
/// 一份成片口播稿。
/// </summary>
public class FinalScript
{
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>
    /// 这份稿子是精确算出来的，还是估算的。
    /// true = 没导入转写稿，只能拿文案稿按字数占比摊时间 —— 界面必须写明"估算"。
    /// </summary>
    public bool IsEstimate { get; set; }

    /// <summary>稿子来源："transcript"（转写稿求交）/ "script"（文案稿估算）。</summary>
    public string Source { get; set; } = "";

    /// <summary>成片总时长（秒）。</summary>
    public double TotalSec { get; set; }

    /// <summary>总字数（不含标点空格的估算口径，用于界面显示"2130 字"）。</summary>
    public int CharCount { get; set; }

    /// <summary>逐句列表，按成片时间顺序。</summary>
    public List<ScriptLine> Lines { get; set; } = new List<ScriptLine>();
}

/// <summary>
/// 口播稿里的一句。
/// </summary>
public class ScriptLine
{
    /// <summary>序号，从 1 开始。</summary>
    public int Index { get; set; }

    /// <summary>这句话的文字。</summary>
    public string Text { get; set; } = "";

    /// <summary>这句话在**成片**里的开始时间（秒）。</summary>
    public double StartSec { get; set; }

    /// <summary>这句话在**成片**里的结束时间（秒）。</summary>
    public double EndSec { get; set; }

    /// <summary>对应的源素材开始时间（渲染要回源对齐）。</summary>
    public double SourceStartSec { get; set; }

    /// <summary>对应的源素材结束时间。</summary>
    public double SourceEndSec { get; set; }

    /// <summary>时长（秒）。这是**成片里真实播出的时长**（被剪掉的部分不在内）。</summary>
    public double DurationSec => EndSec - StartSec;

    /// <summary>
    /// 这句话在源素材里的时间范围，被保留下来多少（0~1）。
    /// 注意：剪掉气口、重录、语气词都会让这个值小于 1，但**内容一字不少** ——
    /// 判断"这句少没少话"请看 CutSec / Truncated，不要看这个。
    /// </summary>
    public double KeptRatio { get; set; } = 1;

    /// <summary>
    /// 这句话**确实被剪掉**的内容时长（秒）。不含气口、语气词、重录那些废话。
    /// 有词级时间码时按丢掉的词算，否则按句内的缺口算。
    /// </summary>
    public double CutSec { get; set; }

    /// <summary>
    /// 这句话少了内容（中段被挖掉一截话），不是"剪了气口"。
    /// ★ 不要拿"句子的时间范围被块边界切到了"当判据 —— 剪停顿也满足那个条件，
    ///   一个真实项目上那样算会误报 61% 的句子。判据见 FinalScriptBuilder。
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// 是不是长句（≥12 秒）。
    /// 长句一镜到底会很呆 —— Agent 提示词里对长句要求"动效更复杂，
    /// 让句子内容动态起来，不要只顶一行字幕"。
    /// </summary>
    public bool IsLong => DurationSec >= 12.0;

    /// <summary>
    /// 这句话被用户手工改过（转写错字、微调）。
    ///
    /// 【为什么记这一笔】
    ///   点「重算」会重新从转写稿推一遍稿子。改过的句子必须留下来 ——
    ///   不然改了半天一重算全没了，谁还敢改。
    ///   重算时按源时间匹配，Edited=true 的句子沿用改过的文本。
    /// </summary>
    public bool Edited { get; set; }
}
