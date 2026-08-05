using System;
using System.Windows;
using Microsoft.Extensions.Logging;
using XAssistant.Views;

namespace XAssistant.Services;

// 轻量 toast 服务：在 UI 线程弹出一个短暂浮现的确认提示（不抢占焦点）
public sealed class ToastService
{
    private readonly ILogger<ToastService> _logger;

    public ToastService(ILogger<ToastService> logger) => _logger = logger;

    public void Show(string message)
    {
        try
        {
            // 必须在 UI 线程创建 WPF 窗口；调用方通常在 UI 线程，这里兜底调度
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                var toast = new ToastWindow(message);
                toast.Show();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示 toast 失败");
        }
    }
}
