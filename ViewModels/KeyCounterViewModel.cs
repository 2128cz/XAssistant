using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
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

    // 用于绑定列表显示（按键名 + 次数）
    public ObservableCollection<KeyCountItem> KeyCounts { get; } = new();

    [ObservableProperty]
    private bool _isRecording;

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

        // 加载历史统计
        LoadCounts();

        if (_configService.GetKeyRecordingAutoStart())
        {
            StartRecording();
        }
    }

    private void OnKeyPressed(string key)
    {
        // WPF 调度到 UI 线程
        WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            var item = KeyCounts.FirstOrDefault(x => x.Key == key);
            if (item != null)
                item.Count++;
            else
                KeyCounts.Add(new KeyCountItem { Key = key, Count = 1 });
        });

        // 持久化（可在任意线程）
        var record = new Models.KeyPressRecord { Key = key, PressTime = DateTime.Now };
        _dbService.SaveKeyPress(record);
    }

    private void LoadCounts()
    {
        var dict = _dbService.GetKeyCounts();
        KeyCounts.Clear();
        foreach (var kv in dict.OrderByDescending(x => x.Value))
        {
            KeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });
        }
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
