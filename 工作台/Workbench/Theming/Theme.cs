// ============================================================
// Theme.cs - 界面主题（配色方案）
// ============================================================
//
// 【为什么要有这个东西】
//   以前整个工作台只有一套深色配色：颜色都写在 App.xaml 里，
//   或者直接写死在各个页面的 XAML / C# 里。白天用深色界面，
//   屏幕一片发黑、字发灰，眼睛很累（2026-10-18 用户原话：
//   "黑色主题白天看起来费眼睛"）。所以要做成"可选的两套配色"。
//
// 【怎么做到"切一下，全界面立刻变色"】
//   1. 所有颜色都定义成 App.xaml 里的一支 SolidColorBrush（一支笔），
//      页面一律 `{StaticResource xxx}` 引用**这一支笔**；
//   2. 切换主题时**不换笔，只改笔里的颜色**（SolidColorBrush.Color）。
//      同一个对象改了颜色，所有引用它的地方会自动重画 ——
//      不需要遍历控件树，也不需要重开窗口。
//   3. 前提：这支笔**不能冻结**（Freeze）。冻结的 Freezable 改不动。
//      App.xaml 里声明的资源默认不冻结，所以别给它们加 Freeze。
//
// 【夜空黑为什么等于"以前的样子"】
//   夜空黑不另存一份色值 —— 启动时直接把 App.xaml 里的原始颜色
//   **快照**下来当夜空黑。这样"恢复夜空黑"永远精确等于改造前的配色，
//   不会因为抄错一位 hex 而慢慢跑偏。
//
// 【新增一个颜色时的规矩】
//   ① 在 App.xaml 里加一支 SolidColorBrush（颜色写夜空黑的值）；
//   ② 在下面的 Light 表里加一条"白天用哪个色"；
//   ③ 页面里用 {StaticResource 那个 key}，**不要再写 #RRGGBB**。
//   ★ 漏了第 ② 步不会崩，但白天主题下那处会保持深色 —— 很显眼，
//     所以无头验证里有一条"浅色主题下不允许还有深色残留"的检查。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LiteCut.Workbench.Theming;

/// <summary>一套主题（现在只有"夜空黑/晨光白"两套，Id 存进 config.json）。</summary>
public sealed class ThemeInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";
}

public static class Theme
{
    /// <summary>夜空黑：原来的深色配色（色值来自 App.xaml 快照，不在这里重复写）。</summary>
    public const string NightId = "night";

    /// <summary>晨光白：白天用的明亮配色。</summary>
    public const string DayId = "day";

    public static IReadOnlyList<ThemeInfo> Themes { get; } = new[]
    {
        new ThemeInfo
        {
            Id = NightId, Name = "夜空黑",
            Hint = "原本的配色。深底亮字，晚上/关灯时看不刺眼。",
        },
        new ThemeInfo
        {
            Id = DayId, Name = "晨光白",
            Hint = "明亮清爽，白底深字。白天光线足的时候用，眼睛不累。",
        },
    };

    /// <summary>当前生效的主题 Id。</summary>
    public static string CurrentId { get; private set; } = NightId;

    /// <summary>
    /// 主题刚切换了（Apply 成功之后发）。
    ///
    /// 【为什么需要这个事件】
    ///   界面 XAML 里的颜色用 {DynamicResource} 引用，资源一换它们自己就跟着变；
    ///   但**纯代码画的控件**（热力图格子、波形柱、时间轴）是"画的时候抓一次笔"，
    ///   不重画就一直是老颜色。它们订阅这个事件，收到后重画一遍。
    ///
    /// ★ 订阅规矩：Loaded 里 += / Unloaded 里 -=（别在构造函数里裸订阅，
    ///   控件销毁后还挂在静态事件上就是泄漏）。
    /// </summary>
    public static event Action? Changed;

    /// <summary>用户保存过的偏好（切换预览时不动它，保存才动）。</summary>
    public static string SavedId { get; set; } = NightId;

