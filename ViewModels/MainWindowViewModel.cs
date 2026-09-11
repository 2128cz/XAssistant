using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
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
    public DashboardViewModel Dashboard { get; }

    [ObservableProperty] private double _windowWidth;
    [ObservableProperty] private double _windowHeight;
    [ObservableProperty] private bool _isLogExpanded;
    [ObservableProperty] private bool _isStartWithWindowsEnabled;
    [ObservableProperty] private string _logLevelFilter = "All";
    [ObservableProperty] private DateTime _logDate = DateTime.Today;
    [ObservableProperty] private string _exportStatus = "记录由现有服务保存";

    public string[] LogLevelOptions { get; } = ["All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    public ObservableCollection<LogEntry> AllLogs => _logBuffer.LogEntries;
    public IEnumerable<LogEntry> FilteredLogs => SnapshotLogs().Where(entry => entry.Timestamp.Date == LogDate.Date
        && (LogLevelFilter == "All" || entry.Level.Equals(LogLevelFilter, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(entry => entry.Timestamp);
    public int FilteredLogCount => FilteredLogs.Count();
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
    }

    partial void OnWindowWidthChanged(double value) => _configService.SetWindowWidth(value);
    partial void OnWindowHeightChanged(double value) => _configService.SetWindowHeight(value);
    partial void OnIsLogExpandedChanged(bool value) => _configService.SetIsLogExpanded(value);
    partial void OnIsStartWithWindowsEnabledChanged(bool value)
    {
        _startupService.SetAutoStart(value);
        OnPropertyChanged(nameof(StartupDescription));
    }
    partial void OnLogLevelFilterChanged(string value) => NotifyLogs();
    partial void OnLogDateChanged(DateTime value) => NotifyLogs();
    private void LogsChanged(object? sender, NotifyCollectionChangedEventArgs e) => NotifyLogs();
    private void NotifyLogs()
    {
        OnPropertyChanged(nameof(FilteredLogs));
        OnPropertyChanged(nameof(FilteredLogCount));
    }
    [RelayCommand] private void ClearLogs() => BindingOperations.AccessCollection(AllLogs, AllLogs.Clear, true);
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
    [RelayCommand] private void ExportLogs() => SaveJson("XAssistant-events", FilteredLogs.ToArray());

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
    private LogEntry[] SnapshotLogs()
    {
        LogEntry[] result = [];
        BindingOperations.AccessCollection(AllLogs, () => result = AllLogs.ToArray(), false);
        return result;
    }
    public void Dispose()
    {
        AllLogs.CollectionChanged -= LogsChanged;
        Dashboard.Dispose();
    }
}
