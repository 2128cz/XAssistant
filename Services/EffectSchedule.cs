using System;
using System.Collections.Generic;
using System.Linq;

namespace XAssistant.Services;

/// <summary>播放通道。紧急通道首轮能抢屏，普通通道只能排队。</summary>
public enum EffectChannel { Normal, Emergency }

/// <summary>一条效果在队列里的生命周期。</summary>
public enum EffectOutcome { Played, Repeating, Dropped, Preempted, Stopped }

/// <summary>
/// 一条待播 / 在播 / 待续期的效果。<b>可变</b>：重播次数与到期时间都在播完之后就地更新，
/// 同一条告警在屏幕上始终是同一个对象——底部面板才不会出现“同一句话排了八行”。
/// </summary>
public sealed class EffectJob
{
    public required int Id { get; init; }
    public required EffectCommand Command { get; init; }
    public EffectChannel Channel { get; set; }
    /// <summary>提交时刻。同 <see cref="Due"/> 时按它排先后，也是“丢最旧”的依据。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>什么时候允许播。普通项提交即到期；重播项 = 上一轮播完 + 间隔。</summary>
    public DateTime Due { get; set; }
    /// <summary>还差几轮重播。<see cref="EffectCommand.InfiniteReplay"/> = 无限，0 = 不重播。</summary>
    public int Remaining { get; set; }
    /// <summary>本轮允不允许抢占屏幕。只有紧急项的**首轮**为真。</summary>
    public bool Preemptive { get; set; }
    /// <summary>已经播过几轮（面板与历史拿它显示「第 3/∞ 次」）。</summary>
    public int Plays { get; set; }

    public string? Tag => Command.Tag;
    public string Text => Command.Text ?? "";
    public double ScreenSeconds => Command.ScreenSeconds;
    /// <summary>剩余次数文案：无限就写 ∞。</summary>
    public string RemainingText => Remaining == EffectCommand.InfiniteReplay ? "∞" : Remaining.ToString();
}

/// <summary>
/// 播放调度：单轨屏幕 + 两个通道 + 按时间戳归位的重播队列。<b>纯逻辑</b>——时钟与「真的画到屏上」
/// 都由宿主注入（<see cref="EffectQueue"/> 在 WPF 侧接线，夹具塞一个假时钟就能整段推演），
/// 所以这里一行都不碰窗口。
///
/// 四条规矩，对应四条真实事故：
/// 1. <b>一条播完才播下一条</b>：效果窗原本是「新的来了旧的立刻停下」，告警一密集就互相挤掉、
///    等于没提醒。队列化之后普通项按 Due 排队，谁也不会被谁咬掉半句。
/// 2. <b>紧急项首轮插播，被它挤掉的那条直接作废</b>（不补播）：供电中断这种话晚一秒到屏上都不行，
///    而一条被抢的普通动画丢了也就丢了。
/// 3. <b>紧急项只有首轮能抢屏</b>——重播期到之后降级进普通队列，按时间戳归位。这就是要的那点
///    「类 linux 调度」：紧急的 nice 值只在它第一次出现时生效，之后不再长期挤占普通动画。
/// 4. <b>重播不占队列名额</b>：下一次是等这一轮播完才生成的，还在间隔里时它待在 <see cref="Rearming"/>；
///    队列溢出淘汰时先丢最旧的一次性项，一条都不剩才动重播项里到期最远的那条——
///    否则「丢旧的」会连着把重播的到期节奏一起丢掉。
/// </summary>
public sealed class EffectSchedule
{
    /// <summary>待播队列上限：超了就淘汰，免得一分钟前的告警排着队往屏幕上砸。</summary>
    public const int DefaultMaxQueued = 8;

    private readonly List<EffectJob> _waiting = new();
    private readonly List<EffectJob> _rearming = new();
    private readonly Func<DateTime> _clock;
    private readonly Action<EffectJob> _start;
    private readonly Action<EffectJob, EffectOutcome>? _report;
    private int _sequence;

    /// <summary>当前正占着屏幕的那条；null = 屏幕空闲。</summary>
    public EffectJob? Playing { get; private set; }

    /// <summary>正在播的这条什么时候让位。</summary>
    public DateTime? BusyUntil => Playing is null ? null : _busyUntil;

    /// <summary>被溢出淘汰掉的条数（面板上「已丢弃 N 条」读它）。</summary>
    public int Dropped { get; private set; }

