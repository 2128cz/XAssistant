using Serilog.Core;
using Serilog.Events;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class UiLogSink : ILogEventSink
{
    private readonly ILogBufferService _buffer;

    public UiLogSink(ILogBufferService buffer)
    {
        _buffer = buffer;
    }

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry
        {
            Timestamp = logEvent.Timestamp.DateTime,
            Level = logEvent.Level.ToString(),
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString(),
            SourceContext = logEvent.Properties.TryGetValue("SourceContext", out var sc)
                ? sc.ToString().Trim('"')
                : null,
        };

        _buffer.Add(entry);
    }
}
