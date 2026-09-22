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
    /// <summary>本行随组退场的时刻：同组各行各算自己的，整组到点取最迟那一行。</summary>
    public DateTime RowUntil { get; set; }
    /// <summary>消息栈卡片钉过没有：钉过就不再钉，重复喊同一句不该在顶上叠两张一样的卡。</summary>
    public bool Carded { get; set; }

    /// <summary>归组键（<c>-group</c> &gt; <c>-tag</c> &gt; <c>-from</c>）；null = 不成组，按老规矩排队。</summary>
    public string? GroupKey => Command.GroupKey;

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
/// 5. <b>同组合并不排队</b>：屏上正播着某组（<c>-group</c> &gt; <c>-tag</c> &gt; <c>-from</c>，三者都没写就不成组）时，
///    同组的新消息立刻排成它下面的一行（最多 <see cref="MaxRows"/> 行），并把整组的到点时刻从此刻重算；
///    组里最迟那一行走完才一起退场。不同组照旧排队，紧急项照旧抢屏（抢的是整组）。
/// </summary>
public sealed class EffectSchedule
{
    /// <summary>待播队列上限：超了就淘汰，免得一分钟前的告警排着队往屏幕上砸。</summary>
    public const int DefaultMaxQueued = 8;

    /// <summary>一屏最多叠几行同组消息：再多就不是「一叠」而是一堵墙，读不动了。</summary>
    public const int MaxRows = 4;

    private readonly List<EffectJob> _waiting = new();
    private readonly List<EffectJob> _rearming = new();
    private readonly List<EffectJob> _onScreen = new();
    private readonly Func<DateTime> _clock;
    private readonly Action<EffectJob> _start;
    private readonly Action<EffectJob, EffectOutcome>? _report;
    private int _sequence;

    /// <summary>
    /// 当前占着屏幕的那一组里打头的一条；null = 屏幕空闲。
    /// 面板与旧调用读它，类型与语义都不动——多出来的行从 <see cref="OnScreen"/> 看。
    /// </summary>
    public EffectJob? Playing => _onScreen.Count == 0 ? null : _onScreen[0];

    /// <summary>屏上这一组（第 0 条打头，同组后来的按到达顺序往下排）；空 = 屏幕空闲。</summary>
    public IReadOnlyList<EffectJob> OnScreen => _onScreen;

