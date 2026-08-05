using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services.Interfaces;
using XAssistant.ViewModels;
using XAssistant.Views;

namespace XAssistant.Services.QuickNote;

// 速记唤起服务：全局热键/托盘触发时，速记窗已开则置顶聚焦，否则弹原生无边框速记窗并抓取前台窗口标题作来源
public sealed class QuickNoteCaptureService
{
    private const int MaxTitleLength = 200; // 来源标题截断上限
    private const int MonitorDefaultToNearest = 0x00000002;
    private const int MdTEffectiveDpi = 0; // GetDpiForMonitor 的 MDT_EFFECTIVE_DPI
    private const int DefaultDpi = 96;

    private readonly ILogger<QuickNoteCaptureService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IQuickNoteDatabaseService _dbService;
    private readonly ToastService _toastService;
    private readonly QuickNoteSettings _settings;

    private QuickNoteWindow? _window; // 自持窗体引用：单实例复用，无需窗口标题标记

    public QuickNoteCaptureService(
        IConfigurationService configurationService,
        IQuickNoteDatabaseService dbService,
        ToastService toastService,
        ILoggerFactory loggerFactory
    )
    {
        _logger = loggerFactory.CreateLogger<QuickNoteCaptureService>();
        _loggerFactory = loggerFactory;
        _dbService = dbService;
        _toastService = toastService;
        _settings = configurationService.Settings.QuickNote;
    }

    // xapp SPA 基址：优先配置，否则随构建配置（Debug 开发版 :3009 / Release 生产版 :9009）
    public string BaseUrl =>
        _settings.SpaBaseUrl
#if DEBUG
        ?? "http://localhost:3009"
#else
        ?? "http://localhost:9009"
#endif
        ;

    // 唤起速记窗：已打开则置顶聚焦（不覆盖已输入内容与来源），否则新开并带上当前前台标题
    public void InvokeCapture()
    {
        if (_window is { IsVisible: true } existing)
        {
            _logger.LogDebug("速记窗已打开，仅置顶聚焦");
            existing.BringToFront();
            return;
        }

        // 新开：唤起瞬间抓取前台窗口标题作来源（前台是本应用自己时不算，避免来源变成 "XAssistant"），并定位在它所在屏居中
        IntPtr foreground = GetForegroundWindow();
        string source = IsOwnProcess(foreground)
            ? ""
            : Truncate(GetWindowTitle(foreground) ?? "");
        (double left, double top) = ComputeWindowPosition(foreground);

        var viewModel = new QuickNoteViewModel(
            _dbService,
            _loggerFactory.CreateLogger<QuickNoteViewModel>()
        )
        {
            Source = source,
        };
        // 保存成功 → 关窗（窗口自行订阅）+ 弹 toast 确认已落库
        viewModel.SaveSucceeded += () => _toastService.Show("速记已保存");
        _window = new QuickNoteWindow(viewModel, _settings) { Left = left, Top = top };
        _window.BringToFront(); // 显示并抢前台焦点、聚焦输入框
        _logger.LogInformation("唤起速记窗，来源：{Source}", source);
    }

    // 打开速记列表页（普通浏览器标签）
    public void OpenList()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"{BaseUrl}/quick-note") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开速记列表页失败");
        }
    }

    // 新窗位置：前台窗口所在屏工作区居中（WPF 坐标为 DIP，物理像素按该屏 DPI 换算）
    // 注：多屏缩放不一致时会有少量偏移，v1 不追求高 DPI 精细适配（见 spec 出域项）
    private (double left, double top) ComputeWindowPosition(IntPtr foreground)
    {
        IntPtr monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return (0, 0); // 拿不到工作区时放主屏原点，由系统默认放置

        int dpiX = DefaultDpi;
        int dpiY = DefaultDpi;
        if (GetDpiForMonitor(monitor, MdTEffectiveDpi, out uint dx, out uint dy) == 0)
        {
            dpiX = (int)dx;
            dpiY = (int)dy;
        }

        // 窗口物理尺寸按目标屏 DPI 放大后在工作区居中，再换算回 DIP 作为 Left/Top
        RECT work = info.rcWork;
        int winWidthPx = (int)(_settings.WindowWidth * dpiX / (double)DefaultDpi);
        int winHeightPx = (int)(_settings.WindowHeight * dpiY / (double)DefaultDpi);
        int xPx = Math.Max(work.Left, work.Left + (work.Width - winWidthPx) / 2);
        int yPx = Math.Max(work.Top, work.Top + (work.Height - winHeightPx) / 2);
        double left = xPx * DefaultDpi / (double)dpiX;
        double top = yPx * DefaultDpi / (double)dpiY;
        return (left, top);
    }

    private static string Truncate(string s) =>
        s.Length <= MaxTitleLength ? s : s[..MaxTitleLength];

    // 前台窗口是否属于本应用（XAssistant 自己的窗口作来源没有意义）
    private static bool IsOwnProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    // 读取窗口标题（best-effort；UIPI 下提升权限的窗口读不到，返回 null）
    private static string? GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return null;
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        string title = sb.ToString().Trim();
        return title.Length > 0 ? title : null;
    }

    // ---------- P/Invoke ----------
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr hmonitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
