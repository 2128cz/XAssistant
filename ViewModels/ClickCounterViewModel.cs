using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

public partial class ClickCounterViewModel : ViewModelBase
{
    private readonly IMouseClickHookService _hookService;
    private readonly IClickDatabaseService _dbService;
    private readonly IConfigurationService _configService;

    [ObservableProperty]
    private int _leftClickCount;

    [ObservableProperty]
    private int _middleClickCount;

    [ObservableProperty]
    private int _rightClickCount;

    [ObservableProperty]
    private bool _isRecording;

    // 今天
    [ObservableProperty]
    private int _leftClickToday;

    [ObservableProperty]
    private int _middleClickToday;

    [ObservableProperty]
    private int _rightClickToday;

    // 日历选中日期及对应点击量
    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private int _selectedDateLeftCount;

    [ObservableProperty]
    private int _selectedDateMiddleCount;

    [ObservableProperty]
    private int _selectedDateRightCount;

    // ===== 新增：首页用聚合属性 =====
    public int MouseTodayClicks => LeftClickToday + MiddleClickToday + RightClickToday;
    public int MouseTotalClicks => LeftClickCount + MiddleClickCount + RightClickCount;

    public string MouseRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush MouseRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public ClickCounterViewModel(
        IMouseClickHookService hookService,
        IClickDatabaseService dbService,
        IConfigurationService configService
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;

        // 加载历史总计
        var counts = _dbService.GetClickCounts();
        LeftClickCount = counts["Left"];
        MiddleClickCount = counts["Middle"];
        RightClickCount = counts["Right"];

        RefreshDailyCounts();
        LoadCountsForDate(SelectedDate);

        _hookService.MouseClicked += OnMouseClicked;

        if (_configService.GetRecordingAutoStart())
        {
            StartRecording();
        }
    }

    // 当 IsRecording 变化时，自动通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(MouseRecordingStatus));
        OnPropertyChanged(nameof(MouseRecordingColor));
    }

    // SelectedDate 变更时自动加载对应日期的点击量
    partial void OnSelectedDateChanged(DateTime value)
    {
        LoadCountsForDate(value);
    }

    private void LoadCountsForDate(DateTime date)
    {
        var dayCounts = _dbService.GetClickCountsByDate(date);
        SelectedDateLeftCount = dayCounts["Left"];
        SelectedDateMiddleCount = dayCounts["Middle"];
        SelectedDateRightCount = dayCounts["Right"];
    }

    public void RefreshDailyCounts()
    {
        var today = _dbService.GetClickCountsByDate(DateTime.Today);
        LeftClickToday = today["Left"];
        MiddleClickToday = today["Middle"];
        RightClickToday = today["Right"];
        SyncSelectedTodayCounts();
        OnPropertyChanged(nameof(MouseTodayClicks));
    }

    // The selected-day panel shares today's in-memory values while recording.
    // Older selected dates retain their database snapshot until the date changes.
    private void SyncSelectedTodayCounts()
    {
        if (SelectedDate.Date != DateTime.Today)
            return;

        SelectedDateLeftCount = LeftClickToday;
        SelectedDateMiddleCount = MiddleClickToday;
        SelectedDateRightCount = RightClickToday;
    }

    private DateTime _lastRefreshDate = DateTime.Today;

    private void OnMouseClicked(string button)
    {
        if (!IsRecording)
            return;

        switch (button)
        {
            case "Left":
                LeftClickCount++;
                break;
            case "Middle":
                MiddleClickCount++;
                break;
            case "Right":
                RightClickCount++;
                break;
        }

        _dbService.SaveClick(new MouseClickRecord { Button = button, ClickTime = DateTime.Now });

        if (DateTime.Today != _lastRefreshDate)
        {
            RefreshDailyCounts();
            _lastRefreshDate = DateTime.Today;
        }
        else
        {
            switch (button)
            {
                case "Left":
                    LeftClickToday++;
                    break;
                case "Middle":
                    MiddleClickToday++;
                    break;
                case "Right":
                    RightClickToday++;
                    break;
            }

            SyncSelectedTodayCounts();
            OnPropertyChanged(nameof(MouseTodayClicks));
        }

        // 通知聚合属性更新
        OnPropertyChanged(nameof(MouseTotalClicks));
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        IsRecording = false;
        _configService.SetRecordingAutoStart(false);
    }
}
