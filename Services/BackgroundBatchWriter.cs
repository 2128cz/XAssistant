using System.Threading.Channels;

namespace XAssistant.Services;

/// <summary>Ordered background persistence; Dispose joins the final batch without relying on the UI dispatcher.</summary>
public sealed class BackgroundBatchWriter<T> : IDisposable
{
    private readonly Channel<T> _queue = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private bool _disposed;
    public BackgroundBatchWriter(Action<IReadOnlyList<T>> persist, Action<Exception>? onError = null)
    {
        _worker = Task.Run(async () =>
        {
            while (await _queue.Reader.WaitToReadAsync())
            {
                await Task.Delay(25);
                var batch = new List<T>(256);
                while (batch.Count < 256 && _queue.Reader.TryRead(out var item)) batch.Add(item);
                for (int attempt = 0; ; attempt++)
                {
                    try { persist(batch); break; }
                    catch (Exception e) when (attempt < 2) { onError?.Invoke(e); await Task.Delay(100 * (attempt + 1)); }
                }
            }
        });
    }
    public void Enqueue(T item)
    {
        if (_worker.IsFaulted) _worker.GetAwaiter().GetResult();
        if (_disposed || !_queue.Writer.TryWrite(item)) throw new ObjectDisposedException(nameof(BackgroundBatchWriter<T>));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _worker.GetAwaiter().GetResult();
    }
}
