// ============================================================
// TimelineCollision.cs - 时间轴碰撞求解（纯逻辑，零界面依赖）
// ============================================================
//
// 【这个文件解决什么问题】
//   时间轴上的块不能互相覆盖。拖动/裁切时，"鼠标想把它放哪儿"和
//   "它实际能放哪儿"是两回事 —— 这个文件专门算后面那件事。
//
// 【为什么单独拎出来放 Core】
//   1. 它是纯数学：输入一串区间，输出一个位移量。不碰 WPF、不碰鼠标。
//   2. 放在 Core（net8.0，跨平台）就能被独立的控制台程序直接测，
//      不用起界面、不用点鼠标 —— 拖拽这种交互最容易出边界 bug，
//      能脱离界面测试是关键。
//   3. 以后想换成"多轨道"或"允许重叠的某几种块"，只改这里。
//
// 【核心概念：空档（Corridor）】
//   把所有"别的块"占掉的区间从时间轴上挖掉，剩下的每一段连续空白
//   就是一个"空档"。一块/一组块只能整整齐齐待在某个空档里。
//
//   · 鼠标指向的空档装得下 → 跟着鼠标走（两边贴墙就是"顶住"）
//   · 鼠标指向的块身上   → 被挤到那个块的左/右边缘（顶住）
//   · 鼠标指向的空档装不下 → 判为无效（界面画警告红，松手弹回）
//   · 最后一个空档延伸到 +∞ → 片尾可以随便放
//
// 【坐标约定】
//   全部是"时间轴秒数"，不是像素。谁调用谁负责换算。
// ============================================================

using System;
using System.Collections.Generic;

namespace LiteCut.Core.Timeline;

/// <summary>
/// 时间轴上的一个区间（秒）。左闭右开，长度可以为 0。
/// 用它代表"一个块占掉的范围"或"一段空档"。
/// </summary>
public readonly struct TimeRange
{
    /// <summary>区间起点（秒）</summary>
    public double Start { get; }

    /// <summary>区间终点（秒）</summary>
    public double End { get; }

    /// <summary>构造一个区间。参数反了会自动交换（容错）。</summary>
    public TimeRange(double start, double end)
    {
        if (end < start) (start, end) = (end, start);
        Start = start;
        End = end;
    }

    /// <summary>区间长度（秒），不会是负数</summary>
    public double Length => End - Start;

    /// <summary>调试用：打印成 [1.000, 3.500]</summary>
    public override string ToString() => $"[{Start:F3}, {End:F3}]";
}

/// <summary>
/// 拖动落点的判定结果。
/// </summary>
public enum PlacementState
{
    /// <summary>落点合法，松手可以放</summary>
    Free,

    /// <summary>被别的块挡住（顶住了某一面墙）—— 落点是合法的，只是走不过去</summary>
    Blocked,

    /// <summary>鼠标指的这个空档装不下 —— 落点无效，松手要弹回原位</summary>
    TooNarrow,
}

/// <summary>
/// 一次拖动求解的答案。
/// </summary>
public readonly struct Placement
{
    /// <summary>实际允许的位移（秒）。界面拿它去改块的位置。</summary>
    public double Delta { get; init; }

    /// <summary>落点状态</summary>
    public PlacementState State { get; init; }

    /// <summary>是不是顶住了左边（界面画红色渐变提示用）</summary>
    public bool TouchedLeft { get; init; }

    /// <summary>是不是顶住了右边（界面画红色渐变提示用）</summary>
    public bool TouchedRight { get; init; }

    /// <summary>能不能落在当前位置（TooNarrow 就是不能）</summary>
    public bool CanDrop => State != PlacementState.TooNarrow;

    /// <summary>方便调试打印</summary>
    public override string ToString() =>
        $"{State} delta={Delta:F3} L={TouchedLeft} R={TouchedRight}";
}

/// <summary>
/// 裁切（拖块的左/右边缘）求解结果。
/// </summary>
public readonly struct TrimPlacement
{
    /// <summary>允许拖到的边缘位置（秒）</summary>
    public double Value { get; init; }

    /// <summary>是不是顶住了（顶住时画红色渐变，并且不再跟着鼠标走）</summary>
    public bool Blocked { get; init; }

    public override string ToString() => $"{Value:F3} blocked={Blocked}";
}

/// <summary>
/// 时间轴碰撞求解器 —— 全是静态纯函数，没有状态，随便调用。
/// </summary>
public static class TimelineCollision
{
    /// <summary>浮点容差：小于这个差异就当"贴在一起"</summary>
    public const double Eps = 1e-6;

    // ========================================================
    // 空档计算
    // ========================================================

