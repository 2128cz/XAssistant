using System.Windows;
using System.Windows.Input;
using XAssistant.ViewModels;

namespace XAssistant.Controls;

/// <summary>
/// 消息与播放队列面板。✕ 打在 DataTemplate 里，它的 DataContext 是行对象而不是视图模型，
/// 所以停止动作在代码里回到宿主取一次命令——与词频面板那两枚按钮同一做法。
/// </summary>
public partial class MessageQueuePanel : System.Windows.Controls.UserControl
{
    public MessageQueuePanel() => InitializeComponent();

    private void StopRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EffectRow row } && DataContext is MessageQueueViewModel vm)
            vm.StopCommand.Execute(row);
        e.Handled = true;
    }
}
