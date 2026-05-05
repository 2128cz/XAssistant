using System;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

[SupportedOSPlatform("windows")]
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase? _currentViewModel;
    private readonly IStartupService _startupService;

    [ObservableProperty]
    private bool _isStartWithWindowsEnabled;

    public MainWindowViewModel(IStartupService startupService)
    {
        _startupService = startupService;
        // 初始化时读取当前注册表状态
        IsStartWithWindowsEnabled = _startupService.IsStartWithWindowsEnabled();
    }

    // 属性变化时自动调用 SetAutoStart（通过 CommunityToolkit 的 partial 方法）
    partial void OnIsStartWithWindowsEnabledChanged(bool value)
    {
        _startupService.SetAutoStart(value);
    }

    [RelayCommand]
    private void Navigate(string pageName)
    {
        CurrentViewModel = pageName switch
        {
            "ClickCounter" => App.Services.GetRequiredService<ClickCounterViewModel>(),
            "KeyCounter" => App.Services.GetRequiredService<KeyCounterViewModel>(),
            "Usage" => App.Services.GetRequiredService<UsageViewModel>(),
            _ => CurrentViewModel,
        };
    }
}
