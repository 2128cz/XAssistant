using System.Collections.ObjectModel;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

public interface ILogBufferService
{
    ObservableCollection<LogEntry> LogEntries { get; }
    void Add(LogEntry entry);
}
