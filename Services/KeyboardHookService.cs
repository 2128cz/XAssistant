using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyboardHookService : IKeyboardHookService, IDisposable
{
    public event Action<string>? KeyPressed;

    public event Action<string>? TextInput;
    private readonly InputEventDispatcher? _events;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly ILogger<KeyboardHookService> _logger;
    private LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // 长按重复检测按物理键（扫描码 + 扩展位）记录：GetKeyNameText 会把主键盘 4 与小键盘 4 都
    // 命名成 “4”、两个 “.” 都命名成 “.”，按名字去重会吞掉其中一把键；丢过一次 key-up 时陈旧的
    // 按下记录也会按实时键态在这里自愈，否则某个字符会从此永久打不出来（练习会卡在半句）。
    private readonly Dictionary<int, int> _pressedPhysical = new(); // 物理键 -> vkCode

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    public KeyboardHookService(ILogger<KeyboardHookService> logger, InputEventDispatcher? events = null)
    {
        _logger = logger;
        _events = events;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        _pressedPhysical.Clear(); // 上一段监听里没等到 key-up 的记录不能带进来
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _pressedPhysical.Clear();
        }
    }

    private static int PhysicalKey(KBDLLHOOKSTRUCT key) => (key.scanCode << 1) | ((key.flags & 1) != 0 ? 1 : 0);
    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    // Ctrl / Alt（含右 Alt 即 AltGr）/ Win 按下时不产出文本，只记键名。
    private static bool ComboDown() => IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);

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
                string keyName = GetKeyNameFromScanCode(kb.vkCode, kb.scanCode, kb.flags);
                int physical = PhysicalKey(kb);

                if (isKeyDown)
                {
                    // 只有同一把物理键且系统键态显示它仍按下，才是长按重复。
                    if (kb.vkCode != 8 && _pressedPhysical.TryGetValue(physical, out var heldVk) && IsDown(heldVk))
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);

                    _pressedPhysical[physical] = kb.vkCode;
                    string? input = null;
                    if (!ComboDown())
                    {
                        input = kb.vkCode switch { 8 => "\b", 13 => "\r", _ => TranslateInput(kb) };

                    }
                    void Deliver()
                    {
                        if (_events == null) { KeyPressed?.Invoke(keyName); if (!string.IsNullOrEmpty(input)) TextInput?.Invoke(input); }
                        else { _events.Deliver(KeyPressed, keyName); if (!string.IsNullOrEmpty(input)) _events.Deliver(TextInput, input); }
                    }
                    if (_events == null) Deliver(); else _events.Publish(Deliver);
                }
                else // isKeyUp
                {
                    _pressedPhysical.Remove(physical);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private string? TranslateInput(KBDLLHOOKSTRUCT key)
    {
        var state = new byte[256];
        GetKeyboardState(state);
        state[0x10] = (byte)(IsDown(0x10) ? 0x80 : 0); // 实时取 Shift，不依赖按下集合里残留的键名
        state[0x11] = state[0x12] = 0;
        state[key.vkCode] |= 0x80;
        var buffer = new StringBuilder(8);
        var thread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        // Flag 4 avoids changing the foreground application's dead-key state.
        int count = ToUnicodeEx((uint)key.vkCode, (uint)key.scanCode, state, buffer, buffer.Capacity, 4, GetKeyboardLayout(thread));
        return count > 0 ? new string(buffer.ToString().Take(count).Where(c => !char.IsControl(c)).ToArray()) : null;
    }
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder buffer, int size, uint flags, IntPtr layout);

    // 左右修饰键固定映射：GetKeyNameText 对左/右 Shift、Ctrl、Alt、Win 的命名依赖键盘布局
    // （部分布局自带 “Left/Right” 方位词，多数不区分），统一走这里保证左右稳定可区分。
    // Win 用 “Left Windows/Right Windows” 与正式版历史数据保持一致
    private static readonly Dictionary<int, string> ModifierKeyNames = new()
    {
        [0x5B] = "Left Windows", // VK_LWIN
        [0x5C] = "Right Windows", // VK_RWIN
        [0xA0] = "Shift", // VK_LSHIFT
        [0xA1] = "Right Shift", // VK_RSHIFT
        [0xA2] = "Ctrl", // VK_LCONTROL
        [0xA3] = "Right Ctrl", // VK_RCONTROL
        [0xA4] = "Alt", // VK_LMENU
        [0xA5] = "Right Alt", // VK_RMENU
    };

    // 把扫描码 + 扩展标志合成 GetKeyNameText 所需的参数
    private string GetKeyNameFromScanCode(int vkCode, int scanCode, int flags)
    {
        // 左右修饰键优先走固定映射，避免布局差异
        if (ModifierKeyNames.TryGetValue(vkCode, out var modifierName))
            return modifierName;

        // 如果设置了扩展位（flags & 1），需要给扫描码加上 0x100
        bool isExtended = (flags & 1) != 0;
        int lParamValue = (scanCode << 16) | (isExtended ? 0x1000000 : 0);

        var sb = new StringBuilder(256);
        int result = GetKeyNameText(lParamValue, sb, sb.Capacity);
        if (result > 0)
            return sb.ToString();

        // GetKeyNameText 无法命名（常见于笔记本 Fn 键等非标准键）。
        // 扫描码 0x63 是多数笔记本 Fn 的扫描码；其余保留原始码值以便后续识别
        if (scanCode == 0x63)
            return "Fn";
        _logger.LogWarning("按键无法识别，扫描码 0x{Scan:X2}，虚拟键码 0x{Vk:X2}，标志 0x{Flags:X2}", scanCode, vkCode, flags);
        return $"Unknown (scan 0x{scanCode:X2}, vk 0x{vkCode:X2})";
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
