using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input; // 注意 Avalonia 也有 Input，用 System.Windows.Input 明确按钮

namespace XAssistant.Services;

public class MouseClickHookService : IDisposable
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
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
            GetModuleHandle(curModule.ModuleName), 0);
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
    private static extern IntPtr SetWindowsHookEx(int idHook,
        LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose() => Stop();
}