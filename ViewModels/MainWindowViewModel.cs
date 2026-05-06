using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IStartupService _startupService;
    private readonly ILogBufferService _logBuffer;
    private readonly IConfigurationService _configService;

    [ObservableProperty]
    private double _windowWidth;

    partial void OnWindowWidthChanged(double value) => _configService.SetWindowWidth(value);

    [ObservableProperty]
    private double _windowHeight;

    partial void OnWindowHeightChanged(double value) => _configService.SetWindowHeight(value);

    [ObservableProperty]
    private bool _isLogExpanded;

    partial void OnIsLogExpandedChanged(bool value) => _configService.SetIsLogExpanded(value);

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    [ObservableProperty]
    private bool _isStartWithWindowsEnabled;

    // 日志集合（直接暴露底层集合，也可以做筛选）
    public ObservableCollection<LogEntry> AllLogs => _logBuffer.LogEntries;

    [ObservableProperty]
    private string _logLevelFilter = "All";

    public string[] LogLevelOptions { get; } =
        { "All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    // 计算属性：展示筛选后的日志（也可以在 xaml 中用 CollectionViewSource 过滤）
    public IEnumerable<LogEntry> FilteredLogs =>
        LogLevelFilter == "All"
            ? AllLogs
            : AllLogs.Where(l =>
                l.Level.Equals(LogLevelFilter, StringComparison.OrdinalIgnoreCase)
            );

    public MainWindowViewModel(
        IStartupService startupService,
        ILogBufferService logBuffer,
        IConfigurationService configService
    )
    {
        _startupService = startupService;
        _logBuffer = logBuffer;
        _configService = configService;
        WindowWidth = _configService.GetWindowWidth();
        WindowHeight = _configService.GetWindowHeight();
        IsLogExpanded = _configService.GetIsLogExpanded();

        IsStartWithWindowsEnabled = _startupService.IsStartWithWindowsEnabled();

        // 当日志集合变化时，通知 FilteredLogs 属性变化（简化方式）
        _logBuffer.LogEntries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FilteredLogs));
        };
    }

    // 当日志筛选级别改变时，通知 FilteredLogs 更新
    partial void OnLogLevelFilterChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredLogs));
    }

    // 清空日志
    [RelayCommand]
    private void ClearLogs()
    {
        _logBuffer.LogEntries.Clear();
    }

    partial void OnIsStartWithWindowsEnabledChanged(bool value)
    {
        _startupService.SetAutoStart(value);
    }

    [RelayCommand]
    private void Navigate(string pageName)
    {
        CurrentViewModel = pageName switch
        {
            "ClickCounter" => App.Services.GetRequiredService<ClickCounterViewModel>(),
            "KeyCounter" => App.Services.GetRequiredService<KeyCounterViewModel>(),
            "Usage" => App.Services.GetRequiredService<UsageViewModel>(),
            _ => CurrentViewModel,
        };
    }
}
