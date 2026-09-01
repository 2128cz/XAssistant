using System.Windows;
using System.Windows.Input;
using XAssistant.ViewModels;

namespace XAssistant.Views;

/// <summary>
/// 键盘动画分离窗口：透明、无边框、置顶。
/// - 内容为 Composition 模式 WebView2（AllowsTransparency 下唯一可交互方案）
/// - 全页面拖拽由页面 JS 发送 dragDelta 消息实现；顶栏保留 WPF 拖拽作备用手柄
/// - 页面「背景：透明」开关控制透视效果
/// </summary>
public partial class KeyAnimationWindow : Window
{
    private static KeyAnimationWindow? _instance;

    public KeyAnimationWindow()
    {
        InitializeComponent();
        Activated += (_, _) => Overlay.FocusWebView();
    }

    /// <summary>打开（或激活）键盘动画分离窗口；单实例复用</summary>
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

    /// <summary>按页面拖拽增量移动窗口（由视图转发 dragDelta 消息调用）</summary>
    public void MoveBy(double dx, double dy)
    {
        Left += dx;
        Top += dy;
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