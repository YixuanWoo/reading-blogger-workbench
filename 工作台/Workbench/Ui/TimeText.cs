// ============================================================
// TimeText.cs - 秒数 → 人看的时间码（全工作台共用一份）
// ============================================================
//
// 【为什么要有这个文件】
//   它原来是 `Step1CutView.FormatTime`（粗剪页的私有工具）。V1.17.0 砍掉粗剪之后，
//   分镜 / 渲染 / 组接三个页面还在用它 —— 但那个类已经不存在了。
//
//   当时有两个选择：把函数复制三份，或者提出来一份。选了后者。
//   理由很实在：**同一个时间码在不同页面显示出两种样子，是那种"看着不对
//   但说不上哪不对"的毛病**，很难查。所以全工作台只有这一个实现。
//
// 【格式】
//   不到 1 小时：03:12.4（分:秒.十分之一秒）
//   超过 1 小时：1:03:12（时:分:秒）
//   为什么保留十分之一秒：剪片时"挪 0.2 秒"是有意义的操作，
//   只显示整秒的话，挪完看着一模一样。
// ============================================================

using System;

namespace LiteCut.Workbench.Ui;

public static class TimeText
{
    /// <summary>把秒数写成 mm:ss.d 或 h:mm:ss。</summary>
    public static string Format(double sec)
    {
        if (sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{(ts.Milliseconds / 100):D1}";
    }
}
