// ============================================================
// ReviewHostView.xaml.cs - ★ V1.15.0 复盘的"两层容器"
// ============================================================
//
// 【它只做三件事】
//   ① 进来默认落在第一层（账号页）—— 点左边「复盘」第一眼就是热力图；
//   ② 收到"要打开某支片"就换上第二层（作品页），并显示返回条；
//   ③ 点「← 返回账号」回第一层。
//
// 【为什么把「返回」放在容器上，不改作品页】
//   作品页（ReviewView）本来就在项目里用过、现在一个字没改就能当第二层 ——
//   "往哪返回"是**导航**的事，不是页面的事。放容器上，作品页就还能被别处复用。
// ============================================================

using System.Windows;
using System.Windows.Controls;
using LiteCut.Workbench.Navigation;
using LiteCut.Workbench.Project;

namespace LiteCut.Workbench.Views;

public partial class ReviewHostView : UserControl, INavPage
{
    private AccountReviewView? _account;
    private ReviewView? _work;

    public ReviewHostView()
    {
        InitializeComponent();
    }

    // ========================================================
    // 导航生命周期
    // ========================================================

    /// <summary>
    /// 从菜单点进「复盘」= 回到第一层。
    /// 已经在复盘页时再点一次菜单，也会回到账号页 —— 这是符合直觉的"回首页"。
    /// </summary>
    public void OnEnter() => ShowAccount();

    public void OnLeave() { }

    // ========================================================
    // 第一层：账号页
    // ========================================================

    private void ShowAccount()
    {
        if (_account == null)
        {
            _account = new AccountReviewView();
            _account.OpenWorkRequested += ShowWork;   // 账号页说"要打开这支片" → 这里接住
        }

        Body.Content = _account;
        _account.OnEnter();          // 每次回来重新汇总（刚在作品页改过数据，这里要立刻反映）

        BackBar.Visibility = Visibility.Collapsed;
        TxtCrumb.Text = "";
    }

    // ========================================================
    // 第二层：作品页（= 原来那个 ReviewView，一行没改）
    // ========================================================

    private void ShowWork(ProjectRef project)
    {
        if (project == null) return;

        if (_work == null) _work = new ReviewView();

        Body.Content = _work;                 // 先上屏（这样它才在视觉树里）
        _work.ShowProject(project);           // 再指定"看哪一支片"

        TxtCrumb.Text = "作品：" + project.Model.Name;
        BackBar.Visibility = Visibility.Visible;

        WorkbenchWindow.FindOwner(this)?.Status($"进入「{project.Model.Name}」的作品复盘");
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e) => ShowAccount();
}
