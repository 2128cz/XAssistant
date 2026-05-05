using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyboardHookService : IKeyboardHookService, IDisposable
{
    public event Action<string>? KeyPressed;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // 记录当前正处于按下状态的键（名称）
    private readonly HashSet<string> _pressedKeys = new(StringComparer.Ordinal);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    public KeyboardHookService()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
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
            int msg = (int)wParam;
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                string keyName = GetKeyNameFromScanCode(kb.scanCode, kb.flags);

                if (isKeyDown)
                {
                    // 如果该键已经处于按下状态，则为长按重复，直接忽略
                    if (_pressedKeys.Contains(keyName))
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);

                    _pressedKeys.Add(keyName);
                    KeyPressed?.Invoke(keyName);
                }
                else // isKeyUp
                {
                    _pressedKeys.Remove(keyName);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // 把扫描码 + 扩展标志合成 GetKeyNameText 所需的参数
    private static string GetKeyNameFromScanCode(int scanCode, int flags)
    {
        // 如果设置了扩展位（flags & 1），需要给扫描码加上 0x100
        bool isExtended = (flags & 1) != 0;
        int lParamValue = (scanCode << 16) | (isExtended ? 0x1000000 : 0);

        var sb = new StringBuilder(256);
        int result = GetKeyNameText(lParamValue, sb, sb.Capacity);
        if (result > 0)
            return sb.ToString();
        else
            return "Unknown"; // fallback
    }

    // 结构体定义
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    // P/Invoke
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
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
