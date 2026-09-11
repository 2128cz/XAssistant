using CommunityToolkit.Mvvm.ComponentModel;

namespace XAssistant.Models;

/// <summary>
/// 某个应用在某天的合并会话行。行实例跨刷新复用、只就地改值（时长每两秒动一次），
/// 因此所有会变的量都是可通知属性、格式化串靠 NotifyPropertyChangedFor 连带通知：
/// 整表重建会让会话表每两秒掉一次滚动位置与行内视觉状态。
/// </summary>
public partial class AppUsageItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _processName = string.Empty;

    /// <summary>数据库里已经落盘的累计秒数。追踪器每 5 秒写一次，进行中的行最长会落后真实值一个写入节拍。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedRawUsage))]
    [NotifyPropertyChangedFor(nameof(CorrectionSeconds))]
    [NotifyPropertyChangedFor(nameof(FormattedCorrection))]
    private long _rawSeconds;

    /// <summary>校正后秒数：已退出的应用等于落盘值，进行中的应用补上「最后一次落盘之后又跑了多久」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedUsage))]
    [NotifyPropertyChangedFor(nameof(CorrectionSeconds))]
    [NotifyPropertyChangedFor(nameof(FormattedCorrection))]
    private long _totalSeconds;

    /// <summary>是否还有未关闭、且仍在按时累计的会话段。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isRunning;

    /// <summary>当天该应用被记录的会话段数，重启一次多一段。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _sessionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedStartTime))]
    private DateTime? _startTime;

    /// <summary>最后一次确认活动的时间：已退出的是结束时刻，进行中的是最后落盘时刻。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedEndTime))]
    private DateTime? _endTime;

    /// <summary>占当天全部已记录时长的百分比，由所属集合统一回填，供占比列直接格式化。</summary>
    [ObservableProperty]
    private double _share;

    public string DisplayName => ProcessName.Length == 0
        ? "—"
        : char.ToUpper(ProcessName[0]) + ProcessName[1..];

    /// <summary>多段时一并给出段数：一个应用当天重启过几次，直接影响「最近活动」这一列该怎么读。</summary>
    public string StatusText => IsRunning
        ? SessionCount > 1 ? $"进行中 · {SessionCount} 段" : "进行中"
        : SessionCount > 1 ? $"已结束 · {SessionCount} 段" : "已结束";

    /// <summary>校正量，即落盘快照之外补上的秒数。</summary>
    public long CorrectionSeconds => TotalSeconds - RawSeconds;

    public string FormattedUsage => FormatSeconds(TotalSeconds);

    public string FormattedRawUsage => FormatSeconds(RawSeconds);

    /// <summary>补算差值为零说明这一行已经落定，不是「还没算」，所以给短横而不是 00:00:00。</summary>
    public string FormattedCorrection => CorrectionSeconds == 0 ? "—" : $"+{FormatSeconds(CorrectionSeconds)}";

    public string FormattedStartTime => StartTime?.ToString("HH:mm:ss") ?? "--";

    public string FormattedEndTime => EndTime?.ToString("HH:mm:ss") ?? "--";

    private static string FormatSeconds(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
