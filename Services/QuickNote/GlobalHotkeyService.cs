using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services.QuickNote;

// 全局热键服务：注册 RegisterHotKey，捕获 WM_HOTKEY 后触发事件
// 用隐藏的 NativeWindow 接收消息，不依赖主窗口的存在
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4E01; // 速记热键的注册 id

    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly QuickNoteSettings _settings;
    private HotkeyMessageWindow? _window;
    private bool _started;

    public event Action? HotKeyPressed;

    public GlobalHotkeyService(
        IConfigurationService configurationService,
        ILogger<GlobalHotkeyService> logger
    )
    {
        _logger = logger;
        _settings = configurationService.Settings.QuickNote;
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        try
        {
            (uint modifiers, uint vk) = QuickNoteHotKey.Parse(_settings.HotKey);
            _window = new HotkeyMessageWindow();
            _window.HotkeyMessage += () => HotKeyPressed?.Invoke();

            if (!_window.Register(HotkeyId, modifiers | QuickNoteHotKey.ModNoRepeat, vk))
            {
                int error = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "全局热键 {HotKey} 注册失败（错误码 {Error}，可能已被其他程序占用），可改用托盘菜单唤起",
                    _settings.HotKey,
                    error
                );
            }
            else
            {
                _logger.LogInformation("速记全局热键 {HotKey} 注册成功", _settings.HotKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "注册速记全局热键失败，请检查 HotKey 配置：{HotKey}",
                _settings.HotKey
            );
        }
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            _window.Unregister(HotkeyId);
            _window.DestroyHandle();
            _window = null;
        }
    }

    // 隐藏消息窗口：接收 WM_HOTKEY
    private sealed class HotkeyMessageWindow : NativeWindow
    {
        public event Action? HotkeyMessage;

        public HotkeyMessageWindow()
        {
            // 创建一个隐藏窗口，用于接收全局热键消息
            CreateHandle(new CreateParams());
        }

        public bool Register(int id, uint modifiers, uint vk) =>
            RegisterHotKey(Handle, id, modifiers, vk);

        public void Unregister(int id) => UnregisterHotKey(Handle, id);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
                HotkeyMessage?.Invoke();
            base.WndProc(ref m);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
