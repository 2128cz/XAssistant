using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XAssistant.ViewModels;
// 主工程开了 UseWindowsForms，隐式 using 里的 CheckBox / TextBox / Control 会跟 WPF 的撞名
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;

namespace XAssistant.Controls;

/// <summary>
/// 监视模块面板的代码后置。这里只做三件事：把控件事件翻译成「哪张卡的哪一行提交了什么值」、
/// 拦一下非数字输入、把实测空间回传给模块。真正的规则都在注册表与模块里，面板不判断业务。
/// </summary>
public partial class WatchModulesPanel : System.Windows.Controls.UserControl
{
    public WatchModulesPanel() => InitializeComponent();

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModuleCardViewModel card }) card.ToggleCommand.Execute(null);
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ModuleRowViewModel row }) return;
        if (row.Owner is { } card) card.Push(row, row.Check);
    }

    private void Text_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ModuleRowViewModel row }) return;
        if (row.Owner is { } card) card.Push(row, row.Text);
    }

    private void Secret_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { Tag: ModuleRowViewModel row }) return;
        if (row.Owner is { } card) card.Push(row, ((PasswordBox)sender).Password);
    }

    /// <summary>卡片重建后把存着的保密值回填进密码框（密码框不支持绑定，只能这样接回去）。</summary>
    private void Secret_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { Tag: ModuleRowViewModel row }) row.Password = (PasswordBox)sender;
    }

    /// <summary>元数据说「只允许数字」的行：拦掉字母之类，小数点、负号、千分位逗号放过。</summary>
    private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not Control { Tag: true }) return;
        e.Handled = e.Text.Any(c => !char.IsDigit(c) && c is not ('.' or '-' or ','));
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModuleCardViewModel card })
            card.ReportSpace(e.NewSize.Width, e.NewSize.Height);
    }
}
