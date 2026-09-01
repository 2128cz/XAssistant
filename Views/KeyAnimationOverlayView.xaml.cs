using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using XAssistant.ViewModels;

namespace XAssistant.Views;

/// <summary>
/// 键盘动画 Composition 视图：供透明窗口使用。
/// WebView2 走 Composition 模式（CoreWebView2CompositionController + HostVisual），
/// 是 AllowsTransparency 窗口下唯一能正常接收输入的方案。
/// 注意：WebView2 事件回调（NavigationCompleted / WebMessageReceived）可能在非 UI 线程触发，
/// 所有 controller / Window 访问一律通过 RunOnUi 切回 UI 线程。
/// </summary>
public partial class KeyAnimationOverlayView
{
    private readonly HostVisual _hostVisual = new();
    private HostVisualHost? _hostVisualHost;
    private CoreWebView2CompositionController? _controller;
    private VisualTarget? _visualTarget;
    private ContainerVisual? _rootVisual;
    private KeyAnimationViewModel? _vm;
    private bool _subscribed;
    private bool _webReady;

    public KeyAnimationOverlayView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>确保在 UI 线程执行；非 UI 线程时同步切回（WebView2 Composition 成员只能在 UI 线程访问）</summary>
    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.Invoke(action);
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

        if (_hostVisualHost == null)
        {
            _hostVisualHost = new HostVisualHost(_hostVisual);
            RootGrid.Children.Add(_hostVisualHost);

            // 强制布局：HwndHost 的 BuildWindowCore 在布局时才执行，Handle 才能用
            _hostVisualHost.UpdateLayout();

            try
            {
                InitializeComposition();
            }
            catch (Exception ex)
            {
                ShowError("WebView2 初始化失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 同步初始化 Composition WebView2。整体在 UI 线程执行（Dispatcher.Invoke），
    /// 异步 API 用 GetAwaiter().GetResult() 同步等待，彻底避免
    /// await 延续线程不确定导致的 "Members can only be accessed from the ui thread"。
    /// </summary>
    private void InitializeComposition()
    {
        if (_hostVisualHost == null)
            return;

        Dispatcher.Invoke(() =>
        {
            try
            {
                var environment = CoreWebView2Environment
                    .CreateAsync()
                    .GetAwaiter()
                    .GetResult();
                _controller = environment
                    .CreateCoreWebView2CompositionControllerAsync(_hostVisualHost.HostHandle)
                    .GetAwaiter()
                    .GetResult();
                _controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;

                // 把 CompositionController 的 RootVisual 挂到 WPF VisualTarget
                _visualTarget = new VisualTarget(_hostVisual);
                _rootVisual = new ContainerVisual();
                _visualTarget.RootVisual = _rootVisual;
                _controller.RootVisualTarget = _rootVisual;

                _controller.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _controller.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Assets",
                    "KeyboardAnimation",
                    "index.html"
                );
                if (File.Exists(htmlPath))
                    _controller.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);

                ApplyBounds();

                // 等窗口完全布局后再次同步尺寸，确保 WebView 初始渲染区域正确
                Dispatcher.BeginInvoke(new Action(ApplyBounds), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                ShowError("WebView2 初始化失败：" + ex.Message);
            }
        });
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyBounds();

    private void ApplyBounds()
    {
        RunOnUi(() =>
        {
            if (_controller == null || _hostVisualHost == null)
                return;

            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0)
                scale = 1;
            _controller.RasterizationScale = scale;
            _controller.Bounds = new System.Drawing.Rectangle(
                0,
                0,
                (int)Math.Max(1, ActualWidth * scale),
                (int)Math.Max(1, ActualHeight * scale)
            );
        });
    }

    // WebView2 回调可能在非 UI 线程：先取值，controller 操作切回 UI 线程
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        bool ok = e.IsSuccess;
        RunOnUi(() =>
        {
            _webReady = ok;
            if (ok)
            {
                PushInitialCounts();

                // 分离窗口环境标记：隐藏页面内的"分离窗口"按钮，避免套娃
                var env = JsonSerializer.Serialize(new { type = "setEnv", overlay = true });
                _controller?.CoreWebView2.PostWebMessageAsJson(env);
            }
        });
    }

    // ---------- VM 事件转发（VM 事件在 UI 线程触发，双保险再切一次） ----------
    private void OnVmKeyPressed(string key, int count)
    {
        if (!_webReady || _controller == null)
            return;
        var message = JsonSerializer.Serialize(new { type = "press", key, count });
        RunOnUi(() => _controller?.CoreWebView2.PostWebMessageAsJson(message));
    }

    private void OnVmMouseClicked(string button, double x, double y)
    {
        if (!_webReady || _controller == null)
            return;
        var message = JsonSerializer.Serialize(new { type = "mouseClick", button, x, y });
        RunOnUi(() => _controller?.CoreWebView2.PostWebMessageAsJson(message));
    }

    private void PushInitialCounts()
    {
        if (_vm == null || _controller == null)
            return;
        var message = JsonSerializer.Serialize(
            new { type = "init", counts = _vm.GetInitialCounts() }
        );
        _controller.CoreWebView2.PostWebMessageAsJson(message);
    }

    private void ShowError(string message)
    {
        RunOnUi(() =>
        {
            ErrText.Text = message;
            ErrText.Visibility = Visibility.Visible;
        });
    }

    /// <summary>窗口获得激活时把键盘焦点交给 WebView（透明窗口需手动传递）</summary>
    public void FocusWebView()
    {
        RunOnUi(() => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic));
    }

    // ---------- 页面消息（WebView2 回调，可能在非 UI 线程） ----------
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 先在线程安全位置解析消息
        string? type = null;
        double dx = 0;
        double dy = 0;
        try
        {
            var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (root.TryGetProperty("dx", out var px))
                dx = px.GetDouble();
            if (root.TryGetProperty("dy", out var py))
                dy = py.GetDouble();
        }
        catch
        {
            return;
        }

        // UI 操作切回 UI 线程
        RunOnUi(() =>
        {
            if (type == "detach")
            {
                // 分离窗口内忽略，避免套娃
                return;
            }
            else if (type == "dragDelta")
            {
                if (System.Windows.Window.GetWindow(this) is KeyAnimationWindow w)
                    w.MoveBy(dx, dy);
            }
        });
    }
}