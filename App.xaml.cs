using System;
using System.Runtime.Versioning;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using XAssistant.Services;
using XAssistant.Services.Interfaces;
using XAssistant.ViewModels;
using XAssistant.Views;

namespace XAssistant;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // 服务（单例，共享状态）
        services.AddSingleton<IMouseClickHookService, MouseClickHookService>();
        services.AddSingleton<IClickDatabaseService, ClickDatabaseService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IStartupService>(_ => new StartupService("XAssistant"));
        services.AddSingleton<IKeyboardHookService, KeyboardHookService>();
        services.AddSingleton<IKeyDatabaseService, KeyDatabaseService>();
        services.AddSingleton<ClickCounterViewModel>();
        services.AddSingleton<KeyCounterViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        Services = provider;

        var mainVM = provider.GetRequiredService<MainWindowViewModel>();
        // 启动默认视图
        mainVM.NavigateCommand.Execute("ClickCounter");

        var mainWindow = new MainWindow { DataContext = mainVM };
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
