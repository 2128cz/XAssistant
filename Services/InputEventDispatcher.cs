using System.Collections.Concurrent;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace XAssistant.Services;

/// <summary>One UI event pump shared by native input sources. Native callbacks never execute modules.</summary>
public sealed class InputEventDispatcher(ILogger<InputEventDispatcher> logger) : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentQueue<Action> _pending = new();
    private int _scheduled;
    private bool _disposed;
    public void Publish(Action delivery)
    {
        if (_disposed) return;
        _pending.Enqueue(delivery);
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Drain));
    }
    public void Deliver<T>(Action<T>? handlers, T value)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception e) { logger.LogError(e, "Input module failed: {Module}", handler.Method.DeclaringType); }
    }
    private void Drain()
    {
        for (int i = 0; i < 128 && _pending.TryDequeue(out var delivery); i++)
            try { delivery(); } catch (Exception e) { logger.LogError(e, "Input delivery failed"); }
        Interlocked.Exchange(ref _scheduled, 0);
        if (!_pending.IsEmpty && Interlocked.Exchange(ref _scheduled, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Drain));
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        _disposed = true;
        while (!_pending.IsEmpty) Drain();
    }
}