    /// <summary>正在播的这一组什么时候让位（= 组里最迟那一行的退场时刻）。</summary>
    public DateTime? BusyUntil => _onScreen.Count == 0 ? null : _busyUntil;

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
    /// 交一条命令进来。三种去向，按顺序判：
    /// 1. <b>按 tag 归并</b>——同一个告警喊八遍不该在屏幕上排八行；归到屏上那一行就重新计时并让它重扫一遍。
    /// 2. <b>同组入屏</b>——屏上正播着同 <see cref="EffectJob.GroupKey"/> 的一组，这条直接排成它下面的一行，
    ///    不排队等屏幕空（用户要的「同类消息提到前面来，垂直排列」）。
    /// 3. 其余：紧急项挤掉屏上那一组、自己开一组；普通项排队等屏幕空。
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
            if (_onScreen.Contains(same))
            {
                // 屏上那一行又喊了一遍：整组从此刻重新计时，这一行重扫一次（"还在响"要看得见）
                TimeGroup(now);
                _start(same);
            }
            else
            {
                _waiting.Remove(same);
                _rearming.Remove(same);
                InsertByDue(_waiting, same);
            }
            Changed?.Invoke();
            Pump();
            return;
        }

        if (TryJoin(job, now))
        {
            Changed?.Invoke();
            return;
        }

        if (job.Preemptive)
        {
            PreemptGroup();
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
    /// 到点的整组收尾并各自续期 → 到期的重播项按时间戳归位 → 屏幕空了就取下一条。
    /// </summary>
    public void Tick()
    {
        var now = _clock();
        if (_onScreen.Count > 0 && now >= _busyUntil)
        {
            // 组里最迟那一行到点才算整组结束：其余行多留一会儿，正是「以最后一条消失的时间为准」
            var rows = _onScreen.ToArray();
            _onScreen.Clear();
            _busyUntil = default;
            foreach (var row in rows) FinishRound(row, now);
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
    /// 把屏上这一组直接收工（<c>xa off</c>）。该续的下一次照样续：“不想看了”不等于
    /// “这个告警取消了”，取消得用 <c>xa -k</c>。屏幕空了顺带把下一条推上去。
    /// </summary>
    public bool SkipPlaying()
    {
        if (_onScreen.Count == 0) return false;
        var rows = _onScreen.ToArray();
        _onScreen.Clear();
        _busyUntil = default;
        foreach (var row in rows) FinishRound(row, _clock());
        Pump();
        Changed?.Invoke();
        return true;
    }

    // ===== 内部 =====

    private IEnumerable<EffectJob> AllJobs()
    {
        foreach (var job in _onScreen) yield return job;
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
        _onScreen.Add(job);
        TimeGroup(_clock());
        _start(job);
    }

    /// <summary>
    /// 整组重新计时：每行从此刻起再播自己那一段，组的到点时刻取最迟那一行。
    /// 这就是「新的同类消息进来之后，时间从它进来那一刻重算」——老行不会被半路掐掉，
    /// 但也不会停在「再播 0.4 秒就走」这种被新来者挤剩的零头上。
    /// </summary>
    private void TimeGroup(DateTime now)
    {
        foreach (var row in _onScreen) row.RowUntil = now.AddSeconds(Math.Max(0.1, row.ScreenSeconds));
        _busyUntil = _onScreen.Count == 0 ? now : _onScreen.Max(row => row.RowUntil);
    }

    /// <summary>
    /// 同组入屏：屏上正播着同键的一组，这条就排成它下面的一行，不排队等屏幕空。
    /// 没有归组键（裸消息）不成组，照旧排队——「一条播完才播下一条」是拿真事故换来的，不能松。
    /// 同一句话（同键同正文）再进来不算新行，把它顶到最前并重新扫一遍——重复告警排成两行等于没提醒。
    /// 行数到顶时挤掉打头之后最老那一行：宁可让老消息回去排重播，也不把新告警挡在门外。
    /// </summary>
    private bool TryJoin(EffectJob job, DateTime now)
    {
        if (job.GroupKey is not { } key || _onScreen.Count == 0 || _onScreen[0].GroupKey != key) return false;

        var twin = _onScreen.FirstOrDefault(row => row.Text == job.Text);
        if (twin is not null)
        {
            _onScreen.Remove(twin);
            _onScreen.Insert(0, twin);
            twin.Remaining = job.Remaining;
            TimeGroup(now);
            _start(twin);
            return true;
        }

        if (_onScreen.Count >= MaxRows)
        {
            var oldest = _onScreen[1];
            _onScreen.Remove(oldest);
            if (oldest.Remaining == 0)
            {
                Dropped++;
                _report?.Invoke(oldest, EffectOutcome.Dropped);
            }
            else
            {
                oldest.Due = now.AddSeconds(Math.Max(0.1, oldest.Command.ReplayInterval));
                _rearming.Add(oldest);
                _report?.Invoke(oldest, EffectOutcome.Preempted);
            }
        }
        JoinRow(job, now);
        return true;
    }

    /// <summary>把一行并到屏上那一叠里，并按「此刻 + 它自己的时长」重算整组的到点时刻。</summary>
    private void JoinRow(EffectJob job, DateTime now)
    {
        _onScreen.Add(job);
        TimeGroup(now);
        _start(job);
    }

    /// <summary>
    /// 被紧急项挤掉：屏上整组一起下，还有重播配额的各自排下一次，一次性项就此作废（用户选的「让位并丢弃」）。
    /// 紧急项只有首轮能抢屏，所以被抢的这一组之后回来也只走普通队列，不会再反过来抢。
    /// </summary>
    private void PreemptGroup()
    {
        if (_onScreen.Count == 0) return;
        var rows = _onScreen.ToArray();
        _onScreen.Clear();
        _busyUntil = default;
        var now = _clock();
        foreach (var job in rows)
        {
            job.Preemptive = false;
            if (job.Remaining != 0)
            {
                _waiting.Remove(job);
                job.Due = now.AddSeconds(Math.Max(0.1, job.Command.ReplayInterval));
                _rearming.Add(job);
            }
            _report?.Invoke(job, EffectOutcome.Preempted);
        }
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
        if (_onScreen.Remove(job) && _onScreen.Count > 0)
            _busyUntil = _onScreen.Max(row => row.RowUntil);   // 少一行不改别人的到点时刻，只重算最迟那条
        else if (_onScreen.Count == 0) _busyUntil = default;
        _report?.Invoke(job, outcome);
        Pump();
    }

    /// <summary>
    /// 屏幕空了（整组都下去了）才取队首——组内插行由 <see cref="TryJoin"/> 负责，不走这里。
    /// 取走一条之后把同键的排队项一起带上屏：它们本来就是「同一件事的几条」，
    /// 只是来的时候屏幕上还压着别组，不该因此被拆成前后两轮。
    /// </summary>
    private void Pump()
    {
        if (_onScreen.Count > 0 || _waiting.Count == 0) return;
        var next = _waiting[0];
        _waiting.RemoveAt(0);
        StartPlaying(next);
        if (next.GroupKey is not { } key) return;
        foreach (var mate in _waiting.Where(job => job.GroupKey == key).ToArray())
        {
            _waiting.Remove(mate);
            JoinRow(mate, _clock());
        }
    }
}
