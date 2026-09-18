using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services;

namespace XAssistant.ViewModels;

/// <summary>面板里的一行：正在播的、排队中的、等下一次重播的都长成这个样子。</summary>
public sealed class EffectRow
{
    public required int Id { get; init; }
    public required string State { get; init; }
    public required string Channel { get; init; }
    public required string Text { get; init; }
    public required string Detail { get; init; }
    public string? Tag { get; init; }
    /// <summary>紧急档：行首要挂一个三角，别只靠颜色区分。</summary>
    public bool Urgent { get; init; }
}

/// <summary>
/// 工作台底部的消息与播放队列面板。顶部那张悬浮消息栈是可以被关掉、也会被自动淘汰的，
/// 而“哪些告警还在无限重播、谁能把它停掉”这件事必须在主界面留一处能长期盯着的地方——
/// 这里就是那一处：列活动条目（在播 / 排队 / 等重播）并能逐条停、一键全停，下半区是最近结束的历史。
///
/// 数据源是 <see cref="EffectQueue"/> 自己（不是又一份计数）：调度器每 200 ms 心跳一次并报 Changed，
/// 面板就跟着重算这几行，剩余秒数才不会停在数字上不动。
/// </summary>
public sealed partial class MessageQueueViewModel : ViewModelBase, IDisposable
{
    private readonly EffectQueue _queue;

    public ObservableCollection<EffectRow> Active { get; } = new();

    /// <summary>直接共享调度器那份历史：面板不必再抄一遍，插入与裁剪都跟着走。</summary>
    public ObservableCollection<EffectLogEntry> History => _queue.History;

    [ObservableProperty] private string _summary = "屏幕空闲，队列里没有待播的消息";

    /// <summary>一行都没有时整块收起，不留一个空面板在工作台尾巴上占地方。</summary>
    [ObservableProperty] private bool _hasRows;

    public MessageQueueViewModel(EffectQueue queue)
    {
        _queue = queue;
        _queue.Changed += Refresh;
        Refresh();
    }

    public void Dispose() => _queue.Changed -= Refresh;

    [RelayCommand]
    private void Stop(EffectRow? row)
    {
        if (row is not null) _queue.Stop(row.Id);
    }

    [RelayCommand]
    private void StopAll() => _queue.StopAll();

    /// <summary>
    /// 心跳里剩下的秒数一直在变，所以每次 Changed 都整段重算：行数上限是队列上限 + 在播 + 重播表，
    /// 十几行的重建成本可以忽略，换来的是「面板上的数字不会撒谎」。
    /// </summary>
    private void Refresh()
    {
        var rows = _queue.Playing is { } playing
            ? new[] { Row(playing, "正在播", $"剩 {Left(playing)} s · 第 {playing.Plays + 1} 次") }
                    .Concat(_queue.Waiting.Select((job, at) => Row(job, $"排队 {at + 1}", LeftText(job))))
                    .Concat(_queue.Rearming.Select(job => Row(job, "等重播", NextText(job))))
            : _queue.Waiting.Select((job, at) => Row(job, $"排队 {at + 1}", LeftText(job)))
                    .Concat(_queue.Rearming.Select(job => Row(job, "等重播", NextText(job))));
        var list = rows.ToArray();

        Active.Clear();
        foreach (var row in list) Active.Add(row);
        HasRows = list.Length > 0 || _queue.Dropped > 0;
        Summary = list.Length == 0
            ? $"屏幕空闲，无排队；累计被挤掉 {_queue.Dropped} 条（队列上限 {_queue.MaxQueued}）"
            : $"{Head()} · 排队 {Math.Max(0, list.Length - (_queue.Playing is null ? 0 : 1) - _queue.Rearming.Count)} 条"
                + $" · 重播中 {_queue.Rearming.Count} 条 · 被挤掉 {_queue.Dropped} 条";
    }

    private string Head() => _queue.Playing is { } playing
        ? $"正在播「{Clip(playing.Text)}」"
        : "屏幕空闲";

    private static EffectRow Row(EffectJob job, string state, string detail) => new()
    {
        Id = job.Id,
        State = state,
        Channel = job.Channel == EffectChannel.Emergency ? "紧急" : "普通",
        Text = string.IsNullOrWhiteSpace(job.Text) ? "（只有边框，无正文）" : job.Text,
        Detail = detail,
        Tag = job.Tag,
        Urgent = job.Channel == EffectChannel.Emergency,
    };

    private string Left(EffectJob job) =>
        _queue.BusyUntil is { } until ? Math.Max(0, (until - DateTime.Now).TotalSeconds).ToString("F1") : "—";

    private string LeftText(EffectJob job) =>
        $"第 {job.Plays + 1} 次 · 等前一条播完" + (job.Remaining == EffectCommand.InfiniteReplay ? " · 无限重播" : "");

    private string NextText(EffectJob job) =>
        $"还有 {Math.Max(0, (job.Due - DateTime.Now).TotalSeconds).ToString("F1")} s · 间隔 {job.Command.ReplayInterval:0.#} s × 剩 {job.RemainingText}";

    private static string Clip(string text) => text.Length <= 26 ? text : text[..26] + "…";
}
