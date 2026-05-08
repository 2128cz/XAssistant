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
            });
        }
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
