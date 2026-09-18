using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using XAssistant.Services;
using XAssistant.Services.Interfaces;
using XAssistant.Services.Keywords;
using XAssistant.Services.QuickNote;
using XAssistant.ViewModels;
using XAssistant.Views;

namespace XAssistant;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private NotifyIcon? _notifyIcon;
    private QuickNoteCaptureService? _quickNoteCapture;
    private GlobalHotkeyService? _globalHotkey;
    private NotificationPipeServer? _notifyPipe;
    private AgentErrorWatch? _agentWatch;
    internal static bool IsShuttingDown { get; private set; }

    /// <summary>这轮进程只是来放一个效果的（--fx）：不建容器、不装钩子、不开主窗。</summary>
    private static bool _headlessEffect;

    private ILogger<App>? _appLogger;
    private bool _isDataSaved;

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

        // 无头效果：`XAssistant.exe --fx warn 3 "AI Computer Use"` / `--fx confetti` / `--fx off`。
        // 排在一切启动工作前面：脚本与 MCP 调一次只想要一个动画，不该顺手装钩子、开数据库、弹主窗。
        _headlessEffect = EffectCli.TryHandle(e.Args, this);
        if (_headlessEffect) return;

        // 常驻托盘的程序不能让「最后一个窗口关掉」决定进程寿命：默认模式下，彩蛋粒子窗、
        // 悬浮键盘动画窗这类关掉就没的窗口一旦成为最后一个窗口，就会把整个程序带走。
        // 退出只由托盘菜单的「退出」与系统注销触发（两处都已各自走 SaveDataAndStopTracker）。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 准备日志目录
        string logDir = AppDataPathHelper.GetAppDataFolder();
        string logPath = System.IO.Path.Combine(logDir, "logs", "xassistant-.log");

        var services = new ServiceCollection();

        // 创建 LogBufferService 实例并提前注册
        // 这样 Serilog 配置和 DI 都使用同一个实例，且无需提前 Build 容器
        var logBuffer = new LogBufferService();
        services.AddSingleton<ILogBufferService>(logBuffer);

        // 注册其他应用服务
        services.AddSingleton<PracticeViewModel>(sp => { var vm = PracticeViewModel.CreateDefault(); vm.ConnectKeyboard(sp.GetRequiredService<IKeyboardHookService>()); return vm; });
        services.AddSingleton<WordFrequencyViewModel>(sp => {
            _ = sp.GetRequiredService<IKeyDatabaseService>();
            var folder = AppDataPathHelper.GetAppDataFolder();
            return new WordFrequencyViewModel(new WordFrequencyStore(System.IO.Path.Combine(folder, "key_data.db"), System.IO.Path.Combine(folder, "word_frequency.db"), System.IO.Path.Combine(folder, "word_marks.db")));
        });
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<InputEventDispatcher>();
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
        services.AddSingleton<AppUsageViewModel>();
        services.AddSingleton<LogViewerViewModel>();
        services.AddSingleton<KeyAnimationViewModel>();
        services.AddSingleton<SettingsViewModel>();

        services.AddSingleton<ProcessUsageTracker>();

        // 打字关键词彩蛋（white / black / flower …）：切词与词频统计共用同一条物理键规则，
        // 命中就换本次运行的主题并掉一把粒子。它跟着钩子走，所以键盘记录开着才生效。
        services.AddSingleton(sp => KeywordCatalog.Load(KeywordCatalog.DefaultRoot));
        services.AddSingleton<KeywordWatcher>();

        // 速记唤起（全局热键 + 原生捕获窗 + 直插速记库 + 保存确认 toast）
        services.AddSingleton<IQuickNoteDatabaseService, QuickNoteDatabaseService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<QuickNoteCaptureService>();

        // 配置 Serilog Logger：调试构建保留 Debug 级别便于排查，
        // 正式版只到 Information，避免常驻时日志目录涨到几十 MB
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            )
            .WriteTo.Sink(new UiLogSink(logBuffer))
            .CreateLogger();

        // 添加日志服务到 DI
        services.AddLogging(builder => builder.AddSerilog());

        // 构建容器
        var provider = services.BuildServiceProvider();
        Services = provider;

        // 启动进程追踪
        var processTracker = provider.GetRequiredService<ProcessUsageTracker>();
        processTracker.Start();

        // 获取系统日志记录器
        _appLogger = provider.GetRequiredService<ILogger<App>>();
        _appLogger.LogInformation("═══════ XAssistant 启动成功 ═══════");

        // 应用持久化的界面主题（默认浅色）
        var configService = provider.GetRequiredService<IConfigurationService>();
        ThemeManager.Apply(configService.GetTheme());
        _appLogger.LogInformation("已应用界面主题：{Theme}", ThemeManager.Current);

        // xa 命令的接手方：无头实例与本程序同时在跑时，整条命令交给这里执行——
        // 效果窗与顶部持久消息栈只归一份，而持久窗只有常驻进程养得住（见 NotificationPipe）
        _notifyPipe = new NotificationPipeServer();
        _notifyPipe.LineReceived += line => Dispatcher.Invoke(() => EffectDispatch.OnLine(line));
        if (!_notifyPipe.TryStart())
            _appLogger.LogInformation("xa 转发管道已被别的实例占着：命令将由发起方本地执行");

        // 对话报错哨兵：quota/限流这类模型层错误不在任何 hook 事件流里，只能盯 IDE 自己写的
        // agent.log 状态机迁移（prompting -> error）——报成红档，免得对话卡在异地的报错屏上没人知道
        _agentWatch = new AgentErrorWatch();

        // 关键词引擎订阅钩子：只订事件，装钩子仍是键盘记录自己的事
        provider.GetRequiredService<KeywordWatcher>()
            .ConnectKeyboard(provider.GetRequiredService<IKeyboardHookService>());

        // 后续主窗口
        var mainVM = provider.GetRequiredService<MainWindowViewModel>();

        var mainWindow = new MainWindow { DataContext = mainVM };
        MainWindow = mainWindow;
        mainWindow.Show();

        // 速记唤起：注册全局热键，热键按下 → 唤起捕获窗
        _quickNoteCapture = provider.GetRequiredService<QuickNoteCaptureService>();
        _globalHotkey = provider.GetRequiredService<GlobalHotkeyService>();
        _globalHotkey.HotKeyPressed += () => _quickNoteCapture.InvokeCapture();
        _globalHotkey.Start();

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
        contextMenu.Items.Add("打开速记窗", null, (_, _) => _quickNoteCapture?.InvokeCapture());
        contextMenu.Items.Add("速记列表", null, (_, _) => _quickNoteCapture?.OpenList());
        contextMenu.Items.Add(new ToolStripSeparator());
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
        if (_isDataSaved)
            return;
        IsShuttingDown = true;
        _notifyIcon!.Visible = false;
        _notifyIcon.Dispose();

        SaveDataAndStopTracker(); // 复用统一逻辑
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

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        SaveDataAndStopTracker();
    }

    private void SaveDataAndStopTracker()
    {
        if (_isDataSaved)
            return;
        _isDataSaved = true;

        _appLogger?.LogInformation("系统正在关闭/注销，保存进程使用数据...");
        try
        {
            var tracker = Services.GetRequiredService<ProcessUsageTracker>();
            tracker.Stop();
            tracker.Dispose();
        }
        catch (Exception ex)
        {
            _appLogger?.LogError(ex, "停止进程追踪器失败");
        }

        // 按键/点击是在后台线程攒批落库的，退出前把队列排空，否则最后几十毫秒的记录会丢
        try
        {
            Services.GetRequiredService<IKeyDatabaseService>().Flush();
            Services.GetRequiredService<IClickDatabaseService>().Flush();
        }
        catch (Exception ex)
        {
            _appLogger?.LogError(ex, "排空落库队列失败");
        }

        Log.CloseAndFlush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 无头那轮什么都没建，去容器里取服务只会抛空引用
        if (_headlessEffect)
        {
            base.OnExit(e);
            return;
        }
        // 防止重复保存（如果已经通过 SessionEnding 或 ShutdownApplication 保存过）
        SaveDataAndStopTracker();

        Services.GetRequiredService<IKeyboardHookService>().Stop();

        Services.GetRequiredService<InputEventDispatcher>().Dispose();
        Services.GetRequiredService<KeyCounterViewModel>().Dispose();
        Services.GetRequiredService<KeyAnimationViewModel>().Dispose();
        Services.GetRequiredService<ClickCounterViewModel>().Dispose();
        Services.GetRequiredService<IMouseClickHookService>().Stop();
        try { (Services.GetRequiredService<IKeyDatabaseService>() as IDisposable)?.Dispose(); }
        catch (Exception error) { _appLogger?.LogError(error, "Keyboard database flush failed"); }
        try { (Services.GetRequiredService<IClickDatabaseService>() as IDisposable)?.Dispose(); }
        catch (Exception error) { _appLogger?.LogError(error, "Mouse database flush failed"); }
        Services.GetRequiredService<MainWindowViewModel>().Dispose();
        Services.GetRequiredService<WordFrequencyViewModel>().Dispose();
        Services.GetRequiredService<PracticeViewModel>().Dispose();
        Services.GetRequiredService<KeywordWatcher>().Dispose();
        _agentWatch?.Dispose();
        _notifyPipe?.Dispose();
        _globalHotkey?.Dispose();
        _notifyIcon?.Dispose();
        // 移除事件订阅，避免内存泄漏
        SystemEvents.SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }
}
