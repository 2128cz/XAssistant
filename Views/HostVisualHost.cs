using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace XAssistant.Views;

/// <summary>
/// 把 HostVisual 桥接到一个子 HWND，供 WebView2 Composition 模式挂载。
/// 透明窗口（AllowsTransparency）下 WebView2 只能走 Composition 模式才能正常接收输入。
/// 参考微软官方 WPFWebView2Composition 示例。
/// </summary>
public class HostVisualHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;

    private readonly HostVisual _hostVisual;
    private HwndSource? _hwndSource;

    public HostVisualHost(HostVisual hostVisual)
    {
        _hostVisual = hostVisual;
    }

    /// <summary>子窗口句柄（供 CreateCoreWebView2CompositionControllerAsync 使用）</summary>
    public IntPtr HostHandle => Handle;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var parameters = new HwndSourceParameters("HostVisualHost")
        {
            ParentWindow = hwndParent.Handle,
            WindowStyle = WS_CHILD | WS_VISIBLE,
            Width = 0,
            Height = 0,
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
        // 关键：把 HostVisual 挂到 HwndSource 的 RootVisual，
        // 否则 VisualTarget 渲染到 HostVisual 的内容没有显示目标（窗口空白）
        _hwndSource.RootVisual = _hostVisual;
        return new HandleRef(this, _hwndSource.Handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}