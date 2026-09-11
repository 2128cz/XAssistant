using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Point = System.Windows.Point;
using XAssistant.Models;

namespace XAssistant.ViewModels;

/// <summary>Single-page presentation of the existing recording services and view models.</summary>
public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 曲线重绘节拍。横坐标改为按“距今多少秒”计算后，曲线会随每个节拍连续左移；
    /// 100 ms 一档的位移约 0.9 像素，肉眼已经是平滑滚动，而不是每秒跳一格。
    /// </summary>
    private const int RenderIntervalMs = 100;

    /// <summary>速率采样节拍。速率取自近 1 秒滚动窗口，本身变化很慢，250 ms 足以还原细节。</summary>
    private const int SampleIntervalMs = 250;

    /// <summary>排行榜仍按 1 秒合并刷新，避免把整表排序的成本随重绘节拍放大十倍。</summary>
    private const int RankingIntervalMs = 1000;

    /// <summary>画布尺寸与时间跨度，需与 DashboardView 里 Canvas 的 900×220 及“−60 s”标注一致。</summary>
    private const double ChartWidth = 900;
    private const double ChartHeight = 220;
    private const int WindowSeconds = 60;

    private readonly DispatcherTimer _timer;
    private readonly Queue<DateTime> _recentPressTimes = new();
    private readonly List<RateSample> _rateSamples = new();
    private readonly HashSet<KeyCountItem> _observedKeyItems = new();
    private DateTime _lastSampleAt;
    private DateTime _lastRankingAt;
    private DateTime _lastClockSecond;
    private bool _keyRankingDirty = true;
    private bool _disposed;

    /// <summary>一个速率采样点：值配上落地时刻，横坐标才有连续可导出的时间轴。</summary>
    private readonly record struct RateSample(DateTime At, double Value);

    public ClickCounterViewModel Mouse { get; }
    public KeyCounterViewModel Keyboard { get; }
    public UsageViewModel Usage { get; }
    public AppUsageViewModel Apps { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>
    /// 滚动窗口选项。标签与小时数在同一处定义：“N 小时”的时长不再需要从文案里反向解析，
    /// 改标签或加一档都不会让文案与实现漂移。
    /// 标签不带“最近”前缀是为了塞进右栏时段标签条的窄格，
    /// 完整说法由 <see cref="PeriodRangeHint"/> 直接按小时数给出。
    /// </summary>
    private static readonly (string Label, int Hours)[] RollingWindows =
    {
        ("1 小时", 1),
        ("6 小时", 6),
        ("12 小时", 12),
    };

    /// <summary>按“新→旧”排：今天、滚动窗口、再是整天粒度。</summary>
    public string[] KeyboardPeriodOptions { get; } = new[] { "今天" }
        .Concat(RollingWindows.Select(window => window.Label))
        .Append("总计").Append("昨天").Append("前天").ToArray();

    [ObservableProperty]
    private string _selectedKeyboardPeriod = "今天";

    public ObservableCollection<KeyCountItem> DisplayKeyCounts { get; } = new();
    public ObservableCollection<DashboardKeyRank> TopKeys { get; } = new();
    public ObservableCollection<DashboardRecentKey> RecentKeys { get; } = new();
    public ObservableCollection<string> SessionDateOptions { get; } = new();
    public ObservableCollection<SessionEvent> FilteredSessionEvents { get; } = new();

    [ObservableProperty]
    private string _selectedSessionDate = DateTime.Today.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private DailyUsage? _selectedUsage;

    [ObservableProperty]
    private double _currentCps;

    [ObservableProperty]
    private double _instantCps;

    [ObservableProperty]
    private double _peakCps;

    /// <summary>纵轴上限：由峰值向上取到“好看”的刻度，保证峰值线不会与图表顶边重合。</summary>
    [ObservableProperty]
    private double _axisCps = 1;

    /// <summary>峰值出现的时刻。</summary>
    [ObservableProperty]
    private DateTime _peakCpsAt;

    /// <summary>峰值线在图表内的相对高度（0 = 顶边，1 = 底边），由 View 层按控件实际像素换算。</summary>
    [ObservableProperty]
    private double _peakLineRatio;

    [ObservableProperty]
    private PointCollection _ratePoints = new();

    [ObservableProperty]
    private string _clockText = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    public int TodayKeyCount => Keyboard.TodayKeyCounts.Sum(item => item.Count);
    public int YesterdayKeyCount => Keyboard.YesterdayKeyCounts.Sum(item => item.Count);
    public int DayBeforeYesterdayKeyCount => Keyboard.DayBeforeYesterdayKeyCounts.Sum(item => item.Count);
    public int PeriodTotalCount => GetPeriodKeys().Sum(item => item.Count);

    /// <summary>
    /// 当前时段的锚点。滚动窗口必须说清“从何时起算”：它跟着当前时刻跑，
    /// 同一个选项在不同时刻给出的是完全不同的区间，不标出来就没法解读这些数字。
    /// </summary>
    public string PeriodRangeHint
    {
        get
        {
            var hours = RollingHours;
            if (hours > 0)
                return $"近 {hours} 小时·{DateTime.Now.AddHours(-hours):MM-dd HH:mm} 起，向前滚动";
            return SelectedKeyboardPeriod switch
            {
                "总计" => "全部已记录历史",
                "昨天" => $"{DateTime.Today.AddDays(-1):yyyy-MM-dd} 全天",
                "前天" => $"{DateTime.Today.AddDays(-2):yyyy-MM-dd} 全天",
                _ => $"{DateTime.Today:yyyy-MM-dd} 00:00 起",
            };
        }
    }
    public double AverageCps => Keyboard.TodayAverageKeysPerMinute / 60d;

    /// <summary>最近一次按键间隔的换算副行，与“距上次按下”同一数据、只换单位。</summary>
    public string LastIntervalHint => IntervalHint(Keyboard.LastKeyIntervalMs, "按此间隔", "等待两次按键");

    /// <summary>近 60 秒平均间隔的换算副行。</summary>
    public string AverageIntervalHint => IntervalHint(Keyboard.AverageKeyIntervalMs, "近 60 秒", "窗口不足两次");

    /// <summary>“间隔速率”卡片的主数值：由平均按键间隔倒数推算的每秒按下数。</summary>
    public string IntervalRateText => Keyboard.AverageKeyIntervalMs > 0
        ? CpsOf(Keyboard.AverageKeyIntervalMs)
        : "—";

    /// <summary>间隔为 0 在上游是“样本不足”的哨兵值，不能当成极快连击。</summary>
    private static string IntervalHint(double intervalMs, string scope, string emptyHint) => intervalMs > 0
        ? $"ms · {scope}≈{CpsOf(intervalMs)} 次/s"
        : $"ms · {emptyHint}";

    /// <summary>毫秒间隔换算为次 / s；不足 1 ms 已超过可显示上限，给封顶占位而不是除零。</summary>
    private static string CpsOf(double intervalMs) => intervalMs < 1
        ? "1000+"
        : (1000d / intervalMs).ToString("F1", CultureInfo.CurrentCulture);

    /// <summary>峰值出现时刻的显示文本。</summary>
    public string PeakAtText => PeakCpsAt == default ? "—" : PeakCpsAt.ToString("HH:mm:ss");

    /// <summary>曲线采样与窗口参数，直接从常量生成，避免文案与实现漂移。</summary>
    public string CadenceSummary => $"采样 {SampleIntervalMs} ms · 重绘 {RenderIntervalMs} ms · 窗口 {WindowSeconds} s · 速率=近 1 秒";

    public bool HasPeak => PeakCps > 0;

    partial void OnPeakCpsAtChanged(DateTime value) => OnPropertyChanged(nameof(PeakAtText));

    partial void OnPeakCpsChanged(double value) => OnPropertyChanged(nameof(HasPeak));
    public bool IsRecording => Keyboard.IsRecording || Mouse.IsRecording;
    public string RecordingStatus => Keyboard.IsRecording && Mouse.IsRecording
        ? "记录中" : IsRecording ? "部分记录中" : "已暂停";
    public string LastKeyText => RecentKeys.Count > 0 ? RecentKeys[^1].Key : "等待输入";

    /// <summary>
    /// 「最近输入」条带的倒序镜像。配合条带自身的 RightToLeft 面板，最新按键从右侧起排，
    /// 空间不足时溢出落在左侧并被裁掉，看到的始终是当前按下的键，也不会伸到旁边的卡片上。
    /// 做成独立集合而不是绑 Reverse() 视图，是为了让每次按键只增删一个容器，而不是重建 30 个。
    /// </summary>
    public ObservableCollection<string> RecentKeyChips { get; } = new();
    public string KeyboardChangeText => YesterdayKeyCount == 0
        ? "暂无昨日对比"
        : $"较昨日 {(TodayKeyCount - YesterdayKeyCount) / (double)YesterdayKeyCount:+0.0%;-0.0%;0.0%}";
    public string MouseDailyDetail => $"今日 {Mouse.MouseTodayClicks:N0} · 左 {Mouse.LeftClickToday:N0} / 中 {Mouse.MiddleClickToday:N0} / 右 {Mouse.RightClickToday:N0}";
    public string MouseTotalDetail => $"累计 左 {Mouse.LeftClickCount:N0} / 中 {Mouse.MiddleClickCount:N0} / 右 {Mouse.RightClickCount:N0}";

    // History contains completed sessions only. Replace today's entry with the same
    // current value used by the today card, including its active session exactly once.
    public long TotalUsageSeconds
    {
        get
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var total = Usage.History
                .Where(item => item.Date != today || !Usage.TodayUsageSeconds.HasValue)
                .Sum(item => Math.Max(0, item.CorrectedSeconds ?? item.Seconds));
            return total + Math.Max(0, Usage.TodayUsageSeconds ?? 0);
        }
    }
    public string TotalUsageText => FormatDuration(TotalUsageSeconds);
    public string TotalUsageScopeText
    {
        get
        {
            var dates = Usage.History.Select(item => item.Date).ToHashSet();
            if (Usage.TodayUsageSeconds.HasValue)
                dates.Add(DateTime.Today.ToString("yyyy-MM-dd"));
            var currentSource = Usage.TodayUsageSeconds.HasValue
                ? Usage.IsTodayUsageCorrected ? " · 含今日实时" : " · 今日为数据库值"
                : string.Empty;
            return $"已加载 {dates.Count} 天{currentSource}";
        }
    }
    public string TodayCorrectedUsageText => Usage.TodayUsageText;
    public string YesterdayUsageText => FormatHistoryDay(DateTime.Today.AddDays(-1));
    public string SelectedOriginalUsageText => SelectedUsage?.FormattedTime ?? "—";
    public string SelectedCorrectedUsageText => IsTodaySelected && Usage.TodayUsageSeconds is long todaySeconds
        ? FormatDuration(todaySeconds) + (Usage.IsTodayUsageCorrected ? string.Empty : " (数据库)")
        : SelectedUsage?.FormattedCorrectedTime ?? "—";
    public string SelectedUsageDifferenceText => IsTodaySelected && Usage.TodayUsageSeconds.HasValue
        ? Usage.IsTodayUsageCorrected ? "实时累计中" : "暂无实时校正"
        : SelectedUsage?.CorrectedSeconds is long corrected
            ? FormatSignedDuration(corrected - SelectedUsage.Seconds) : "—";
    public string SelectedCorrectionReason => IsTodaySelected && Usage.TodayUsageSeconds.HasValue
        ? Usage.IsTodayUsageCorrected
            ? "今日有效时长含进行中的会话；原始记录为数据库快照，暂不比较差值。"
            : "今日实时服务不可用，当前采用数据库值，暂不计算实时校正差值。"
        : SelectedUsage?.CorrectionReason ?? "暂无校正说明";
    private bool IsTodaySelected => SelectedSessionDate == DateTime.Today.ToString("yyyy-MM-dd");
    public string WordFrequencyStatus => "暂无词频数据 · 当前仅记录按键名称，不采集输入文本";

    public DashboardViewModel(
        ClickCounterViewModel mouse,
        KeyCounterViewModel keyboard,
        UsageViewModel usage,
        AppUsageViewModel apps,
        SettingsViewModel settings)
    {
        Mouse = mouse;
        Keyboard = keyboard;
        Usage = usage;
        Apps = apps;
        Settings = settings;

        Mouse.PropertyChanged += OnMousePropertyChanged;
        Keyboard.PropertyChanged += OnKeyboardPropertyChanged;
        Usage.PropertyChanged += OnUsagePropertyChanged;
        Keyboard.RecentKeySequence.CollectionChanged += OnRecentKeySequenceChanged;
        foreach (var collection in AllKeyCollections())
            collection.CollectionChanged += OnKeyCollectionChanged;
        ReconcileKeySubscriptions();
        RebuildRecentKeyChips();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RenderIntervalMs) };
        _timer.Tick += OnTimerTick;
        UpdateClock(DateTime.Now);
        RefreshKeyRanking();
        RefreshUsage();
        CurrentCps = Keyboard.KeysPerMinute / 60d;
        _timer.Start();
    }

    partial void OnSelectedKeyboardPeriodChanged(string value)
    {
        _keyRankingDirty = true;
        RefreshKeyRanking();
    }

    partial void OnSelectedSessionDateChanged(string value) => RefreshSelectedUsage();

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
        {
            if (Keyboard.IsRecording)
                Keyboard.StopRecordingCommand.Execute(null);
            if (Mouse.IsRecording)
                Mouse.StopRecordingCommand.Execute(null);
        }
        else
        {
            Keyboard.StartRecordingCommand.Execute(null);
            Mouse.StartRecordingCommand.Execute(null);
        }
    }

    private IEnumerable<ObservableCollection<KeyCountItem>> AllKeyCollections()
    {
        yield return Keyboard.KeyCounts;
        yield return Keyboard.TodayKeyCounts;
        yield return Keyboard.YesterdayKeyCounts;
        yield return Keyboard.DayBeforeYesterdayKeyCounts;
        yield return Keyboard.RollingKeyCounts;
    }

    /// <summary>选中项对应的滚动窗口小时数；不是滚动窗口选项时为 0。</summary>
    private int RollingHours => RollingWindows
        .FirstOrDefault(window => window.Label == SelectedKeyboardPeriod).Hours;

    private ObservableCollection<KeyCountItem> GetPeriodKeys() => RollingHours > 0
        ? Keyboard.RollingKeyCounts
        : SelectedKeyboardPeriod switch
        {
            "总计" => Keyboard.KeyCounts,
            "昨天" => Keyboard.YesterdayKeyCounts,
            "前天" => Keyboard.DayBeforeYesterdayKeyCounts,
            _ => Keyboard.TodayKeyCounts,
        };

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        // 时钟按整秒更新即可，别跟着十倍节拍重复分配字符串
        if (now.Ticks / TimeSpan.TicksPerSecond != _lastClockSecond.Ticks / TimeSpan.TicksPerSecond)
        {
            _lastClockSecond = now;
            UpdateClock(now);
        }
        UpdateCurrentCps(now);
        TrackPeak(InstantCps, now);
        if (_rateSamples.Count == 0 || (now - _lastSampleAt).TotalMilliseconds >= SampleIntervalMs)
        {
            _lastSampleAt = now;
            _rateSamples.Add(new RateSample(now, InstantCps));
        }
        RebuildRatePoints(now);
        // 滚动窗口会自己向前滚：一个按键都没有时，旧按键也在持续掉出边界，
        // 因此不能只靠按键事件把排行榜标脏，选中这类时段时要按固定节拍强制重算
        if (RollingHours > 0 && (now - _lastRankingAt).TotalMilliseconds >= RankingIntervalMs)
            _keyRankingDirty = true;
        if (_keyRankingDirty && (now - _lastRankingAt).TotalMilliseconds >= RankingIntervalMs)
        {
            _lastRankingAt = now;
            RefreshKeyRanking();
        }
    }

    /// <summary>
    /// 用采样时刻而非序号定位横坐标：x = 900 - 距今秒数 × 每秒像素，整条曲线因此连续左移；
    /// 最右端补一个“此刻”的实时点，曲线始终贴着“现在”这条边线。
    /// </summary>
    private void RebuildRatePoints(DateTime now)
    {
        var unitsPerSecond = ChartWidth / WindowSeconds;
        var scale = Math.Max(1d, AxisCps);
        // 额外保留一秒跨度的最旧样本，让左侧线段伸出画布外被裁剪，而不是提前出现断口
        while (_rateSamples.Count > 0 && (now - _rateSamples[0].At).TotalSeconds > WindowSeconds + 1)
            _rateSamples.RemoveAt(0);
        var points = new PointCollection(_rateSamples.Count + 1);
        foreach (var sample in _rateSamples)
        {
            // 系统时钟被回拨时 age 可能为负，钳到 0 以免点跑到“现在”右侧
            var age = Math.Max(0, (now - sample.At).TotalSeconds);
            points.Add(new Point(ChartWidth - age * unitsPerSecond, RateY(sample.Value, scale)));
        }
        points.Add(new Point(ChartWidth, RateY(InstantCps, scale)));
        points.Freeze();
        RatePoints = points;
    }

    private static double RateY(double value, double scale) =>
        ChartHeight - Math.Min(1d, value / scale) * ChartHeight;

    /// <summary>候选纵轴上限，与曲线可能达到的整数速率对齐。</summary>
    private static readonly double[] AxisSteps =
        [1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 120, 150, 200, 300, 500];

    /// <summary>
    /// 记录峰值并刷新示意线。纵轴不直接跟着峰值走，而是取一个带余量的整刻度，
    /// 否则曲线最高点永远触顶、峰值线与图表顶边重合，示意线就没有意义了。
    /// </summary>
    private void TrackPeak(double value, DateTime now)
    {
        if (value <= PeakCps) return;
        PeakCps = value;
        PeakCpsAt = now;
        AxisCps = NiceAxisCeiling(value);
        PeakLineRatio = Math.Clamp(1d - value / AxisCps, 0d, 1d);
    }

    private static double NiceAxisCeiling(double peak)
    {
        var target = Math.Max(1d, peak * 1.1d);
        foreach (var step in AxisSteps)
            if (target <= step) return step;
        return Math.Ceiling(target / 500d) * 500d;
    }

    private void UpdateClock(DateTime now)
    {
        ClockText = now.ToString("HH:mm:ss");
        DateText = now.ToString("yyyy年 M月 d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    }

    private void UpdateCurrentCps(DateTime now)
    {
        var cutoff = now.AddSeconds(-1);
        while (_recentPressTimes.TryPeek(out var timestamp) && timestamp <= cutoff)
            _recentPressTimes.Dequeue();
        InstantCps = _recentPressTimes.Count;
        CurrentCps = Keyboard.KeysPerMinute / 60d;
    }

    private void OnRecentKeySequenceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MirrorRecentKeySequence(e);
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Pausing resets cadence, but the observed event history remains available.
            _recentPressTimes.Clear();
            InstantCps = 0;
        }
        else if (e.NewItems != null)
        {
            foreach (string key in e.NewItems)
            {
                var observedAt = DateTime.Now;
                RecentKeys.Add(new DashboardRecentKey(key, observedAt));
                _recentPressTimes.Enqueue(observedAt);
                while (RecentKeys.Count > 30)
                    RecentKeys.RemoveAt(0);
            }
            var now = DateTime.Now;
            UpdateCurrentCps(now);
            TrackPeak(InstantCps, now);
        }
        OnPropertyChanged(nameof(LastKeyText));
    }

    /// <summary>
    /// 把源的增量变化映射到倒序集合上。源只会在末尾追加、超限时从头裁掉一项，
    /// 因此镜像只需在头部插入、末尾移除；不符合该形状的变化整体重建一次兜底。
    /// </summary>
    private void MirrorRecentKeySequence(NotifyCollectionChangedEventArgs e)
    {
        var source = Keyboard.RecentKeySequence;
        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems?.Count == 1 && e.NewStartingIndex == source.Count - 1)
        {
            RecentKeyChips.Insert(0, (string)e.NewItems[0]!);
            return;
        }
        if (e.Action == NotifyCollectionChangedAction.Remove
            && e.OldItems?.Count == 1 && e.OldStartingIndex == 0 && RecentKeyChips.Count > 0)
        {
            RecentKeyChips.RemoveAt(RecentKeyChips.Count - 1);
            return;
        }
        RebuildRecentKeyChips();
    }

    private void RebuildRecentKeyChips() =>
        ReconcileCollection(RecentKeyChips, Keyboard.RecentKeySequence.Reverse().ToList(), key => key);

    private void OnKeyboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyCounterViewModel.KeyTodayPresses)
            or nameof(KeyCounterViewModel.KeyTotalPresses))
            NotifyKeySummary();
        if (e.PropertyName == nameof(KeyCounterViewModel.TodayAverageKeysPerMinute))
            OnPropertyChanged(nameof(AverageCps));
        if (e.PropertyName == nameof(KeyCounterViewModel.KeysPerMinute))
            CurrentCps = Keyboard.KeysPerMinute / 60d;
        if (e.PropertyName is nameof(KeyCounterViewModel.LastKeyIntervalMs)
            or nameof(KeyCounterViewModel.AverageKeyIntervalMs))
        {
            // 间隔变化即换算变化，三个展示字段一起重报，避开在 XAML 里做数学
            OnPropertyChanged(nameof(LastIntervalHint));
            OnPropertyChanged(nameof(AverageIntervalHint));
            OnPropertyChanged(nameof(IntervalRateText));
        }
        if (e.PropertyName == nameof(KeyCounterViewModel.IsRecording))
            NotifyRecording();
    }

    private void OnMousePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClickCounterViewModel.IsRecording))
            NotifyRecording();
        if (e.PropertyName is nameof(ClickCounterViewModel.LeftClickToday)
            or nameof(ClickCounterViewModel.MiddleClickToday) or nameof(ClickCounterViewModel.RightClickToday))
            OnPropertyChanged(nameof(MouseDailyDetail));
        if (e.PropertyName is nameof(ClickCounterViewModel.LeftClickCount)
            or nameof(ClickCounterViewModel.MiddleClickCount) or nameof(ClickCounterViewModel.RightClickCount))
            OnPropertyChanged(nameof(MouseTotalDetail));
    }

    private void NotifyRecording()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingStatus));
    }

    private void OnKeyCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReconcileKeySubscriptions();
        NotifyKeySummary();
    }

    private void ReconcileKeySubscriptions()
    {
        var current = AllKeyCollections().SelectMany(collection => collection).ToHashSet();
        foreach (var item in _observedKeyItems.Except(current).ToArray())
        {
            item.PropertyChanged -= OnKeyItemPropertyChanged;
            _observedKeyItems.Remove(item);
        }
        foreach (var item in current.Except(_observedKeyItems))
        {
            item.PropertyChanged += OnKeyItemPropertyChanged;
            _observedKeyItems.Add(item);
        }
    }

    private void OnKeyItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyCountItem.Count))
            NotifyKeySummary();
    }

    private void NotifyKeySummary()
    {
        _keyRankingDirty = true;
        OnPropertyChanged(nameof(TodayKeyCount));
        OnPropertyChanged(nameof(YesterdayKeyCount));
        OnPropertyChanged(nameof(DayBeforeYesterdayKeyCount));
        OnPropertyChanged(nameof(PeriodTotalCount));
        OnPropertyChanged(nameof(KeyboardChangeText));
    }

    private void RefreshKeyRanking()
    {
        // 先重算窗口再排序：否则排行、热力图与“当前时段累计”会各自读到上一个窗口的尾巴
        var hours = RollingHours;
        if (hours > 0)
            Keyboard.RefreshRollingWindow(hours);
        var sorted = GetPeriodKeys().OrderByDescending(item => item.Count)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToList();
        ReconcileCollection(DisplayKeyCounts, sorted, item => item.Key);
        var total = sorted.Sum(item => item.Count);
        var maximum = sorted.FirstOrDefault()?.Count ?? 0;
        var ranks = sorted.Take(5).Select((item, index) => new DashboardKeyRank
        {
            Key = item.Key,
            Rank = index + 1,
            Count = item.Count,
            Percentage = total > 0 ? item.Count * 100d / total : 0,
            BarWidth = maximum > 0 ? item.Count * 100d / maximum : 0,
        }).ToList();
        // Retain existing row instances so count changes do not restart all row visuals.
        foreach (var rank in ranks)
        {
            var existing = TopKeys.FirstOrDefault(item => item.Key == rank.Key);
            if (existing == null)
                continue;
            existing.Rank = rank.Rank;
            existing.Count = rank.Count;
            existing.Percentage = rank.Percentage;
            existing.BarWidth = rank.BarWidth;
        }
        var stableRanks = ranks.Select(rank => TopKeys.FirstOrDefault(item => item.Key == rank.Key) ?? rank).ToList();
        ReconcileCollection(TopKeys, stableRanks, item => item.Key);
        _keyRankingDirty = false;
        OnPropertyChanged(nameof(PeriodTotalCount));
        OnPropertyChanged(nameof(PeriodRangeHint));
    }

    private void OnUsagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageViewModel.History) or nameof(UsageViewModel.SessionEvents))
            RefreshUsage();
        if (e.PropertyName is nameof(UsageViewModel.TodayUsageSeconds) or nameof(UsageViewModel.IsTodayUsageCorrected))
        {
            NotifyUsageTotals();
            NotifySelectedUsage();
        }
        if (e.PropertyName == nameof(UsageViewModel.TodayUsageText))
            OnPropertyChanged(nameof(TodayCorrectedUsageText));
    }

    private void RefreshUsage()
    {
        var dates = Usage.History.Select(item => item.Date)
            .Concat(Usage.SessionEvents.Select(item => item.Date))
            .Append(DateTime.Today.ToString("yyyy-MM-dd"))
            .Where(date => !string.IsNullOrWhiteSpace(date))
            .Distinct().OrderByDescending(date => date, StringComparer.Ordinal).ToList();
        ReconcileCollection(SessionDateOptions, dates, date => date);
        if (!dates.Contains(SelectedSessionDate))
            SelectedSessionDate = dates[0];
        RefreshSelectedUsage();
        NotifyUsageTotals();
    }

    private void NotifyUsageTotals()
    {
        OnPropertyChanged(nameof(TotalUsageSeconds));
        OnPropertyChanged(nameof(TotalUsageText));
        OnPropertyChanged(nameof(TotalUsageScopeText));
        OnPropertyChanged(nameof(TodayCorrectedUsageText));
        OnPropertyChanged(nameof(YesterdayUsageText));
    }

    private void RefreshSelectedUsage()
    {
        SelectedUsage = Usage.History.FirstOrDefault(item => item.Date == SelectedSessionDate);
        var events = Usage.SessionEvents.Where(item => item.Date == SelectedSessionDate).ToList();
        // Replace changed source event objects to retain their live cumulative-duration notifications.
        ReconcileCollection(FilteredSessionEvents, events, item => item.Id);
        NotifySelectedUsage();
    }

    private void NotifySelectedUsage()
    {
        OnPropertyChanged(nameof(SelectedOriginalUsageText));
        OnPropertyChanged(nameof(SelectedCorrectedUsageText));
        OnPropertyChanged(nameof(SelectedUsageDifferenceText));
        OnPropertyChanged(nameof(SelectedCorrectionReason));
    }

    private string FormatHistoryDay(DateTime day)
    {
        var record = Usage.History.FirstOrDefault(item => item.Date == day.ToString("yyyy-MM-dd"));
        return record == null ? "暂无记录" : FormatDuration(record.CorrectedSeconds ?? record.Seconds);
    }

    private static void ReconcileCollection<T, TKey>(ObservableCollection<T> target,
        IReadOnlyList<T> desired, Func<T, TKey> key)
    {
        var comparer = EqualityComparer<TKey>.Default;
        for (var index = 0; index < desired.Count; index++)
        {
            var currentIndex = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (comparer.Equals(key(target[candidate]), key(desired[index])))
                {
                    currentIndex = candidate;
                    break;
                }
            }
            if (currentIndex < 0)
                target.Insert(index, desired[index]);
            else
            {
                if (currentIndex != index)
                    target.Move(currentIndex, index);
                if (!EqualityComparer<T>.Default.Equals(target[index], desired[index]))
                    target[index] = desired[index];
            }
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static string FormatDuration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(long)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    private static string FormatSignedDuration(long seconds) => seconds == 0
        ? "无差异" : $"{(seconds > 0 ? "+" : "−")}{FormatDuration(Math.Abs(seconds))}";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        // 鼠标里程靠定时落库，退出前让它的最后一次冲刷把不足 1 秒的尾数写进去
        Mouse.Dispose();
        Mouse.PropertyChanged -= OnMousePropertyChanged;
        Keyboard.PropertyChanged -= OnKeyboardPropertyChanged;
        Usage.PropertyChanged -= OnUsagePropertyChanged;
        Keyboard.RecentKeySequence.CollectionChanged -= OnRecentKeySequenceChanged;
        foreach (var collection in AllKeyCollections())
            collection.CollectionChanged -= OnKeyCollectionChanged;
        foreach (var item in _observedKeyItems)
            item.PropertyChanged -= OnKeyItemPropertyChanged;
        _observedKeyItems.Clear();
        GC.SuppressFinalize(this);
    }
}

public partial class DashboardKeyRank : ObservableObject
{
    public string Key { get; set; } = string.Empty;
    [ObservableProperty] private int _rank;
    [ObservableProperty] private int _count;
    [ObservableProperty] private double _percentage;
    /// <summary>Bar width normalized to 100 display units.</summary>
    [ObservableProperty] private double _barWidth;
}

public sealed record DashboardRecentKey(string Key, DateTime Timestamp)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
}
