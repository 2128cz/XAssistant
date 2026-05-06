using System.Collections.ObjectModel;
using System.Windows.Data;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class LogBufferService : ILogBufferService
{
    private readonly object _lock = new();

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public LogBufferService()
    {
        // 允许跨线程操作 ObservableCollection
        BindingOperations.EnableCollectionSynchronization(LogEntries, _lock);
    }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            LogEntries.Add(entry);
        }
    }
}
