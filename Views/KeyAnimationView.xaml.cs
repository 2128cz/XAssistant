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
        KeyboardWebView.CoreWebView2InitializationCompleted += OnCoreWebView2Initialized;
    }

    private void OnCoreWebView2Initialized(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e
    )
    {
        if (e.IsSuccess)
            KeyboardWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
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

            // 宿主是分离窗口时，通知页面隐藏「分离窗口」按钮，避免套娃
            if (System.Windows.Window.GetWindow(this) is KeyAnimationWindow)
            {
                var env = System.Text.Json.JsonSerializer.Serialize(
                    new { type = "setEnv", overlay = true }
                );
                if (KeyboardWebView.CoreWebView2 != null)
                    KeyboardWebView.CoreWebView2.PostWebMessageAsJson(env);
            }
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        bool ok = e.IsSuccess;
        Dispatcher.Invoke(() =>
        {
            _webReady = ok;
            if (ok)
            {
                PushInitialCounts();

                // 宿主是分离窗口时，通知页面隐藏「分离窗口」按钮，避免套娃
                if (System.Windows.Window.GetWindow(this) is KeyAnimationWindow)
                {
                    var env = System.Text.Json.JsonSerializer.Serialize(
                        new { type = "setEnv", overlay = true }
                    );
                    if (KeyboardWebView.CoreWebView2 != null)
                        KeyboardWebView.CoreWebView2.PostWebMessageAsJson(env);
                }
            }
        });
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

    private void OnWebMessageReceived(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e
    )
    {
        // WebView2 回调可能在非 UI 线程，统一切回 UI 线程处理
        Dispatcher.Invoke(() =>
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                var root = json.RootElement;
                string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "detach")
                {
                    // 分离窗口内忽略，避免套娃
                    if (System.Windows.Window.GetWindow(this) is KeyAnimationWindow)
                        return;
                    if (_vm != null)
                        KeyAnimationWindow.EnsureOpen(_vm);
                }
                else if (type == "dragDelta")
                {
                    if (System.Windows.Window.GetWindow(this) is KeyAnimationWindow w)
                    {
                        double dx = root.TryGetProperty("dx", out var px) ? px.GetDouble() : 0;
                        double dy = root.TryGetProperty("dy", out var py) ? py.GetDouble() : 0;
                        w.MoveBy(dx, dy);
                    }
                }
            }
            catch
            {
                // 忽略无法解析的消息
            }
        });
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