using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;

namespace XAssistant.Services;

/// <summary>
/// 后台攒批落库队列：保存入口只入队就返回，开连接与写事务都在这个后台线程上做。
///
/// 为什么必须有它：低级钩子（WH_KEYBOARD_LL / WH_MOUSE_LL）是串在系统输入链路上的同步钩子，
/// 回调超过 LowLevelHooksTimeout（默认约 1 秒）就会被系统直接跳过——不报错、不重投、也不通知应用，
/// 表现就是界面仍显示「正在记录」而实际什么都收不到。逐条「开连接 + INSERT + 提交」在真库上实测
/// 约 2.5 ms/条，磁盘慢或抢锁时就是压垮钩子的那一段；纯入队约 1 µs/条。
///
/// 两条底线：队列有界（宁可丢记录也不能无界涨内存），以及任何失败都不能把队列停掉
/// （一批写坏丢一批，下一批照写；否则一次故障之后所有统计永久不再落库）。
/// </summary>
public sealed class BackgroundBatchWriter<T> : IDisposable
{
    /// <summary>落库失败的重试次数，重试以完整事务为单位（一批要么全写要么整体重来）</summary>
    private const int MaxAttempts = 3;

    private readonly Channel<T> _queue;
    private readonly Action<IReadOnlyList<T>> _persist;
    private readonly Action<Exception>? _onError;
    private readonly string _name;
    private readonly int _maxBatch;
    private readonly int _settleMs;
    private readonly object _gate = new();
    private readonly Task _worker;
    private long _dropped;

    // 已从队列取出、但还没写完也没丢掉的一批。光看队列长度是不够的：
    // 一批在退避重试时队列是空的、也没持着 _gate，那时 Flush 会错报“排空完成”
    private int _inFlight;
    private bool _disposed;

    /// <param name="persist">批量写入委托，只会在后台线程被反复调用</param>
    /// <param name="onError">每次尝试失败时的回调，用于把故障上报给调用方</param>
    /// <param name="name">队列名，只用于日志</param>
    /// <param name="capacity">队列容量；满了丢新条目，绝不阻塞生产者</param>
    /// <param name="maxBatch">单次事务最多带走多少条</param>
    /// <param name="settleMs">被唤醒后的攒批等待，让突发输入合并成一次事务</param>
    public BackgroundBatchWriter(
        Action<IReadOnlyList<T>> persist,
        Action<Exception>? onError = null,
        string? name = null,
        int capacity = 8192,
        int maxBatch = 256,
        int settleMs = 25
    )
    {
        _persist = persist;
        _onError = onError;
        _name = string.IsNullOrEmpty(name) ? typeof(T).Name : name;
        _maxBatch = maxBatch;
        _settleMs = settleMs;
        _queue = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                // 用 Wait 而不是 DropWrite：DropWrite 在队满时 TryWrite 仍返回 true（新条目被静默丢弃），
                // 拿满的条目会直接掉进下面自己计数；生产者始终不会被阻塞
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        _worker = Task.Run(RunAsync);
    }

    /// <summary>因队列溢出或已停止接收而丢弃的条数，正常情况下恒为 0</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>入队并立即返回。这条路径在钩子回调与 UI 线程上被调用，任何情况都不抛、不阻塞。</summary>
    public void Enqueue(T item)
    {
        if (_queue.Writer.TryWrite(item))
            return;

        var dropped = Interlocked.Increment(ref _dropped);
        if (dropped == 1 || dropped % 1000 == 0)
            Log.Warning("{Queue} 落库队列已满或已停止接收，累计丢弃 {Dropped} 条记录", _name, dropped);
    }

    private async Task RunAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<T>(_maxBatch);

        // 队列里还有剩余时 WaitToReadAsync 立刻返回 true，所以积压能连续排空；
        // 攒批只发生在「等来一条新记录」之后，最后一条不会一直躺在队列里等下一条来叫醒
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (_settleMs > 0)
                await Task.Delay(_settleMs).ConfigureAwait(false);

            batch.Clear();
            while (batch.Count < _maxBatch && reader.TryRead(out var item))
                batch.Add(item);
            if (batch.Count == 0)
                continue;

            Interlocked.Add(ref _inFlight, batch.Count);
            for (var attempt = 0; ; attempt++)
            {
                var lastAttempt = attempt == MaxAttempts - 1;
                try
                {
                    lock (_gate)
                        _persist(batch);
                    break;
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                    if (lastAttempt)
                    {
                        // 一批写坏了就丢掉这一批，但绝不能把整个队列停掉
                        Log.Error(ex, "{Queue} 批量落库失败，重试 {Attempts} 次后丢弃本批 {Count} 条记录", _name, MaxAttempts, batch.Count);
                        break;
                    }
                    await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
                }
            }
            Interlocked.Add(ref _inFlight, -batch.Count);
        }
    }

    /// <summary>
    /// 等队列排空、并且正在写的那一批提交完。重新做全量统计前与程序退出前调用。
    /// 后台线程被卡住时不能把调用方一起挂死，因此全程带超时。
    /// </summary>
    public void Flush(int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (
            (Volatile.Read(ref _inFlight) > 0)
            || (_queue.Reader.CanCount && _queue.Reader.Count > 0)
        )
        {
            if (Environment.TickCount64 >= deadline)
            {
                Log.Warning(
                    "{Queue} 落库队列排空超时，仍有 {Count} 条未写入",
                    _name,
                    _queue.Reader.CanCount ? _queue.Reader.Count : -1
                );
                return;
            }
            Thread.Sleep(10);
        }

        // 拿到锁说明没有在途批次；此刻起的新入队属于调用方之后的事
        if (!Monitor.TryEnter(_gate, Math.Max(0, (int)(deadline - Environment.TickCount64))))
            Log.Warning("{Queue} 等待在途落库超时", _name);
        else
            Monitor.Exit(_gate);
    }

    /// <summary>停止接收，把已入队的记录写完再收线程。不依赖 UI 消息泵，可在退出路径上直接调。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            if (!_worker.Wait(TimeSpan.FromSeconds(5)))
                Log.Warning("{Queue} 落库线程未在 5 秒内收尾，队列里可能还有 {Count} 条没写完", _name, _queue.Reader.CanCount ? _queue.Reader.Count : -1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Queue} 落库线程收尾失败", _name);
        }
    }
}
