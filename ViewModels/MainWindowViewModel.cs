using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services;
using XAssistant.Models;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;

namespace XAssistant.ViewModels;


[SupportedOSPlatform("windows")]
public partial class MainWindowViewModel : ObservableObject
{
    private readonly MouseClickHookService _hookService;
    private readonly ClickDatabaseService _dbService;
    private readonly ConfigurationService _configService;
    private readonly StartupService _startupService;


    [ObservableProperty]
    private int _leftClickCount;

    [ObservableProperty]
    private int _middleClickCount;

    [ObservableProperty]
    private int _rightClickCount;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _IsStartWithWindowsEnabled;

    partial void OnIsStartWithWindowsEnabledChanged(bool value)
    {
        _startupService.SetAutoStart(value);
        _configService.SetAutoStart(value);
    }

    public MainWindowViewModel()
    {
        _hookService = new MouseClickHookService();
        _dbService = new ClickDatabaseService();
        _configService = new ConfigurationService();

        _startupService = new StartupService("XAssistant");
        // 初始化自启状态
        _IsStartWithWindowsEnabled = _startupService.IsStartWithWindowsEnabled();

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
        // 更新计数
        switch (button)
        {
            case "Left": LeftClickCount++; break;
            case "Middle": MiddleClickCount++; break;
            case "Right": RightClickCount++; break;
        }

        // 持久化到 SQLite
        var record = new MouseClickRecord
        {
            Button = button,
            ClickTime = DateTime.Now
        };
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

    // 注意：钩子事件的回调在非 UI 线程，Count 属性更新时由于使用了 [ObservableProperty]，
    // CommunityToolkit.Mvvm 会自动处理线程调度（通过 PropertyChanged 在 UI 线程），但需要确保钩子事件不是
    // 在 UI 线程，这里没问题。不过最好在 OnMouseClicked 中通过 Avalonia 的 Dispatcher 更新，简单起见这样也可以，
    // 因为 ObservableProperty 的 set 会触发 UI 绑定刷新，Avalonia 会自动调度。
}