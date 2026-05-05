using System;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services;
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

    // 昨天
    [ObservableProperty]
    private int _leftClickYesterday;

    [ObservableProperty]
    private int _middleClickYesterday;

    [ObservableProperty]
    private int _rightClickYesterday;

    // 前天
    [ObservableProperty]
    private int _leftClickDayBeforeYesterday;

    [ObservableProperty]
    private int _middleClickDayBeforeYesterday;

    [ObservableProperty]
    private int _rightClickDayBeforeYesterday;

    public ClickCounterViewModel(
        IMouseClickHookService hookService,
        IClickDatabaseService dbService,
        IConfigurationService configService
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;

        // 加载历史计数
        var counts = _dbService.GetClickCounts();
        LeftClickCount = counts["Left"];
        MiddleClickCount = counts["Middle"];
        RightClickCount = counts["Right"];

        RefreshDailyCounts();

        _hookService.MouseClicked += OnMouseClicked;

        // 根据配置自动开始录制
        if (_configService.GetRecordingAutoStart())
        {
            StartRecording(); // 这会设置 IsRecording = true 并启动钩子
        }
    }

    public void RefreshDailyCounts()
    {
        var today = _dbService.GetClickCountsByDate(DateTime.Today);
        LeftClickToday = today["Left"];
        MiddleClickToday = today["Middle"];
        RightClickToday = today["Right"];

        var yesterday = _dbService.GetClickCountsByDate(DateTime.Today.AddDays(-1));
        LeftClickYesterday = yesterday["Left"];
        MiddleClickYesterday = yesterday["Middle"];
        RightClickYesterday = yesterday["Right"];

        var dayBefore = _dbService.GetClickCountsByDate(DateTime.Today.AddDays(-2));
        LeftClickDayBeforeYesterday = dayBefore["Left"];
        MiddleClickDayBeforeYesterday = dayBefore["Middle"];
        RightClickDayBeforeYesterday = dayBefore["Right"];
    }

    private DateTime _lastRefreshDate = DateTime.Today;

    private void OnMouseClicked(string button)
    {
        if (!IsRecording)
            return;

        // 总量始终实时递增
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

        // 检查是否跨天，如跨天则刷新所有每日计数，然后归零今天
        if (DateTime.Today != _lastRefreshDate)
        {
            RefreshDailyCounts(); // 此时获取到的已经是新一天的数据，今天自动为0
            _lastRefreshDate = DateTime.Today;
        }
        else
        {
            // 同一天内，仅内存递增今天计数（不再查库）
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
        }
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
