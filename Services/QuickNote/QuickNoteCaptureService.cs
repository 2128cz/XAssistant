using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services.QuickNote;

// 速记唤起服务：抓取前台窗口标题作为来源，检测已打开的捕获窗并聚焦，否则新开
public sealed class QuickNoteCaptureService
{
    private const int MaxTitleLength = 200; // 来源标题 URL 编码前截断
    private const int SwRestore = 9;
    private const int MonitorDefaultToNearest = 0x00000002;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    private static readonly string[] ChromePaths =
    {
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    };

    private static readonly string[] EdgePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    };

    private readonly ILogger<QuickNoteCaptureService> _logger;
    private readonly QuickNoteSettings _settings;

    public QuickNoteCaptureService(
        IConfigurationService configurationService,
        ILogger<QuickNoteCaptureService> logger
    )
    {
        _logger = logger;
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

    // 唤起捕获窗：已打开则置顶聚焦（不覆盖已输入内容与来源），否则新开并带上当前前台标题
    public void InvokeCapture()
    {
        IntPtr? existing = FindCaptureWindow();
        if (existing.HasValue)
        {
            _logger.LogDebug("捕获窗已打开，仅置顶聚焦");
            BringToFront(existing.Value);
            return;
        }
        LaunchCaptureWindow();
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

    private void LaunchCaptureWindow()
    {
        try
        {
            string? browser = FindBrowserPath();
            if (browser is null)
            {
                _logger.LogError("未找到可用的 Chrome/Edge 浏览器，无法唤起捕获窗");
                return;
            }

            string? title = GetWindowTitle(GetForegroundWindow());
            string src = title is null ? "" : $"?src={Uri.EscapeDataString(Truncate(title))}";
            string url = $"{BaseUrl}/quick-note/capture{src}";

            (int x, int y) = ComputeWindowPosition();
            string args =
                $"--app=\"{url}\" --window-size={_settings.WindowWidth},{_settings.WindowHeight} --window-position={x},{y}";

            Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false });
            _logger.LogInformation("唤起捕获窗：{Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动浏览器唤起捕获窗失败");
        }
    }

    // 按标题标记找已打开的捕获窗（chrome/edge 进程 + 可见未最小化 + 标题前缀）
    private IntPtr? FindCaptureWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(
            (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                    return true; // 只看可见且未最小化的窗口
                if (!IsBrowserWindow(hwnd))
                    return true; // 只看 chrome/edge，避免标题撞名
                if (
                    GetWindowTitle(hwnd)?.StartsWith(
                        _settings.TitleMarker,
                        StringComparison.Ordinal
                    ) == true
                )
                {
                    found = hwnd;
                    return false; // 找到即停止枚举
                }
                return true;
            },
            IntPtr.Zero
        );
        return found == IntPtr.Zero ? null : found;
    }

    // 置顶并聚焦（最小化时先还原）
    private static void BringToFront(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    private static bool IsBrowserWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string name = process.ProcessName;
            return name.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                || name.Equals("msedge", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // 进程已退出
        }
    }

    private string? FindBrowserPath()
    {
        string browser = _settings.Browser?.Trim().ToLowerInvariant() ?? "";
        IEnumerable<string> candidates = browser switch
        {
            "chrome" => ChromePaths,
            "edge" => EdgePaths,
            _ => ChromePaths.Concat(EdgePaths), // 默认：优先 Chrome，Edge 兜底
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // 新窗位置：当前前台窗口所在屏幕的工作区居中；无前台窗口时用主屏
    // 注意：GetWindowRect 返回物理像素，高 DPI 下 --window-position 存在缩放差异（v1 简化居中，精细适配见 spec 出域）
    private (int x, int y) ComputeWindowPosition()
    {
        // hwnd 为 NULL 时 MonitorFromWindow 返回主显示器
        IntPtr fg = GetForegroundWindow();
        IntPtr monitor = MonitorFromWindow(fg, MonitorDefaultToNearest);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        RECT work = default;
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            work = info.rcWork;

        // 拿到工作区后在其中居中；拿不到时让浏览器默认放置
        int x = work.Right == 0 ? 0 : work.Left + (work.Width - _settings.WindowWidth) / 2;
        int y = work.Bottom == 0 ? 0 : work.Top + (work.Height - _settings.WindowHeight) / 2;
        return (Math.Max(x, 0), Math.Max(y, 0));
    }

    private static string Truncate(string s) =>
        s.Length <= MaxTitleLength ? s : s[..MaxTitleLength];

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
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags
    );

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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