    // ========================================================
    // 晨光白：每个 key 白天用哪个颜色
    // ========================================================
    //
    // ★ key 必须与 App.xaml 里的 SolidColorBrush 完全一致。
    //   这里只写"白天值"，夜空黑的值是 App.xaml 原始值（快照）。
    private static readonly Dictionary<string, string> Light = new(StringComparer.Ordinal)
    {
        // ---- 底 / 面板（由深到浅：窗口底 < 导航 < 卡片） ----
        ["BgDeep"]         = "#FFEEF2F7",   // 窗口最底
        ["BgShell"]        = "#FFF4F7FA",   // 页面底 / 顶栏 / 状态栏
        ["BgNav"]          = "#FFE8EEF5",   // 左侧导航
        ["BgNavFoot"]      = "#FFDFE7F0",   // 导航底部信息盒
        ["BgCard"]         = "#FFFFFFFF",   // 卡片
        ["BgCardAlt"]      = "#FFF6F9FC",   // 次一级卡片 / 胶囊
        ["BgChip"]         = "#FFE7EEF6",   // 小胶囊 / 分段控件
        ["BgInput"]        = "#FFFFFFFF",   // 输入框
        ["BgInset"]        = "#FFEFF4F9",   // 内凹区
        ["BgInset2"]       = "#FFECF1F7",   // 内凹区（更深一档）
        ["BgHead"]         = "#FFEDF2F8",   // 表头
        ["BgPreview"]      = "#FF0D1218",   // 视频预览底（两套主题都保持深色：像播放器）
        ["Divider"]        = "#FFE2E9F1",
        ["Divider2"]       = "#FFE5EBF2",
        ["DividerFaint"]   = "#FFE7ECF3",
        ["Border"]         = "#FFD7DFE9",
        ["BorderStrong"]   = "#FFC2CDDA",
        ["BorderMid"]      = "#FFAFBCCA",
        ["BorderHover"]    = "#FF9AABBD",
        ["HoverBg"]        = "#FFE7EEF6",   // 悬停底
        ["HoverBg2"]       = "#FFECF3FA",   // 悬停底（行）
        ["NavSelBg"]       = "#FFDAE7F6",   // 导航选中
        ["SelBg"]          = "#FFD3E5FA",   // 选中行 / 选中项
        ["ItemHover"]      = "#FFE8F1FC",   // 下拉项悬停
        ["PopupBg"]        = "#FFFFFFFF",   // 下拉弹层
        ["PopupBorder"]    = "#FFC6D2E0",

        // ---- 文字（由深到浅） ----
        ["TextMain"]       = "#FF1F2933",
        ["TextBright"]     = "#FF101821",   // 最亮的一档：标题 / 当前句
        ["TextSoft"]       = "#FF37485A",
        ["TextMainAlt"]    = "#FF2A3441",
        ["TextDim"]        = "#FF5A6B7D",
        ["TextFaint"]      = "#FF8394A6",
        ["TextMuted"]      = "#FF7A8898",
        ["TextMuted2"]     = "#FF93A2B1",
        ["TextNote"]       = "#FF8B99A8",
        ["TextGhost"]      = "#FF9DABB9",
        ["TextGhost2"]     = "#FFA4B1BF",
        ["TextFaintAlt"]   = "#FF6B7A8A",
        ["TextWarm"]       = "#FF8A7A76",
        ["TextDisabled"]   = "#FFA8B3BF",
        ["TextOnAccent"]   = "#FFFFFFFF",   // 彩色按钮上的字（两套都是白字）
        ["OnOverlay"]      = "#FFFFFFFF",   // 压在视频/图片上的字（永远是白字）
        ["OnColorDark"]    = "#FFFFFFFF",   // 亮色块上的对钩（夜里是深色，白天改白）

        // ---- 强调 / 语义色 ----
        ["Accent"]         = "#FF2563EB",
        ["AccentHot"]      = "#FF1D4ED8",
        ["Accent2"]        = "#FF0E8FA8",   // 次强调（青色刻度线 / 插片标记）
        ["AccentSoftBg"]   = "#1A2563EB",
        ["AccentSoftBg2"]  = "#142563EB",
        ["Ok"]             = "#FF16A34A",
        ["Danger"]         = "#FFD92D20",
        ["DangerBorder"]   = "#FFF3C7C3",
        ["Warn"]           = "#FFB45309",
        ["WarnHot"]        = "#FFC2410C",
        ["RecRed"]         = "#FFDC2626",   // 录音红点
        ["KeepGreen"]      = "#FF15803D",   // "已保留"绿标
        ["Gold"]           = "#FFA67C00",   // 暖金（写作台分栏线 / 强调）
        ["InfoBg"]         = "#FFEAF3FD",   // 提示框底
        ["InfoBorder"]     = "#FFBCD4EE",
        ["InfoText"]       = "#FF1C4F7C",
        ["WarnBannerBg"]   = "#FFFFF6E1",   // 警示横条底
        ["InsertBg"]       = "#FFE9F7EF",   // 插片徽章底
        ["InsertCardBg"]   = "#FFEBF8FB",   // 插片卡底

        // ---- 时间轴 / 波形（EditorView + WaveformControl） ----
        ["BrushBackground"] = "#FFFFFFFF",
        ["BrushPanel"]      = "#FFF3F7FB",
        ["BrushBorder"]     = "#FFD3DCE6",
        ["BrushText"]       = "#FF2A3441",
        ["BrushAccent"]     = "#FFA67C00",
        ["BrushWave"]       = "#FF2B6CB0",
        ["BrushSelection"]  = "#4D2563EB",
        ["BrushPlayhead"]   = "#FFE11D48",
        ["WaveBlockNormal"] = "#FF4A7FB5",
        ["WaveBlockSel"]    = "#FFB8860B",
        ["WaveBlockBad"]    = "#FFC0453F",
        ["WaveBlockBorder"] = "#FFFFFFFF",
        ["WaveBlockLabel"]  = "#FFFFFFFF",

        // ---- 暗底上的"半透明白"（白天改成半透明黑，才看得见） ----
        ["WhiteSoftBg"]      = "#0F000000",
        ["WhiteSoftBg2"]     = "#0A000000",
        ["WhiteHairline"]    = "#1A000000",
        ["WhiteHoverBorder"] = "#33000000",
        ["WhiteSoftBorder"]  = "#26000000",
        ["TextOnDarkSoft"]   = "#FF3A4855",
        ["TextOnDarkDim"]    = "#FF5A6B7C",

        // ---- 其它 ----
        ["ShadowColor"] = "#FF22303C",      // 阴影（白天用浅灰蓝，不能纯黑）
        ["OverlayBg"]   = "#AA000000",      // 压在缩略图上的字幕条（两套都深）
        ["OverlayBg2"]  = "#B3000000",
        ["Transparent0"] = "#00000000",

        // ---- 窗口顶部细条 / 行底 / 深色小盒 ----
        ["BgRow"]          = "#FFEFF4FA",
        ["BgTopStrip"]     = "#FFF1F5F9",
        ["BgPanelDeep"]    = "#FFFAFBFD",
        ["BorderFaint"]    = "#FFC9D3DE",
        ["ItemSelBg"]      = "#FFE3EEFB",   // 列表选中项
        ["ItemHoverBg"]    = "#FFF0F4F9",   // 列表悬停项

        // ---- Color 类型资源（动画 / 阴影要 Color 不是 Brush） ----
        ["BorderColor"]     = "#FFD7DFE9",
        ["WarnHotColor"]    = "#FFC2410C",
        ["ShadowColorValue"] = "#FF22303C",

        // ---- 打卡热力图（浅色版：空格子浅、满格子橙） ----
        ["HeatEmpty"]      = "#FFEDF1F6",
        ["HeatStroke"]     = "#FFDBE3EC",
        ["Heat1"]          = "#FFFBE3A6",
        ["Heat1Stroke"]    = "#FFEFCF87",
        ["Heat2"]          = "#FFF5C24F",
        ["Heat2Stroke"]    = "#FFE0A72E",
        ["Heat3"]          = "#FFE08D18",
        ["Heat3Stroke"]    = "#FFC97B08",
        ["TodayRing"]      = "#FF64748B",
        ["HeatLabel"]      = "#FF64748B",
    };

