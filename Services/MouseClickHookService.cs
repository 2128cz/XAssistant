using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class MouseClickHookService : IMouseClickHookService, IDisposable
{
    public event Action<string>? MouseClicked; // 传入按钮名称 "Left"/"Middle"/"Right"

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_RBUTTONDOWN = 0x0204;

    private LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public MouseClickHookService()
    {
        _proc = HookCallback;
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
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN:
                    MouseClicked?.Invoke("Left");
                    break;
                case WM_MBUTTONDOWN:
                    MouseClicked?.Invoke("Middle");
                    break;
                case WM_RBUTTONDOWN:
                    MouseClicked?.Invoke("Right");
                    break;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
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
