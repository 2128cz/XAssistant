using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using XAssistant.Models;

namespace XAssistant.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly ClickCounterViewModel _clickVM;
    private readonly KeyCounterViewModel _keyVM;
    private readonly UsageViewModel _usageVM;
    private readonly AppUsageViewModel _appUsageVM;

    // 鼠标摘要
    [ObservableProperty]
    private int _mouseTotalClicks;

    [ObservableProperty]
    private int _mouseTodayClicks;

    [ObservableProperty]
    private bool _isMouseRecording;

    // 键盘摘要
    [ObservableProperty]
    private int _keyTotalPresses;

    [ObservableProperty]
    private int _keyTodayPresses;

    [ObservableProperty]
    private bool _isKeyRecording;

    // 电脑使用今日时长
    [ObservableProperty]
    private string _todayUsageText = "00:00:00";

    // 软件使用 Top 列表（直接引用 AppUsageViewModel 的列表）
    public ObservableCollection<AppUsageItem> TopApps => _appUsageVM.AppUsageList;

    public HomeViewModel(
        ClickCounterViewModel clickVM,
        KeyCounterViewModel keyVM,
        UsageViewModel usageVM,
        AppUsageViewModel appUsageVM
    )
    {
        _clickVM = clickVM;
        _keyVM = keyVM;
        _usageVM = usageVM;
        _appUsageVM = appUsageVM;

        // 初始化数据
        RefreshMouseSummary();
        RefreshKeySummary();
        TodayUsageText = _usageVM.TodayUsageText;
        _appUsageVM.SelectedDate = DateTime.Today; // 确保软件使用显示今日

        // 订阅源数据变化，实时更新摘要
        _clickVM.PropertyChanged += (_, _) => RefreshMouseSummary();
        _keyVM.PropertyChanged += (_, _) => RefreshKeySummary();
        _keyVM.KeyCounts.CollectionChanged += (_, _) => RefreshKeyTotal();
        _keyVM.TodayKeyCounts.CollectionChanged += (_, _) => RefreshKeyToday();
        _usageVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UsageViewModel.TodayUsageText))
                TodayUsageText = _usageVM.TodayUsageText;
        };
        _appUsageVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppUsageViewModel.AppUsageList))
                OnPropertyChanged(nameof(TopApps)); // 集合对象被替换时通知绑定
        };
    }

    private void RefreshMouseSummary()
    {
        MouseTotalClicks =
            _clickVM.LeftClickCount + _clickVM.MiddleClickCount + _clickVM.RightClickCount;
        MouseTodayClicks =
            _clickVM.LeftClickToday + _clickVM.MiddleClickToday + _clickVM.RightClickToday;
        IsMouseRecording = _clickVM.IsRecording;
    }

    private void RefreshKeySummary()
    {
        RefreshKeyTotal();
        RefreshKeyToday();
        IsKeyRecording = _keyVM.IsRecording;
    }

    private void RefreshKeyTotal() => KeyTotalPresses = _keyVM.KeyCounts.Sum(x => x.Count);

    private void RefreshKeyToday() => KeyTodayPresses = _keyVM.TodayKeyCounts.Sum(x => x.Count);
}
