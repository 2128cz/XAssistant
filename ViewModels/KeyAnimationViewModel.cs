using System;
using System.Collections.Generic;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

/// <summary>
/// 键盘动画视图模型：维护按键热力计数，并把每次按键实时推送给前端动画页面。
/// - 计数基础 = 数据库中的今日敲击次数（启动时加载），其后在内存中实时累计
/// - 每次按键在 UI 线程触发 <see cref="KeyPressed"/>，由视图转发给 WebView2
/// </summary>
public partial class KeyAnimationViewModel : ViewModelBase
{
    private readonly IKeyboardHookService _hookService;
    private readonly IKeyDatabaseService _dbService;
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每次按键触发：参数为（按键名, 累计次数），始终在 UI 线程触发</summary>
    public event Action<string, int>? KeyPressed;

    public KeyAnimationViewModel(
        IKeyboardHookService hookService,
        IKeyDatabaseService dbService
    )
    {
        _hookService = hookService;
        _dbService = dbService;

        // 以今天的敲击次数作为热力图基础数据
        var todayCounts = _dbService.GetKeyCounts(DateTime.Today, DateTime.Today.AddDays(1));
        foreach (var kv in todayCounts)
            _counts[kv.Key] = kv.Value;

        _hookService.KeyPressed += OnKeyPressed;
    }

    private void OnKeyPressed(string key)
    {
        _counts.TryGetValue(key, out var previous);
        int newCount = previous + 1;
        _counts[key] = newCount;

        System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => KeyPressed?.Invoke(key, newCount)
        );
    }

    /// <summary>供前端页面加载时获取初始计数快照</summary>
    public Dictionary<string, int> GetInitialCounts() => new(_counts);
}