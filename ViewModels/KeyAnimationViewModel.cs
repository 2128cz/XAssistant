using System;
using System.Collections.Generic;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

/// <summary>
/// 键盘动画视图模型：维护按键热力计数，并把每次按键/鼠标点击/指针位置实时推送给前端动画页面。
/// - 按键计数基础 = 数据库中的今日敲击次数（启动时加载），其后在内存中实时累计
/// - 鼠标点击（含坐标）来自全局鼠标钩子；指针位置由定时器轮询（归一化到 0-1）
/// - 所有事件都在 UI 线程触发，由视图转发给 WebView2
/// </summary>
public partial class KeyAnimationViewModel : ViewModelBase
{
    private readonly IKeyboardHookService _hookService;
    private readonly IMouseClickHookService _mouseHook;
    private readonly IKeyDatabaseService _dbService;
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每次按键触发：参数为（按键名, 累计次数），始终在 UI 线程触发</summary>
    public event Action<string, int>? KeyPressed;

    /// <summary>鼠标点击触发：参数为（按键, 归一化 X, 归一化 Y 0-1），始终在 UI 线程触发</summary>
    public event Action<string, double, double>? MouseClicked;



    public KeyAnimationViewModel(
        IKeyboardHookService hookService,
        IMouseClickHookService mouseHook,
        IKeyDatabaseService dbService
    )
    {
        _hookService = hookService;
        _mouseHook = mouseHook;
        _dbService = dbService;

        // 以今天的敲击次数作为热力图基础数据
        var todayCounts = _dbService.GetKeyCounts(DateTime.Today, DateTime.Today.AddDays(1));
        foreach (var kv in todayCounts)
            _counts[kv.Key] = kv.Value;

        _hookService.KeyPressed += OnKeyPressed;
        _mouseHook.MouseClickedAt += OnMouseClicked;


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

    private void OnMouseClicked(string button, int x, int y)
    {
        // 归一化到虚拟屏幕 0-1，便于前端直接定位
        NormalizePoint(x, y, out double nx, out double ny);

        System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => MouseClicked?.Invoke(button, nx, ny)
        );
    }

    private static void NormalizePoint(int x, int y, out double nx, out double ny)
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        nx = vs.Width > 0 ? (x - vs.Left) / (double)vs.Width : 0;
        ny = vs.Height > 0 ? (y - vs.Top) / (double)vs.Height : 0;
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
    }

    /// <summary>供前端页面加载时获取初始计数快照</summary>
    public Dictionary<string, int> GetInitialCounts() => new(_counts);
}