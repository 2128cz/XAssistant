using System.ComponentModel;
using System.Windows;
using XAssistant; // 引入 App 类以访问 IsShuttingDown

namespace XAssistant.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 如果不是通过托盘菜单“退出”，则隐藏窗口而不是关闭
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
