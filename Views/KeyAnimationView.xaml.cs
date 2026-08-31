using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using XAssistant.ViewModels;

namespace XAssistant.Views;

/// <summary>
/// 键盘动画视图：嵌入 WebView2 渲染虚拟键盘热力图，
/// 把 KeyAnimationViewModel 的实时按键事件通过 PostWebMessageAsJson 推送给页面。
/// </summary>
public partial class KeyAnimationView
{
    private KeyAnimationViewModel? _vm;
    private bool _subscribed;
    private bool _webReady;

    public KeyAnimationView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _vm = DataContext as KeyAnimationViewModel;
            if (_vm != null)
            {
                _vm.KeyPressed += OnVmKeyPressed;
                _vm.MouseClicked += OnVmMouseClicked;
                _vm.MouseMoved += OnVmMouseMoved;
                _subscribed = true;
            }
        }

        // WebView2 默认背景透明：透明开关由页面内按钮控制（透视模式）
        KeyboardWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        // 首次加载时导航到本地动画页
        if (KeyboardWebView.CoreWebView2 == null && KeyboardWebView.Source == null)
        {
            KeyboardWebView.NavigationCompleted += OnNavigationCompleted;
            string htmlPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "KeyboardAnimation",
                "index.html"
            );
            if (File.Exists(htmlPath))
                KeyboardWebView.Source = new Uri(htmlPath);
        }
        else if (_webReady)
        {
            // 页面切换回来：重新推送一次初始计数，保证热力图与当前计数同步
            PushInitialCounts();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _webReady = e.IsSuccess;
        if (_webReady)
            PushInitialCounts();
    }

    private void OnVmKeyPressed(string key, int count)
    {
        if (!_webReady || KeyboardWebView.CoreWebView2 == null)
            return;

        var message = JsonSerializer.Serialize(new { type = "press", key, count });
        KeyboardWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private void OnVmMouseClicked(string button, double x, double y)
    {
        if (!_webReady || KeyboardWebView.CoreWebView2 == null)
            return;

        var message = JsonSerializer.Serialize(new { type = "mouseClick", button, x, y });
        KeyboardWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private void OnVmMouseMoved(double nx, double ny)
    {
        if (!_webReady || KeyboardWebView.CoreWebView2 == null)
            return;

        var message = JsonSerializer.Serialize(new { type = "mouseMove", x = nx, y = ny });
        KeyboardWebView.CoreWebView2.PostWebMessageAsJson(message);
    }
    private void PushInitialCounts()
    {
        if (_vm == null || KeyboardWebView.CoreWebView2 == null)
            return;

        var message = JsonSerializer.Serialize(
            new { type = "init", counts = _vm.GetInitialCounts() }
        );
        KeyboardWebView.CoreWebView2.PostWebMessageAsJson(message);
    }
}