    /// <summary>
    /// 把"障碍块"占用的区间从时间轴上挖掉，返回剩下的空档列表（按时间排序）。
    ///
    /// 细节：
    ///   · 互相重叠/相接的障碍会先合并 —— 历史工程里可能存着重叠的块，
    ///     合并之后照样能算对，不会崩。
    ///   · 返回值最后一段的 End 是 double.PositiveInfinity（片尾无限长）。
    ///   · 长度恰好为 0 的空档也会返回（用来表示"两面墙之间一点缝都没有"），
    ///     这样上层能明确知道"这里塞不下"，而不是"没找到"。
    /// </summary>
    /// <param name="obstacles">障碍块占用的区间</param>
    /// <param name="minStartSec">时间轴允许的最早起点（通常是 0）</param>
    public static List<TimeRange> Corridors(IReadOnlyList<TimeRange> obstacles, double minStartSec = 0)
    {
        var merged = Merge(obstacles);

        var result = new List<TimeRange>(merged.Count + 1);
        double cursor = minStartSec;

        foreach (var o in merged)
        {
            if (o.End <= cursor) continue;              // 完全在游标左边，跳过
            if (o.Start > cursor)
                result.Add(new TimeRange(cursor, o.Start));   // 游标 → 障碍起点 = 一段空档
            else
                result.Add(new TimeRange(cursor, cursor));    // 一点缝都没有：记一个零长空档
            cursor = o.End;
        }

        // 最后一个空档：从最后一块的末尾一直到无穷远
        result.Add(new TimeRange(cursor, double.PositiveInfinity));
        return result;
    }

