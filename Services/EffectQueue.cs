using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using XAssistant.Views;

// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Brush 会跟 WPF 的撞名
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace XAssistant.Services;

/// <summary>一条效果播完或被停掉后留下的一行记录（底部面板的「最近历史」）。</summary>
public sealed record EffectLogEntry(
    int Id, DateTime At, EffectChannel Channel, string? Tag, string Text, EffectOutcome Outcome, int Plays)
{
    public string TimeText => At.ToString("HH:mm:ss");
    public string ChannelText => Channel == EffectChannel.Emergency ? "紧急" : "普通";
    /// <summary>
    /// 面板那一行正文：通道 · tag · 文本。空段连同它前面的分隔符一起省——
    /// 没写 tag 的命令很多，别让历史里排出一串孤立的 “·”。
    /// </summary>
    public string LineText => ChannelText
        + (string.IsNullOrWhiteSpace(Tag) ? "" : " · " + Tag)
        + (string.IsNullOrWhiteSpace(Text) ? "" : " · " + Text);
    public string OutcomeText => Outcome switch
    {
        EffectOutcome.Played => "已播完",
        EffectOutcome.Repeating => "待续期",
        EffectOutcome.Dropped => "被挤掉",
        EffectOutcome.Preempted => "让位",
        _ => "已停止",
    };
}

/// <summary>
/// 调度器的常驻宿主：一条 <see cref="DispatcherTimer"/> 同时管「这一轮播完了没」与「下一次重播到点没」，
/// 真的上屏交给 <see cref="EffectsWindow"/>，留档交给 <see cref="MessageStackWindow"/>。
///
/// 屏幕与队列都只该有一份，所以它是静态 <see cref="Shared"/>：常驻主程序与无头 xa 实例走同一个入口，
/// 两条路径行为不许分叉。<see cref="EffectSchedule"/> 本身不碰 WPF，这里才碰。
/// </summary>
public sealed class EffectQueue
{
    /// <summary>心跳间隔。重播间隔最短 0.5 s，200 ms 足够看清节奏又不至于把 UI 线程点满。</summary>
    private const int TickMilliseconds = 200;

    /// <summary>历史保留条数：面板下半区滚得过来，又能回看一阵子。</summary>
    public const int MaxHistory = 60;

    public static EffectQueue Shared { get; } = new();

    /// <summary>
    /// 这一份是不是常驻主程序里的。无头实例放完就退，重播定时器跟着进程一起没——
    /// 所以它挂上重播时要留一句话告诉用户「得把主程序跑起来」。
    /// </summary>
    public static bool Hosted { get; set; }

    private readonly EffectSchedule _schedule;
    private readonly DispatcherTimer _ticker;

    private EffectQueue()
    {
        // 「一屏摆得下几行」只有窗口量得出来（可用高度 ÷ 实测行高），调度器只管这个数——
        // 所以是**函数**：窗口高度、当前字号、边框带宽都会变，容量得每次现问。
        _schedule = new EffectSchedule(
            () => DateTime.Now,
            Play,
            Report,
            rowCapacity: EffectsWindow.RowCapacityHint);
        _schedule.Changed += () => Changed?.Invoke();
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMilliseconds) };
        _ticker.Tick += (_, _) => _schedule.Tick();
        _ticker.Start();
    }

    /// <summary>面板订阅：入队、开播、播完、淘汰、停止都会来一下。</summary>
    public event Action? Changed;

    public EffectJob? Playing => _schedule.Playing;
    public DateTime? BusyUntil => _schedule.BusyUntil;
    public ObservableCollection<EffectLogEntry> History { get; } = new();

    /// <summary>快照式的排队视图（面板不必自己处理集合移动）。</summary>
    public System.Collections.Generic.IReadOnlyList<EffectJob> Waiting => _schedule.Waiting;

    public System.Collections.Generic.IReadOnlyList<EffectJob> Rearming => _schedule.Rearming;

    public int Dropped => _schedule.Dropped;
    public int MaxQueued => _schedule.MaxQueued;

    /// <summary>交一条命令进队列（常驻与无头两条路都走这里）。</summary>
    public void Submit(EffectCommand command) => _schedule.Submit(command);

    /// <summary>按 tag / 通道 / 正文子串杀除。杀完屏幕上没东西了就顺手收起。</summary>
    public int Kill(EffectChannel? channel, string? tag, string? contains)
    {
        int hits = _schedule.Kill(channel, tag, contains);
        if (hits > 0 && _schedule.Playing is null) EffectsWindow.HideBanner();
        return hits;
    }

    /// <summary>按一条解析好的 -k 命令杀除：没给任何选择条件就是全停。</summary>
    public int Kill(EffectCommand selector) =>
        Kill(selector.Urgent ? EffectChannel.Emergency : null, selector.Id ?? selector.Tag, selector.MatchAny);

    /// <summary>收起当前这一条（<c>xa off</c>）：不算取消告警，带重播的下一轮照旧。</summary>
    public void SkipCurrent()
    {
        _schedule.SkipPlaying();
        if (_schedule.Playing is null) EffectsWindow.HideBanner();
    }

    public bool Stop(int id)
    {
        bool stopped = _schedule.Stop(id);
        if (stopped && _schedule.Playing is null) EffectsWindow.HideBanner();
        return stopped;
    }

    public int StopAll()
    {
        int hits = _schedule.StopAll();
        if (hits > 0) EffectsWindow.HideBanner();
        return hits;
    }

    /// <summary>真的把这一条画到屏上：全屏大字 + 四边带（紧急档两侧再加三角感叹号），首轮带正文的再钉进顶部栈。</summary>
    private static void Play(EffectJob job)
    {
        EffectCommand command = job.Command;
        EffectsWindow.ShowCommand(command);
        // 重播与「同组又喊一遍」都不重复钉卡：一条无限重播每轮都往顶上叠一张一模一样的话，十张上限转眼就被它刷满，
        // 反而把别的提醒挤下去。第一张卡已经代表“这件事需要人来看”。
        // -stack off 是逐条开关：这条只当场看一眼、不留卡（关键词彩蛋走的就是它）。
        if (job.Carded || !command.Stack || command.Text is not { Length: > 0 } text) return;
        job.Carded = true;
        // 紧急档不往文案里贴 ⚠ 字形：卡片自己画一颗警告三角（urgent 参数），尺寸与颜色才能控制
        MessageStackWindow.Push(text, EffectCommand.BrushOf(command.Color) ?? Accent(), command.Source, command.Urgent);
    }

    /// <summary>调度器报上来的结局：进历史，顺带处理「重播没人接管」这种必须说一声的情况。</summary>
    private void Report(EffectJob job, EffectOutcome outcome)
    {
        if (outcome == EffectOutcome.Repeating && !Hosted)
        {
            Append(job, EffectOutcome.Dropped);   // 重播就此断在这里，记成被挤掉而不是假装还在排
            return;
        }
        // 让位与续期不算“结束”，别在历史里刷成一排噪声；其余都留一行
        if (outcome == EffectOutcome.Preempted && job.Remaining != 0) return;
        Append(job, outcome);
    }

    private void Append(EffectJob job, EffectOutcome outcome)
    {
        History.Insert(0, new EffectLogEntry(job.Id, DateTime.Now, job.Channel, job.TagKey, job.Text, outcome, job.Plays));
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>类型词 info 跟着主题强调色走，取不到资源时接中性灰兜底（与 <see cref="EffectDispatch"/> 同一口径）。</summary>
    private static Brush Accent() =>
        System.Windows.Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.Gainsboro;
}
