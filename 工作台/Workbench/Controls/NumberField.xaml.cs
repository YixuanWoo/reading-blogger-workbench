// ============================================================
// NumberField.xaml.cs - ★ V1.15.0 可输入的数字卡（通用控件）
// ============================================================
//
// 【对外就三样】
//   Title / Foot  —— 上面标签、下面脚注
//   ValueText     —— 中间那个大字（读/写都用它）
//   Value         —— 解析成数字（认不出来算 0）；SetValue 能区分"没填"和"0"
//
// 【"没填"和"0"必须分开】
//   粉丝总量 0 和"还没抄过"是两件事。前者说明号还空着，后者说明我还没去后台看。
//   所以没填的时候框是空的、显示灰的"—"，而不是把 0 填进去。
// ============================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LiteCut.Workbench.Controls;

public partial class NumberField : UserControl
{
    public NumberField()
    {
        InitializeComponent();
        RefreshPlaceholder();
    }

    /// <summary>上面那行标签（如"粉丝总量"）。</summary>
    public string Title
    {
        get => TxtTitle.Text;
        set => TxtTitle.Text = value ?? "";
    }

    /// <summary>下面那行脚注（如"你手填的"）。留空就整行收掉。</summary>
    public string Foot
    {
        get => TxtFoot.Text;
        set
        {
            TxtFoot.Text = value ?? "";
            TxtFoot.Visibility = string.IsNullOrWhiteSpace(value)
                                 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>中间那个大字。自己负责格式化。</summary>
    public string ValueText
    {
        get => TxtValue.Text;
        set
        {
            TxtValue.Text = value ?? "";
            RefreshPlaceholder();
        }
    }

    /// <summary>框里那个字（给遥控口/测试读用，也是界面上真实显示的字）。</summary>
    public TextBox ValueBox => TxtValue;

    /// <summary>解析成数字。认不出来（空、乱敲）算 0 —— 跟复盘表单同一套宽容口径。</summary>
    public long Value
    {
        get
        {
            string s = (TxtValue.Text ?? "").Trim().Replace(",", "").Replace("，", "").Replace(" ", "");
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }

    /// <summary>有没有填（空 = 没填）。</summary>
    public bool HasValue => !string.IsNullOrWhiteSpace(TxtValue.Text);

    /// <summary>外部写值。没有数据时传 has=false，框会留空并显示灰提示。</summary>
    public void SetValue(long v, bool has) => ValueText = has ? v.ToString("N0") : "";

    /// <summary>灰色提示文字（默认 "—"）。</summary>
    public void SetPlaceholder(string s) => Ph.Text = string.IsNullOrWhiteSpace(s) ? "—" : s;

    private void TxtValue_TextChanged(object sender, TextChangedEventArgs e) => RefreshPlaceholder();

    private void RefreshPlaceholder()
    {
        if (Ph == null || TxtValue == null) return;
        Ph.Visibility = TxtValue.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
