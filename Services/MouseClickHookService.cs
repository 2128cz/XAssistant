using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class MouseClickHookService : IMouseClickHookService, IDisposable
{
    public event Action<string>? MouseClicked; // 传入按钮名称 "Left"/"Middle"/"Right"
    public event Action<string, int, int>? MouseClickedAt; // 按钮名称 + 屏幕坐标 X/Y

    private readonly InputEventDispatcher? _events;
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MBUTTONDOWN = 0x0207;

    /// <summary>一格滚轮的标准增量；现代滚轮会给出小于它的分数值，所以按格数折算而不是整除计数。</summary>
    private const int WheelDelta = 120;

    /// <summary>
    /// 单次移动事件超过该位移就判为指针被程序传送（焦点跟随、远程桌面、游标归中等），
    /// 只重锚不记距离——否则一次瞬移就能顶上去好几“米”。
    /// </summary>
    private const int TeleportPixels = 600;

    private LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // 移动累加量。钩子回调运行在安装钩子的 UI 线程上，取样定时器也在 UI 线程，
    // 因此不需要锁；WM_MOUSEMOVE 每秒可达上千次，这里只做整数累加。
    private int _lastX;
    private int _lastY;
    private bool _hasAnchor;
    private long _pendingDeltaX;
    private long _pendingDeltaY;
    private double _pendingPathPixels;
    private long _pendingMoveCount;
    private int _pendingWheelDelta;
    private DateTime _lastReadAt;

    public MouseClickHookService(InputEventDispatcher? events = null)
    {
        _events = events;
        _proc = HookCallback;
        _lastReadAt = DateTime.UtcNow;
    }

    public void Start()
    {
        // 防止重复启动，导致多个钩子并存
        if (_hookId != IntPtr.Zero)
            return; // 已有一个钩子在运行

        // 获取模块名（优化后写法，避免 using 风险）
        string? moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
        if (string.IsNullOrEmpty(moduleName))
            throw new InvalidOperationException("无法获取主模块名称，钩子安装失败。");

        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(moduleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SetWindowsHookEx 失败，错误代码：{error}");
        }

        // 钩子刚装上时还没有参考点，先丢弃上一段残留
        ResetMovementTracking();
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        ResetMovementTracking();
    }

    /// <summary>
    /// 累加一次指针移动。不做任何字符串拼接与事件推送，钩子回调里多一分开销就多点丢消息的风险。
    /// </summary>
    private void TrackMovement(int x, int y)
    {
        if (!_hasAnchor)
        {
            _hasAnchor = true;
            _lastX = x;
            _lastY = y;
            return;
        }
        int dx = x - _lastX;
        int dy = y - _lastY;
        _lastX = x;
        _lastY = y;
        if (dx == 0 && dy == 0)
            return; // 系统为重绘发的合成移动事件，不算位移
        if (Math.Abs(dx) > TeleportPixels || Math.Abs(dy) > TeleportPixels)
            return; // 判为指针传送，只重锚（上面的 _lastX/_lastY 已更新）
        _pendingDeltaX += dx;
        _pendingDeltaY += dy;
        _pendingPathPixels += Math.Sqrt((double)dx * dx + (double)dy * dy);
        _pendingMoveCount++;
    }

    /// <summary>
    /// 取走自上次调用以来累积的移动量。采用“拉”而不是“推”，消费方按自己的刷新节奏决定窗口长短。
    /// </summary>
    public MouseMovementSample? ReadMovementSample()
    {
        if (_pendingMoveCount == 0 && _pendingWheelDelta == 0)
            return null;
        var now = DateTime.UtcNow; // 单调推进的 UTC 时钟，本地时间调整不会算出负时长
        var elapsed = Math.Max(0.001, (now - _lastReadAt).TotalSeconds);
        var sample = new MouseMovementSample(
            ClampToInt(_pendingDeltaX),
            ClampToInt(_pendingDeltaY),
            _pendingPathPixels,
            elapsed,
            _lastX,
            _lastY,
            _pendingWheelDelta / (double)WheelDelta);
        _pendingDeltaX = 0;
        _pendingDeltaY = 0;
        _pendingPathPixels = 0;
        _pendingMoveCount = 0;
        _pendingWheelDelta = 0;
        _lastReadAt = now;
        return sample;
    }

    public void ResetMovementTracking()
    {
        _hasAnchor = false;
        _pendingDeltaX = 0;
        _pendingDeltaY = 0;
        _pendingPathPixels = 0;
        _pendingMoveCount = 0;
        _pendingWheelDelta = 0;
        _lastReadAt = DateTime.UtcNow;
    }

    // 正常情况下窗口内累加不会溢出 int；长时间无人取样时钳位而不是环切成负数
    private static int ClampToInt(long value) =>
        (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var mh = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case WM_MOUSEMOVE:
                    TrackMovement(mh.pt.X, mh.pt.Y);
                    break;
                case WM_MOUSEWHEEL:
                    // 高 16 位是有符号的滚动增量，向上为正
                    _pendingWheelDelta += unchecked((short)(mh.mouseData >> 16));
                    break;
                case WM_LBUTTONDOWN:
                    PublishClick("Left", mh.pt.X, mh.pt.Y);
                    break;
                case WM_MBUTTONDOWN:
                    PublishClick("Middle", mh.pt.X, mh.pt.Y);
                    break;
                case WM_RBUTTONDOWN:
                    PublishClick("Right", mh.pt.X, mh.pt.Y);
                    break;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void PublishClick(string button, int x, int y)
    {
        void Deliver()
        {
            if (_events == null) { MouseClicked?.Invoke(button); MouseClickedAt?.Invoke(button, x, y); return; }
            _events.Deliver(MouseClicked, button);
            if (MouseClickedAt != null)
                foreach (Action<string,int,int> handler in MouseClickedAt.GetInvocationList())
                    _events.Deliver<(string Button,int X,int Y)>(p => handler(p.Button,p.X,p.Y), (button,x,y));
        }
        if (_events == null) Deliver(); else _events.Publish(Deliver);
    }
    // 低层鼠标钩子结构：含屏幕坐标
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt; // 屏幕坐标
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // P/Invoke 声明
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose() => Stop();
}
