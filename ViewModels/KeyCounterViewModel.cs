using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services.Interfaces;
using WpfApplication = System.Windows.Application;

namespace XAssistant.ViewModels;

public partial class KeyCounterViewModel : ViewModelBase
{
    private readonly IKeyboardHookService _hookService;
    private readonly IKeyDatabaseService _dbService;
    private readonly IConfigurationService _configService;
    private DateTime _currentDate = DateTime.Today;

    // ===== 实时按键节奏（速率 / 顺序 / 时间差）=====
    private readonly Queue<DateTime> _recentKeyTimes = new(); // 近 60 秒按键时间滑动窗口
    private DateTime? _lastKeyTime; // 上次按键时间，用于计算时间差
    private System.Windows.Threading.DispatcherTimer? _rateTimer; // 每秒刷新速率窗口（静止时速率回落）

    [ObservableProperty]
    private int _keysPerMinute; // 实时速率：近 60 秒按键数（键/分钟）

    [ObservableProperty]
    private long _lastKeyIntervalMs; // 上次按键与上上次的时间差（毫秒）

    [ObservableProperty]
    private double _averageKeyIntervalMs; // 窗口内平均按键间隔（毫秒）

    [ObservableProperty]
    private double _todayAverageKeysPerMinute; // 今日平均速率（键/分钟）

    /// <summary>最近 30 个按键的实时顺序流（最新在末尾）</summary>
    public ObservableCollection<string> RecentKeySequence { get; } = new();

    // 总计
    public ObservableCollection<KeyCountItem> KeyCounts { get; } = new();

    // 今天
    public ObservableCollection<KeyCountItem> TodayKeyCounts { get; } = new();

    // 昨天
    public ObservableCollection<KeyCountItem> YesterdayKeyCounts { get; } = new();

    // 前天
    public ObservableCollection<KeyCountItem> DayBeforeYesterdayKeyCounts { get; } = new();

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private int _selectedTabIndex;

    // ===== 新增：首页用聚合属性 =====
    public int KeyTodayPresses => TodayKeyCounts.Sum(item => item.Count);
    public int KeyTotalPresses => KeyCounts.Sum(item => item.Count);

    public string KeyRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush KeyRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public KeyCounterViewModel(
        IKeyboardHookService hookService,
        IKeyDatabaseService dbService,
        IConfigurationService configService
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;

        _hookService.KeyPressed += OnKeyPressed;

        LoadAllCounts();

        // 每秒刷新速率窗口：让"实时速率"在停止按键后自然回落
        _rateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _rateTimer.Tick += (_, _) => UpdateRateMetrics();
        _rateTimer.Start();

        if (_configService.GetKeyRecordingAutoStart())
        {
            StartRecording();
        }
    }

