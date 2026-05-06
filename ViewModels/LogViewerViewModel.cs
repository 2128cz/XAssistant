using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

public partial class LogViewerViewModel : ViewModelBase
{
    private readonly ILogBufferService _logBuffer;
    private readonly object _lock = new();

    [ObservableProperty]
    private ObservableCollection<LogEntry> _filteredLogs = new();

    [ObservableProperty]
    private string _selectedLevel = "All";

    public string[] AvailableLevels { get; } =
        { "All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    public LogViewerViewModel(ILogBufferService logBuffer)
    {
        _logBuffer = logBuffer;

        // 源集合与显示集合同步（线程安全已由 LogBufferService 保证）
        BindingOperations.EnableCollectionSynchronization(FilteredLogs, _lock);

        // 当基础集合变化时刷新过滤
        _logBuffer.LogEntries.CollectionChanged += (_, _) => ApplyFilter();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        lock (_lock)
        {
            var filtered = string.Equals(
                SelectedLevel,
                "All",
                System.StringComparison.OrdinalIgnoreCase
            )
                ? _logBuffer.LogEntries
                : _logBuffer.LogEntries.Where(e =>
                    string.Equals(e.Level, SelectedLevel, System.StringComparison.OrdinalIgnoreCase)
                );

            FilteredLogs.Clear();
            foreach (var entry in filtered)
                FilteredLogs.Add(entry);
        }
    }

    partial void OnSelectedLevelChanged(string value)
    {
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logBuffer.LogEntries.Clear();
        ApplyFilter();
    }
}
