using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

/// <summary>The desktop hosts one dashboard. All sections share the existing service instances.</summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IStartupService _startupService;
    private readonly ILogBufferService _logBuffer;
    private readonly IConfigurationService _configService;
    private readonly DispatcherTimer _logRefreshTimer;
    private volatile bool _logsDirty;
    public DashboardViewModel Dashboard { get; }

    [ObservableProperty]
    private double _windowWidth;

    [ObservableProperty]
    private double _windowHeight;

    [ObservableProperty]
    private bool _isLogExpanded;

    [ObservableProperty]
    private bool _isStartWithWindowsEnabled;

    [ObservableProperty]
    private string _logLevelFilter = "All";

    [ObservableProperty]
    private DateTime _logDate = DateTime.Today;

    [ObservableProperty]
    private string _exportStatus = "记录由现有服务保存";

    /// <summary>事件列表的物化快照，仅在日志弹窗展开时才重建</summary>
    [ObservableProperty]
    private LogEntry[] _filteredLogs = [];

    /// <summary>当前筛选条件下的日志条数；按钮角标常驻可见，因此收起时也会更新</summary>
    [ObservableProperty]
    private int _filteredLogCount;

    public string[] LogLevelOptions { get; } = ["All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    public ObservableCollection<LogEntry> AllLogs => _logBuffer.LogEntries;
    public string StartupDescription => IsStartWithWindowsEnabled ? "已开启 · 登录系统后在后台记录" : "已关闭 · 手动启动工作台";

    public MainWindowViewModel(IStartupService startupService, ILogBufferService logBuffer,
        IConfigurationService configService, DashboardViewModel dashboard)
    {
        _startupService = startupService;
        _logBuffer = logBuffer;
        _configService = configService;
        Dashboard = dashboard;
        // Reading configuration must not write back to the registry or configuration file.
        _windowWidth = Math.Max(1100, configService.GetWindowWidth());
        _windowHeight = Math.Max(700, configService.GetWindowHeight());
        _isLogExpanded = configService.GetIsLogExpanded();
        _isStartWithWindowsEnabled = startupService.IsStartWithWindowsEnabled();
        AllLogs.CollectionChanged += LogsChanged;

        // 日志去抖：每来一条只置脏标记，由定时器统一刷一次，避免逐条重建列表
        _logRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _logRefreshTimer.Tick += (_, _) =>
        {
            if (_logsDirty)
                RefreshLogView();
        };
        _logRefreshTimer.Start();
        RefreshLogView();
    }

    partial void OnWindowWidthChanged(double value) => _configService.SetWindowWidth(value);
    partial void OnWindowHeightChanged(double value) => _configService.SetWindowHeight(value);
    partial void OnIsStartWithWindowsEnabledChanged(bool value)
    {
        _startupService.SetAutoStart(value);
        OnPropertyChanged(nameof(StartupDescription));
    }
    partial void OnLogLevelFilterChanged(string value) => RefreshLogView();
    partial void OnLogDateChanged(DateTime value) => RefreshLogView();
    partial void OnIsLogExpandedChanged(bool value)
    {
        _configService.SetIsLogExpanded(value);
        // 刚展开时立即物化一次，保证弹窗打开就有内容
        if (value)
            RefreshLogView();
    }

    /// <summary>集合变更只置脏标记，O(1)；真正的快照由 _logRefreshTimer 合并处理</summary>
    private void LogsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _logsDirty = true;

    private void RefreshLogView()
    {
        _logsDirty = false;
        FilteredLogCount = CountMatchedLogs();

        // 事件列表位于 Popup 内，收起时不需要物化，否则常驻运行会持续重建上千行容器
        if (IsLogExpanded)
            FilteredLogs = BuildFilteredLogs();
    }

    /// <summary>条数只遍历计数，不分配中间数组：按钮角标常驻可见，这条路径每 0.5 秒都会走</summary>
    private int CountMatchedLogs()
    {
        var date = LogDate.Date;
        var level = LogLevelFilter;
        int count = 0;
        BindingOperations.AccessCollection(
            AllLogs,
            () =>
            {
                foreach (var entry in AllLogs)
                {
                    if (entry.Timestamp.Date == date
                        && (level == "All"
                            || entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase)))
                        count++;
                }
            },
            false
        );
        return count;
    }

    private LogEntry[] BuildFilteredLogs()
    {
        var date = LogDate.Date;
        var level = LogLevelFilter;
        LogEntry[] result = [];
        BindingOperations.AccessCollection(
            AllLogs,
            () =>
                result = AllLogs
                    .Where(entry =>
                        entry.Timestamp.Date == date
                        && (level == "All"
                            || entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                    )
                    .OrderByDescending(entry => entry.Timestamp)
                    .ToArray(),
            false
        );
        return result;
    }
    [RelayCommand]
    private void ClearLogs()
    {
        BindingOperations.AccessCollection(AllLogs, AllLogs.Clear, true);
        RefreshLogView(); // 角标归零要立即生效，不等去抖定时器
    }
    [RelayCommand] private void CloseLogs() => IsLogExpanded = false;

    [RelayCommand]
    private void ExportRecords()
    {
        SaveJson("XAssistant-records", new
        {
            ExportedAt = DateTimeOffset.Now,
            KeyboardPeriod = Dashboard.SelectedKeyboardPeriod,
            Keys = Dashboard.DisplayKeyCounts.Select(key => new { key.Key, key.Count }).ToArray(),
            Mouse = new { Dashboard.Mouse.LeftClickCount, Dashboard.Mouse.MiddleClickCount, Dashboard.Mouse.RightClickCount,
                Dashboard.Mouse.LeftClickToday, Dashboard.Mouse.MiddleClickToday, Dashboard.Mouse.RightClickToday,
                Dashboard.Mouse.SelectedDate, Dashboard.Mouse.SelectedDateLeftCount, Dashboard.Mouse.SelectedDateMiddleCount, Dashboard.Mouse.SelectedDateRightCount },
            Usage = Dashboard.Usage.History.ToArray(),
            SessionEvents = Dashboard.Usage.SessionEvents.ToArray(),
            ApplicationsDate = Dashboard.Apps.SelectedDate,
            Applications = Dashboard.Apps.AppUsageList.ToArray()
        });
    }
    [RelayCommand] private void ExportLogs() => SaveJson("XAssistant-events", BuildFilteredLogs());

    private void SaveJson(string prefix, object value)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出记录", Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            ExportStatus = $"已导出 · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ExportStatus = "导出失败 · " + exception.Message;
        }
    }
    public void Dispose()
    {
        _logRefreshTimer.Stop();
        AllLogs.CollectionChanged -= LogsChanged;
        Dashboard.Dispose();
    }
}