    // IsRecording 变化时通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(KeyRecordingStatus));
        OnPropertyChanged(nameof(KeyRecordingColor));
    }

    private void OnKeyPressed(string key)
    {
        var record = new Models.KeyPressRecord { Key = key, PressTime = DateTime.Now };

        // 持久化到数据库
        _dbService.SaveKeyPress(record);

        // 检测是否跨天
        DateTime today = DateTime.Today;
        if (today > _currentDate)
        {
            _currentDate = today;
            // 重新加载所有时间段数据
            LoadAllCounts();
        }
        else
        {
            // 未跨天，增量更新内存集合
            WpfApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                // 更新总计
                UpdateCollection(KeyCounts, key);
                // 更新今天
                UpdateCollection(TodayKeyCounts, key);

                // 集合变化后通知聚合属性
                OnPropertyChanged(nameof(KeyTodayPresses));
                OnPropertyChanged(nameof(KeyTotalPresses));

                // 更新实时节奏（速率 / 顺序 / 时间差）
                UpdateCadence(key);
            });
        }
    }

    /// <summary>
    /// 更新实时按键节奏数据：速率窗口、时间差、按键顺序、今日平均速率。
    /// 必须在 UI 线程调用（Dispatcher）。
    /// </summary>
    private void UpdateCadence(string key)
    {
        var now = DateTime.Now;

        // 时间差：本次按键与上次按键的间隔
        if (_lastKeyTime.HasValue)
            LastKeyIntervalMs = (long)(now - _lastKeyTime.Value).TotalMilliseconds;
        _lastKeyTime = now;

        // 滑动窗口：近 60 秒按键数 → 实时速率
        _recentKeyTimes.Enqueue(now);
        PruneRateWindow(now);
        KeysPerMinute = _recentKeyTimes.Count;

        // 窗口内平均按键间隔
        AverageKeyIntervalMs = ComputeAverageInterval();

        // 最近 30 个按键顺序流
        RecentKeySequence.Add(key);
        if (RecentKeySequence.Count > 30)
            RecentKeySequence.RemoveAt(0);

        UpdateTodayAverage();
    }

    /// <summary>移除滑动窗口中超过 60 秒的过期时间点</summary>
    private void PruneRateWindow(DateTime now)
    {
        while (_recentKeyTimes.Count > 0 && (now - _recentKeyTimes.Peek()).TotalSeconds > 60)
            _recentKeyTimes.Dequeue();
    }

    /// <summary>计算窗口内相邻按键的平均间隔（毫秒）；不足两次按键返回 0</summary>
    private double ComputeAverageInterval()
    {
        if (_recentKeyTimes.Count < 2)
            return 0;

        var times = _recentKeyTimes.ToArray();
        double totalMs = 0;
        for (int i = 1; i < times.Length; i++)
            totalMs += (times[i] - times[i - 1]).TotalMilliseconds;
        return Math.Round(totalMs / (times.Length - 1), 1);
    }

    /// <summary>定时刷新：仅清理过期时间点并重算速率（停止按键后速率会自然回落）</summary>
    private void UpdateRateMetrics()
    {
        PruneRateWindow(DateTime.Now);
        KeysPerMinute = _recentKeyTimes.Count;
        AverageKeyIntervalMs = ComputeAverageInterval();
        UpdateTodayAverage();
    }

    /// <summary>今日平均速率 = 今日按键总数 / 今日已过分钟数</summary>
    private void UpdateTodayAverage()
    {
        double minutes = Math.Max(1, (DateTime.Now - DateTime.Today).TotalMinutes);
        TodayAverageKeysPerMinute = Math.Round(KeyTodayPresses / minutes, 1);
    }

    /// <summary>停止录制时清空节奏数据，避免残留误导</summary>
    private void ResetCadence()
    {
        _recentKeyTimes.Clear();
        _lastKeyTime = null;
        RecentKeySequence.Clear();
        KeysPerMinute = 0;
        LastKeyIntervalMs = 0;
        AverageKeyIntervalMs = 0;
        UpdateTodayAverage();
    }

    private void UpdateCollection(ObservableCollection<KeyCountItem> collection, string key)
    {
        var item = collection.FirstOrDefault(x => x.Key == key);
        if (item != null)
            item.Count++;
        else
            collection.Add(new KeyCountItem { Key = key, Count = 1 });
    }

    private void LoadAllCounts()
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            // 总计
            var totalDict = _dbService.GetKeyCounts();
            KeyCounts.Clear();
            foreach (var kv in totalDict.OrderByDescending(x => x.Value))
                KeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

            // 今天
            var todayDict = _dbService.GetKeyCounts(DateTime.Today, DateTime.Today.AddDays(1));
            TodayKeyCounts.Clear();
            foreach (var kv in todayDict.OrderByDescending(x => x.Value))
                TodayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

            // 昨天
            var yesterdayDict = _dbService.GetKeyCounts(DateTime.Today.AddDays(-1), DateTime.Today);
            YesterdayKeyCounts.Clear();
            foreach (var kv in yesterdayDict.OrderByDescending(x => x.Value))
                YesterdayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

            // 前天
            var dayBeforeDict = _dbService.GetKeyCounts(
                DateTime.Today.AddDays(-2),
                DateTime.Today.AddDays(-1)
            );
            DayBeforeYesterdayKeyCounts.Clear();
            foreach (var kv in dayBeforeDict.OrderByDescending(x => x.Value))
                DayBeforeYesterdayKeyCounts.Add(
                    new KeyCountItem { Key = kv.Key, Count = kv.Value }
                );

            // 通知聚合属性更新
            OnPropertyChanged(nameof(KeyTodayPresses));
            OnPropertyChanged(nameof(KeyTotalPresses));
            UpdateTodayAverage();
        });
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetKeyRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        IsRecording = false;
        _configService.SetKeyRecordingAutoStart(false);
        ResetCadence();
    }

    [RelayCommand]
    private void RefreshData()
    {
        _currentDate = DateTime.Today;
        LoadAllCounts();
    }
}

// 辅助类，用于绑定
public partial class KeyCountItem : ObservableObject
{
    private int _count;
    public string Key { get; set; } = string.Empty;

    public int Length => Key?.Length ?? 0; // 用于排序

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}