    public IReadOnlyList<EffectJob> Waiting => _waiting;
    public IReadOnlyList<EffectJob> Rearming => _rearming;

    /// <summary>正在播的这条什么时候让位（只在有得播时有值）。</summary>
    private DateTime _busyUntil;

    /// <summary>状态有变动（入队、开播、播完、淘汰、停止）。宿主用它刷新面板。</summary>
    public event Action? Changed;

    public int MaxQueued { get; }

    public EffectSchedule(Func<DateTime> clock, Action<EffectJob> start, Action<EffectJob, EffectOutcome>? report = null,
        int maxQueued = DefaultMaxQueued)
    {
        _clock = clock;
        _start = start;
        _report = report;
        MaxQueued = Math.Max(1, maxQueued);
    }

    /// <summary>
    /// 交一条命令进来。紧急项立刻插播（挤掉正在播的那条），其余排队等屏幕空。
    /// 带 <c>-tag</c> 的命令重复提交按 tag <b>归并</b>：同一个告警喊八遍不该在屏幕上排八行。
    /// </summary>
    public void Submit(EffectCommand command)
    {
        var now = _clock();
        var job = new EffectJob
        {
            Id = ++_sequence,
            Command = command,
            Channel = command.Urgent ? EffectChannel.Emergency : EffectChannel.Normal,
            CreatedAt = now,
            Due = now,
            Remaining = command.ReplayTimes,
            // 只有紧急档能抢屏，而且只有第一次：播完之后 Preemptive 一律降回 false
            Preemptive = command.Urgent,
        };

        var same = FindByTag(job.Tag);
        if (same is not null)
        {
            // 归并：把到期时间与剩余次数按这一次的说法刷新；正在播的那条不中断，它自己会续下一次
            same.Due = now;
            same.Remaining = job.Remaining;
            if (!ReferenceEquals(Playing, same))
            {
                _waiting.Remove(same);
                _rearming.Remove(same);
                InsertByDue(_waiting, same);
            }
            Changed?.Invoke();
            Pump();
            return;
        }

        if (job.Preemptive)
        {
            if (Playing is { } current) Preempt(current);
            StartPlaying(job);
            Changed?.Invoke();
            return;
        }

        InsertByDue(_waiting, job);
        EvictOverflow();
        Changed?.Invoke();
        Pump();
    }

    /// <summary>
    /// 宿主的心跳（常驻侧 100 ms 一次；夹具里手动推时钟）。三件事按顺序做：
    /// 播完的收尾并续期 → 到期的重播项按时间戳归位 → 屏幕空了就取下一条。
    /// </summary>
    public void Tick()
    {
        var now = _clock();
        if (Playing is { } playing && now >= _busyUntil)
        {
            Playing = null;
            FinishRound(playing, now);
        }
        foreach (var due in _rearming.Where(job => job.Due <= now).OrderBy(job => job.Due).ToArray())
        {
            _rearming.Remove(due);
            InsertByDue(_waiting, due);
        }
        EvictOverflow();
        Pump();
        // 心跳里“剩余倒计时”也在走，面板得跟着刷，不管队列有没有变动
        Changed?.Invoke();
    }