    /// <summary>夜空黑的原始色值（启动时从 App.xaml 快照，见 CaptureNight）。</summary>
    private static Dictionary<string, Color>? _night;

    // ========================================================
    // 应用
    // ========================================================

    /// <summary>
    /// 切到指定主题。themeId 不认识就退回夜空黑。
    /// ★ 必须在 WPF 资源可用之后调用（App.InitializeComponent 之后）。
    /// </summary>
    public static void Apply(string? themeId)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        CaptureNight(res);

        string id = (themeId == DayId) ? DayId : NightId;
        CurrentId = id;

        if (id == NightId)
        {
            // 夜空黑 = 把快照原样写回去（不是"另一套色值"，保证永远等于改造前）
            foreach (var kv in _night!) SetColor(res, kv.Key, kv.Value);
        }
        else
        {
            foreach (var kv in Light)
            {
                if (!TryParse(kv.Value, out Color c)) continue;
                SetColor(res, kv.Key, c);
            }
        }

        // 资源铺完了再广播。★ 先换色再广播：收到通知的控件重画时拿到的已是新笔。
        try { Changed?.Invoke(); } catch { /* 某个控件重画失败不该拖累别的 */ }
    }

    /// <summary>把 App.xaml 里的原始颜色记下来（只在第一次调用时做）。</summary>
    private static void CaptureNight(ResourceDictionary res)
    {
        if (_night != null) return;

        _night = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var key in AllKeys(res))
        {
            if (res[key] is SolidColorBrush sb) _night[key] = sb.Color;
            else if (res[key] is Color c) _night[key] = c;
        }
    }

    /// <summary>资源里所有"颜色类"资源的 key（Brush 与 Color 两种，快照 / 自检都要用）。</summary>
    public static List<string> AllKeys(ResourceDictionary res)
    {
        var list = new List<string>();
        foreach (object key in res.Keys)
        {
            if (key is string s && (res[key] is SolidColorBrush || res[key] is Color)) list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// 改一支笔的颜色。
    /// ★ 万一某支笔被冻结了（改不动），就换成一支新的写回资源 ——
    ///   已经用 {StaticResource} 抓住旧笔的控件不会立刻变，
    ///   但至少新打开的窗口是对的。所以千万别主动 Freeze 这些资源。
    /// </summary>
    private static void SetColor(ResourceDictionary res, string key, Color c)
    {
        // Color 类型的资源（ColorAnimation 的 To / DropShadowEffect 的 Color）：直接换值
        if (res[key] is Color)
        {
            res[key] = c;
            return;
        }

        if (res[key] is SolidColorBrush sb && !sb.IsFrozen)
        {
            sb.Color = c;
            return;
        }
        res[key] = new SolidColorBrush(c);
    }

    private static bool TryParse(string hex, out Color c)
    {
        try
        {
            c = (Color)ColorConverter.ConvertFromString(hex)!;
            return true;
        }
        catch
        {
            c = Colors.Magenta;
            return false;
        }
    }

    // ========================================================
    // 给 C# 代码取色用（对话框、波形、热力图这些纯代码画的界面）
    // ========================================================

    /// <summary>
    /// 取当前主题下的一支笔。
    /// ★ 返回的是**资源里那一支**（同一个对象）—— 所以拿到之后不要 Freeze，
    ///   也不要缓存到 static readonly 字段里（那样切主题就换不掉了）。
    /// </summary>
    public static Brush Brush(string key)
    {
        if (Application.Current?.Resources[key] is Brush b) return b;
        return new SolidColorBrush(ColorOf(key));
    }

    /// <summary>取当前主题下的一个颜色（画图元的 Pen / Color / 标题栏用）。</summary>
    public static Color ColorOf(string key)
    {
        // Brush 类资源
        if (Application.Current?.Resources[key] is SolidColorBrush sb) return sb.Color;

        // Color 类资源（ColorAnimation 的 To / DropShadowEffect 的 Color）
        // ★ 这一支必须单独判：它不是 Brush，走上面那条会漏，然后一路掉到兜底
        //   变成"永远是夜空黑的影子色"。
        if (Application.Current?.Resources[key] is Color c) return c;

        // 资源还没建好（无头测试里没调 App.InitializeComponent）→ 用夜空黑的值兜底
        if (_night != null && _night.TryGetValue(key, out Color n)) return n;
        if (Light.TryGetValue(key, out string? hex) && TryParse(hex, out Color d)) return d;
        return Colors.Gray;
    }
}
