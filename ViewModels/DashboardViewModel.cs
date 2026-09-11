using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;

namespace XAssistant.ViewModels;

/// <summary>Single-page presentation of the existing recording services and view models.</summary>
public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Queue<DateTime> _recentPressTimes = new();
    private readonly List<double> _rateSamples = new();
    private readonly HashSet<KeyCountItem> _observedKeyItems = new();
    private bool _keyRankingDirty = true;
    private bool _disposed;

    public ClickCounterViewModel Mouse { get; }
    public KeyCounterViewModel Keyboard { get; }
    public UsageViewModel Usage { get; }
    public AppUsageViewModel Apps { get; }
    public SettingsViewModel Settings { get; }

    public string[] KeyboardPeriodOptions { get; } = { "今天", "总计", "昨天", "前天" };

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
    public double AverageCps => Keyboard.TodayAverageKeysPerMinute / 60d;
    public bool IsRecording => Keyboard.IsRecording || Mouse.IsRecording;
    public string RecordingStatus => Keyboard.IsRecording && Mouse.IsRecording
        ? "记录中" : IsRecording ? "部分记录中" : "已暂停";
    public string LastKeyText => RecentKeys.Count > 0 ? RecentKeys[^1].Key : "等待输入";
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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        UpdateClock();
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
    }

    private ObservableCollection<KeyCountItem> GetPeriodKeys() => SelectedKeyboardPeriod switch
    {
        "总计" => Keyboard.KeyCounts,
        "昨天" => Keyboard.YesterdayKeyCounts,
        "前天" => Keyboard.DayBeforeYesterdayKeyCounts,
        _ => Keyboard.TodayKeyCounts,
    };

    private void OnTimerTick(object? sender, EventArgs e)
    {
        UpdateClock();
        UpdateCurrentCps();
        _rateSamples.Add(InstantCps);
        if (_rateSamples.Count > 60)
            _rateSamples.RemoveAt(0);
        PeakCps = Math.Max(PeakCps, InstantCps);
        var scale = Math.Max(1d, PeakCps);
        var points = new PointCollection(_rateSamples.Count);
        for (var index = 0; index < _rateSamples.Count; index++)
            points.Add(new System.Windows.Point((60 - _rateSamples.Count + index) * 900d / 59d,
                220d - Math.Min(1d, _rateSamples[index] / scale) * 220d));
        points.Freeze();
        RatePoints = points;
        if (_keyRankingDirty)
            RefreshKeyRanking();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm:ss");
        DateText = now.ToString("yyyy年 M月 d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    }

    private void UpdateCurrentCps()
    {
        var cutoff = DateTime.Now.AddSeconds(-1);
        while (_recentPressTimes.TryPeek(out var timestamp) && timestamp <= cutoff)
            _recentPressTimes.Dequeue();
        InstantCps = _recentPressTimes.Count;
        CurrentCps = Keyboard.KeysPerMinute / 60d;
    }

    private void OnRecentKeySequenceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
            UpdateCurrentCps();
            PeakCps = Math.Max(PeakCps, InstantCps);
        }
        OnPropertyChanged(nameof(LastKeyText));
    }

    private void OnKeyboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyCounterViewModel.KeyTodayPresses)
            or nameof(KeyCounterViewModel.KeyTotalPresses))
            NotifyKeySummary();
        if (e.PropertyName == nameof(KeyCounterViewModel.TodayAverageKeysPerMinute))
            OnPropertyChanged(nameof(AverageCps));
        if (e.PropertyName == nameof(KeyCounterViewModel.KeysPerMinute))
            CurrentCps = Keyboard.KeysPerMinute / 60d;
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