    /// <summary>
    /// 按 tag / 通道 / 正文子串杀除，返回命中条数。三个条件都为空就是「全停」。
    /// 在播的那条一并收起——不然杀完还剩半句挂在屏上。
    /// </summary>
    public int Kill(EffectChannel? channel, string? tag, string? contains)
    {
        bool Loose(EffectJob job) =>
            (channel is null || job.Channel == channel)
            && (tag is null || string.Equals(job.Tag, tag, StringComparison.OrdinalIgnoreCase))
            && (contains is null || job.Text.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var hits = AllJobs().Where(Loose).ToArray();
        foreach (var job in hits) Remove(job, EffectOutcome.Stopped);
        if (hits.Length > 0) Changed?.Invoke();
        return hits.Length;
    }

    /// <summary>停某一条（底部面板每行的 ✕）。</summary>
    public bool Stop(int id)
    {
        var job = AllJobs().FirstOrDefault(x => x.Id == id);
        if (job is null) return false;
        Remove(job, EffectOutcome.Stopped);
        Changed?.Invoke();
        return true;
    }

    /// <summary>全部停掉。</summary>
    public int StopAll()
    {
        var all = AllJobs().ToArray();
        foreach (var job in all) Remove(job, EffectOutcome.Stopped);
        Changed?.Invoke();
        return all.Length;
    }

    /// <summary>
    /// 把正在播的这条直接收工（<c>xa off</c>）。该续的下一次照样续：“不想看了”不等于
    /// “这个告警取消了”，取消得用 <c>xa -k</c>。屏幕空了顺带把下一条推上去。
    /// </summary>
    public bool SkipPlaying()
    {
        if (Playing is not { } job) return false;
        Playing = null;
        _busyUntil = default;
        FinishRound(job, _clock());
        Pump();
        Changed?.Invoke();
        return true;
    }

    // ===== 内部 =====

    private IEnumerable<EffectJob> AllJobs()
    {
        if (Playing is { } playing) yield return playing;
        foreach (var job in _waiting) yield return job;
        foreach (var job in _rearming) yield return job;
    }

    private EffectJob? FindByTag(string? tag) => tag is null
        ? null
        : AllJobs().FirstOrDefault(job => string.Equals(job.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>按 Due 升序、同 Due 按提交序号升序插到正确位置——「按时间戳重新插入」就是这一步。</summary>
    private static void InsertByDue(List<EffectJob> list, EffectJob job)
    {
        int at = 0;
        while (at < list.Count && (list[at].Due < job.Due || (list[at].Due == job.Due && list[at].Id < job.Id))) at++;
        list.Insert(at, job);
    }

    private void StartPlaying(EffectJob job)
    {
        Playing = job;
        _busyUntil = _clock().AddSeconds(job.ScreenSeconds);
        _start(job);
    }

    /// <summary>被紧急项挤掉：还有重播配额的继续排下一次，一次性项就此作废（用户选的「让位并丢弃」）。</summary>
    private void Preempt(EffectJob job)
    {
        job.Preemptive = false;
        if (job.Remaining != 0)
        {
            _waiting.Remove(job);
            job.Due = _clock().AddSeconds(Math.Max(0.1, job.Command.ReplayInterval));
            _rearming.Add(job);
            _report?.Invoke(job, EffectOutcome.Preempted);
        }
        else
        {
            _report?.Invoke(job, EffectOutcome.Preempted);
        }
        Playing = null;
    }

    /// <summary>
    /// 一轮播完：还有配额就把下一次挂到 <see cref="_rearming"/>（降级为普通优先级），没有就收工。
    /// 间隔从「播完」起算而不是从「上一轮的槽位」起算（不是 cron式）：一条 1 秒一次、本身就要闪 6 秒的
    /// 无限告警，按 cron 能铺满屏幕把别人饿死；错开在播完之后，它每一轮之间必定给别的动画留出一段空档。
    /// </summary>
    private void FinishRound(EffectJob job, DateTime now)
    {
        job.Plays++;
        job.Preemptive = false;   // 抢屏只此一次，之后按点排队
        if (job.Remaining == 0)
        {
            _report?.Invoke(job, EffectOutcome.Played);
            return;
        }
        if (job.Remaining != EffectCommand.InfiniteReplay) job.Remaining--;
        job.Due = now.AddSeconds(Math.Max(0.1, job.Command.ReplayInterval));
        _rearming.Add(job);
        _report?.Invoke(job, EffectOutcome.Repeating);
    }

    /// <summary>溢出淘汰：先丢最旧的一次性项；全是重播项时丢到期最远的那条。</summary>
    private void EvictOverflow()
    {
        while (_waiting.Count > MaxQueued)
        {
            var victim = _waiting.Where(job => job.Remaining == 0).OrderBy(job => job.CreatedAt).FirstOrDefault()
                ?? _waiting.OrderByDescending(job => job.Due).First();
            _waiting.Remove(victim);
            Dropped++;
            _report?.Invoke(victim, EffectOutcome.Dropped);
        }
    }

    /// <summary>从队列与续期表里摘掉一条；正在播的那条顺带让宿主收起屏幕上的字。</summary>
    private void Remove(EffectJob job, EffectOutcome outcome)
    {
        _waiting.Remove(job);
        _rearming.Remove(job);
        if (ReferenceEquals(Playing, job))
        {
            Playing = null;
            _busyUntil = default;
        }
        _report?.Invoke(job, outcome);
        Pump();
    }

    /// <summary>屏幕空了就取队首。</summary>
    private void Pump()
    {
        if (Playing is not null || _waiting.Count == 0) return;
        var next = _waiting[0];
        _waiting.RemoveAt(0);
        StartPlaying(next);
    }
}