    /// <summary>
    /// 合并重叠或相接的区间（标准区间合并）。返回的列表按起点排序、互不重叠。
    /// </summary>
    public static List<TimeRange> Merge(IReadOnlyList<TimeRange> ranges)
    {
        var list = new List<TimeRange>(ranges.Count);
        foreach (var r in ranges)
        {
            if (r.Length < 0) continue;
            if (double.IsNaN(r.Start) || double.IsNaN(r.End)) continue;
            list.Add(r);
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<TimeRange>(list.Count);
        foreach (var r in list)
        {
            if (merged.Count == 0)
            {
                merged.Add(r);
                continue;
            }

            var last = merged[merged.Count - 1];
            if (r.Start <= last.End + Eps)
            {
                if (r.End > last.End)
                    merged[merged.Count - 1] = new TimeRange(last.Start, r.End);
            }
            else
            {
                merged.Add(r);
            }
        }
        return merged;
    }

    // ========================================================
    // 拖动（拖块中间 = 移动位置）
    // ========================================================

    /// <summary>
    /// 求解"把整组块往某个方向拖"的结果。
    ///
    /// 【参数为什么传 desiredDelta 而不是鼠标绝对位置】
    ///   传位移量，调用方就不用关心鼠标按在块的哪个位置 ——
    ///   抓块头还是抓块尾，行为完全一样，也不会因为浮点误差累积而漂移。
    /// </summary>
    /// <param name="moving">整组块当前占用的总区间（含组内空隙）</param>
    /// <param name="obstacles">所有"别的块"占用的区间</param>
    /// <param name="desiredDelta">鼠标希望移动多少秒（右正左负）</param>
    /// <param name="minStartSec">时间轴允许的最早起点</param>
    public static Placement ResolveMove(
        TimeRange moving,
        IReadOnlyList<TimeRange> obstacles,
        double desiredDelta,
        double minStartSec = 0)
    {
        if (double.IsNaN(desiredDelta)) desiredDelta = 0;

        double width = moving.Length;
        double target = moving.Start + desiredDelta;

        var corridors = Corridors(obstacles, minStartSec);

        // ---- 1. 鼠标指的这段空档在哪儿 ----
        var home = FindCorridor(corridors, target, desiredDelta);

        // ---- 2. 这段空档装不下 ----
        if (home.Length + Eps < width)
        {
            return new Placement
            {
                // 仍然跟着鼠标走，好让用户看清"我正想放哪儿"，只是判为无效
                Delta = desiredDelta,
                State = PlacementState.TooNarrow,
            };
        }

        // ---- 3. 装得下：在空档里夹取 ----
        double maxStart = double.IsPositiveInfinity(home.End)
            ? double.PositiveInfinity
            : home.End - width;

        double newStart = target;
        if (newStart < home.Start) newStart = home.Start;
        if (newStart > maxStart) newStart = maxStart;

        double delta = newStart - moving.Start;

        bool touchedLeft = Math.Abs(newStart - home.Start) <= Eps;
        bool touchedRight = !double.IsPositiveInfinity(maxStart) && Math.Abs(newStart - maxStart) <= Eps;

        return new Placement
        {
            Delta = delta,
            State = touchedLeft || touchedRight ? PlacementState.Blocked : PlacementState.Free,
            TouchedLeft = touchedLeft,
            TouchedRight = touchedRight,
        };
    }

    /// <summary>
    /// 找 target 落在哪个空档里。
    ///
    /// 如果 target 正好落在某个障碍块身上（这是"想穿过去"的情况），
    /// 按拖动方向把它归到"来路那一侧"的空档 —— 也就是被那个块挡住：
    ///   · 往右拖 → 归到障碍块左边的空档（块右边缘贴住障碍的左边缘）
    ///   · 往左拖 → 归到障碍块右边的空档
    /// 这样"顶住"和"跳过"就是一个逻辑自然长出来的结果：
    /// 鼠标一旦越过障碍块的另一侧边缘，target 就落进下一个空档里了。
    /// </summary>
    private static TimeRange FindCorridor(List<TimeRange> corridors, double target, double direction)
    {
        // 先看是否落在某个空档内（注意最后一段 End 是 +∞，两端都算包含）
        foreach (var c in corridors)
        {
            if (target >= c.Start - Eps && target <= c.End + Eps)
                return c;
        }

        // 落在障碍块身上：按方向取来路那一侧的空档
        if (direction >= 0)
        {
            // 往右：取"终点在最接近 target 的左边"的那个空档 = 被墙挡住的位置
            TimeRange best = corridors[0];
            foreach (var c in corridors)
            {
                if (c.End <= target + Eps) best = c;
                else break;
            }
            return best;
        }
        else
        {
            // 往左：取"起点在最接近 target 的右边"的那个空档
            TimeRange best = corridors[corridors.Count - 1];
            for (int i = corridors.Count - 1; i >= 0; i--)
            {
                if (corridors[i].Start >= target - Eps) best = corridors[i];
                else break;
            }
            return best;
        }
    }

    // ========================================================
    // 裁切（拖块的左/右边缘）
    // ========================================================

    /// <summary>
    /// 求解"拖块的左边缘"。块的右边缘固定在 moving.End，只动左边缘。
    ///
    /// 两道限制叠加：
    ///   1. 时间轴上不能盖住别的块 —— 左边缘不能越过左边邻居的右边缘
    ///   2. 块本身不能短于 minLength（由调用方连同"源素材边界"一起夹取）
    /// </summary>
    /// <param name="moving">这个块当前占用的区间</param>
    /// <param name="obstacles">所有"别的块"占用的区间</param>
    /// <param name="desiredStart">鼠标希望把左边缘拖到哪儿</param>
    /// <param name="minLength">块允许的最短时长（秒）</param>
    /// <param name="minStartSec">时间轴允许的最早起点（通常是 0）</param>
    public static TrimPlacement ResolveTrimLeft(
        TimeRange moving,
        IReadOnlyList<TimeRange> obstacles,
        double desiredStart,
        double minLength,
        double minStartSec = 0)
    {
        double wall = minStartSec;
        foreach (var o in obstacles)
        {
            // 只关心"在我们左边缘区域里"的块：它的起点在本块终点之前
            if (o.Start >= moving.End - Eps) continue;
            if (o.End > wall) wall = o.End;
        }

        double maxStart = moving.End - minLength;      // 再往右就短于最短时长了
        double minStart = Math.Max(wall, minStartSec); // 左边邻居的右边缘 + 时间轴原点

        if (minStart > maxStart + Eps)
        {
            // 左边这块已经贴到（甚至压过）了最短长度，完全没法往右收
            return new TrimPlacement { Value = moving.Start, Blocked = true };
        }

        double v = desiredStart;
        bool blocked = false;
        if (v < minStart) { v = minStart; blocked = true; }
        if (v > maxStart) { v = maxStart; blocked = true; }

        return new TrimPlacement { Value = v, Blocked = blocked };
    }

    /// <summary>
    /// 求解"拖块的右边缘"。块的左边缘固定在 moving.Start，只动右边缘。
    /// 右边缘不能盖住右边邻居，也就是不能越过它的左边缘。
    /// </summary>
    public static TrimPlacement ResolveTrimRight(
        TimeRange moving,
        IReadOnlyList<TimeRange> obstacles,
        double desiredEnd,
        double minLength)
    {
        double wall = double.PositiveInfinity;
        foreach (var o in obstacles)
        {
            // 只关心"在我们右边缘区域里"的块：它的终点在本块起点之后
            if (o.End <= moving.Start + Eps) continue;
            if (o.Start < wall) wall = o.Start;
        }

        double minEnd = moving.Start + minLength;      // 再往左就短于最短时长了
        double maxEnd = double.IsPositiveInfinity(wall) ? desiredEnd : wall;

        if (maxEnd < minEnd - Eps)
        {
            return new TrimPlacement { Value = moving.End, Blocked = true };
        }

        double v = desiredEnd;
        bool blocked = false;
        if (v > maxEnd) { v = maxEnd; blocked = true; }
        if (v < minEnd) { v = minEnd; blocked = true; }

        return new TrimPlacement { Value = v, Blocked = blocked };
    }
}
