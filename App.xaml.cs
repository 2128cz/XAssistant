using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using XAssistant.Services;
using XAssistant.Services.Interfaces;
using XAssistant.ViewModels;
using XAssistant.Views;

namespace XAssistant;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private NotifyIcon? _notifyIcon;
    internal static bool IsShuttingDown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // 服务（单例，共享状态）
        services.AddSingleton<IMouseClickHookService, MouseClickHookService>();
        services.AddSingleton<IClickDatabaseService, ClickDatabaseService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
#if DEBUG
        services.AddSingleton<IStartupService>(_ => new StartupService("XAssistant_Dev"));
#else
        services.AddSingleton<IStartupService>(_ => new StartupService("XAssistant"));
#endif
        services.AddSingleton<IKeyboardHookService, KeyboardHookService>();
        services.AddSingleton<IKeyDatabaseService, KeyDatabaseService>();
        services.AddSingleton<ClickCounterViewModel>();
        services.AddSingleton<KeyCounterViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        Services = provider;

        var mainVM = provider.GetRequiredService<MainWindowViewModel>();
        mainVM.NavigateCommand.Execute("ClickCounter");

        var mainWindow = new MainWindow { DataContext = mainVM };
        MainWindow = mainWindow;
        mainWindow.Show();

        // 初始化系统托盘
        InitializeNotifyIcon(mainWindow);
    }

    private void InitializeNotifyIcon(Window mainWindow)
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = GetAppIcon(),
            Visible = true,
            Text = "XAssistant",
        };

        // 左键单击托盘图标 → 显示主窗口
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowMainWindow(mainWindow);
            }
        };

        // 右键菜单
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("显示", null, (_, _) => ShowMainWindow(mainWindow));
        contextMenu.Items.Add("退出", null, (_, _) => ShutdownApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private static void ShowMainWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private void ShutdownApplication()
    {
        IsShuttingDown = true;
        _notifyIcon!.Visible = false;
        _notifyIcon.Dispose();
        Shutdown();
    }

    private static Icon GetAppIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon != null)
                return icon;
        }
        catch
        {
            // 忽略错误，使用默认图标
        }
        return SystemIcons.Application;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }
}
