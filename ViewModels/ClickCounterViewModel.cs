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

        _hookService.MouseClicked += OnMouseClicked;

        // 根据配置自动开始录制
        if (_configService.GetRecordingAutoStart())
        {
            StartRecording(); // 这会设置 IsRecording = true 并启动钩子
        }
    }

    private void OnMouseClicked(string button)
    {
        if (!IsRecording) // 不录制时直接忽略
            return;

        // 更新计数
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

        // 持久化到 SQLite
        var record = new MouseClickRecord { Button = button, ClickTime = DateTime.Now };
        _dbService.SaveClick(record);
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
