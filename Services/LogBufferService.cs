using System.Collections.ObjectModel;
using System.Windows.Data;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class LogBufferService : ILogBufferService
{
    /// <summary>内存中保留的日志条数上限：常驻运行不再无界增长</summary>
    public const int Capacity = 2000;

    /// <summary>越界时一次性裁剪到的条数，避免每条日志都触发一轮集合变更通知</summary>
    private const int TrimTo = Capacity * 3 / 4;

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

            // 环形裁剪：只在越界时批量丢弃最旧的 1/4，把通知次数摊薄到最低
            if (LogEntries.Count > Capacity)
            {
                int removeCount = LogEntries.Count - TrimTo;
                for (int i = 0; i < removeCount; i++)
                    LogEntries.RemoveAt(0);
            }
        }
    }
}
