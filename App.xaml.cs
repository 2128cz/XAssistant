using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
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

    private ILogger<App>? _appLogger;

    public App()
    {
        // 全局 UI 线程异常
        DispatcherUnhandledException += (_, e) =>
        {
            _appLogger?.LogCritical(e.Exception, "未处理的 UI 线程异常");
            e.Handled = true; // 防止进程崩溃，但记录日志
        };

        // 应用程序域未处理异常（通常导致进程退出）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _appLogger?.LogCritical(e.ExceptionObject as Exception, "未处理的应用程序域异常");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---------- 1. 准备日志目录 ----------
        string logDir = AppDataPathHelper.GetAppDataFolder();
        string logPath = System.IO.Path.Combine(logDir, "logs", "xassistant-.log");

        var services = new ServiceCollection();

        // ---------- 2. 创建 LogBufferService 实例并提前注册 ----------
        // 这样 Serilog 配置和 DI 都使用同一个实例，且无需提前 Build 容器
        var logBuffer = new LogBufferService();
        services.AddSingleton<ILogBufferService>(logBuffer);

        // ---------- 3. 注册其他应用服务 ----------
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
        services.AddSingleton<UsageViewModel>();
        services.AddSingleton<LogViewerViewModel>();

        // ---------- 4. 配置 Serilog Logger ----------
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            )
            .WriteTo.Sink(new UiLogSink(logBuffer))
            .CreateLogger();

        // ---------- 5. 添加日志服务到 DI ----------
        services.AddLogging(builder => builder.AddSerilog());

        // ---------- 6. 构建容器 ----------
        var provider = services.BuildServiceProvider();
        Services = provider;

        // ---------- 7. 获取系统日志记录器 ----------
        _appLogger = provider.GetRequiredService<ILogger<App>>();
        _appLogger.LogInformation("═══════ XAssistant 启动成功 ═══════");

        // 后续主窗口……
        var mainVM = provider.GetRequiredService<MainWindowViewModel>();
        mainVM.NavigateCommand.Execute("ClickCounter");

        var mainWindow = new MainWindow { DataContext = mainVM };
        MainWindow = mainWindow;
        mainWindow.Show();

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

        _appLogger?.LogInformation("用户触发退出，应用即将关闭");
        Log.CloseAndFlush();
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
        _appLogger?.LogInformation("应用 OnExit 执行");
        _notifyIcon?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
