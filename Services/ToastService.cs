using System;
using System.Windows;
using Microsoft.Extensions.Logging;
using XAssistant.Views;

namespace XAssistant.Services;

// 轻量 toast 服务：在屏幕顶部浮岛位置弹出一条短暂提示（不抢占焦点）。
// 只保留一个窗口并复用它：每次新建一个的话，连击关键词会在屏幕顶上叠出一摞提示条，
// 后面的还会把前面的挡住——看起来就像"一次弹了五个"。
public sealed class ToastService
{
    private readonly ILogger<ToastService> _logger;
    private ToastWindow? _current;

    public ToastService(ILogger<ToastService> logger) => _logger = logger;

    public void Show(string message)
    {
        try
        {
            // 必须在 UI 线程创建 WPF 窗口；调用方通常在 UI 线程，这里兜底调度
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_current is { } toast)
                {
                    toast.Reset(message);
                    toast.Show();
                    return;
                }
                var created = new ToastWindow(message);
                // 关掉就撒手，下一条重新开一个：复用一个已关闭的窗口会直接抛 InvalidOperationException
                created.Closed += (_, _) => { if (ReferenceEquals(_current, created)) _current = null; };
                _current = created;
                created.Show();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示 toast 失败");
            _current = null;
        }
    }
}
