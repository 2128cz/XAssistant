using System.Windows;
using System.Windows.Input;
using XAssistant.ViewModels;

namespace XAssistant.Views;

/// <summary>
/// 键盘动画独立窗口：透明、无边框、置顶，支持拖拽移动。
/// 背景透明由页面内「背景：透明」开关控制（透视效果）。
/// </summary>
public partial class KeyAnimationWindow : Window
{
    private static KeyAnimationWindow? _instance;

    public KeyAnimationWindow()
    {
        InitializeComponent();
    }

    /// <summary>打开（或激活）键盘动画窗口；单实例复用</summary>
    public static void EnsureOpen(KeyAnimationViewModel viewModel)
    {
        if (_instance == null || !_instance.IsLoaded)
        {
            _instance = new KeyAnimationWindow { DataContext = viewModel };
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}