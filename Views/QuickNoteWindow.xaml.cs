using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using XAssistant.Models;
using XAssistant.ViewModels;

namespace XAssistant.Views;

// 速记原生捕获窗：无边框置顶小窗，Ctrl+Enter 保存 / Esc 取消，多行输入自动聚焦
public partial class QuickNoteWindow : Window
{
    private const byte VkMenu = 0x12; // 虚拟键：Alt（前台锁兜底用）
    private const uint KeyEventfExtendedKey = 0x0001;
    private const uint KeyEventfKeyUp = 0x0002;

    private readonly QuickNoteViewModel _viewModel;

    public QuickNoteWindow(QuickNoteViewModel viewModel, QuickNoteSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        _viewModel.SaveSucceeded += OnSaveSucceeded;
    }

    private void OnSaveSucceeded() => Close();

    // ✕ 关闭按钮：不保存直接关闭（与 Esc 语义一致）
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // 全限定类型：项目同时启用 WPF 与 WinForms，KeyEventArgs 存在歧义
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Esc 取消不保存；Ctrl+Enter 保存。用 Preview 事件提前拦截，避免 TextBox 把 Enter 当换行
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (
            e.Key == Key.Enter
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && _viewModel.SaveCommand.CanExecute(null)
        )
        {
            _viewModel.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // 已打开时再次唤起：置顶聚焦输入框，不覆盖已输入内容与来源
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        ForceForeground(); // 全局热键唤起时后台进程可能被前台锁拦截，需显式抢前台焦点
        FocusContent();
    }

    // 抢前台焦点：直接 SetForegroundWindow；被前台锁拒绝时模拟一次 Alt 键后重试（注册热键的进程通常已被授予前台权限，此为兜底）
    private void ForceForeground()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        if (SetForegroundWindow(handle))
            return;
        keybd_event(VkMenu, 0, KeyEventfExtendedKey, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyEventfExtendedKey | KeyEventfKeyUp, UIntPtr.Zero);
        SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FocusContent();
    }

    private void FocusContent()
    {
        ContentBox.Focus();
        ContentBox.CaretIndex = ContentBox.Text.Length; // 光标移到末尾
        Keyboard.Focus(ContentBox);
    }
}
