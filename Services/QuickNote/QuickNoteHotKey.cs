namespace XAssistant.Services.QuickNote;

// 热键字符串解析：形如 "Win+Numpad0"、"Ctrl+Alt+N"
// 修饰键 + 按键，按键为最后一段
internal static class QuickNoteHotKey
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000; // 按住不放不重复触发

    private static readonly Dictionary<string, uint> ModifierMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = ModControl,
            ["alt"] = ModAlt,
            ["shift"] = ModShift,
            ["win"] = ModWin,
        };

    private static readonly Dictionary<string, uint> KeyMap = BuildKeyMap();

    private static Dictionary<string, uint> BuildKeyMap()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["space"] = 0x20,
            ["enter"] = 0x0D,
            ["tab"] = 0x09,
            ["esc"] = 0x1B,
            ["backspace"] = 0x08,
            ["delete"] = 0x2E,
            ["insert"] = 0x2D,
            ["home"] = 0x24,
            ["end"] = 0x23,
            ["pageup"] = 0x21,
            ["pagedown"] = 0x22,
            ["numpad0"] = 0x60,
            ["numpad1"] = 0x61,
            ["numpad2"] = 0x62,
            ["numpad3"] = 0x63,
            ["numpad4"] = 0x64,
            ["numpad5"] = 0x65,
            ["numpad6"] = 0x66,
            ["numpad7"] = 0x67,
            ["numpad8"] = 0x68,
            ["numpad9"] = 0x69,
        };
        // 字母 A-Z：0x41-0x5A
        for (char c = 'A'; c <= 'Z'; c++)
            map[c.ToString()] = (uint)(0x41 + (c - 'A'));
        // 数字 0-9：0x30-0x39
        for (char c = '0'; c <= '9'; c++)
            map[c.ToString()] = (uint)(0x30 + (c - '0'));
        // 功能键 F1-F24：0x70-0x87
        for (int i = 1; i <= 24; i++)
            map[$"f{i}"] = (uint)(0x70 + i - 1);
        return map;
    }

    public static (uint modifiers, uint vk) Parse(string hotKey)
    {
        if (string.IsNullOrWhiteSpace(hotKey))
            throw new ArgumentException("热键配置不能为空", nameof(hotKey));

        string[] parts = hotKey.Split(
            '+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
        );
        if (parts.Length < 2)
            throw new ArgumentException(
                $"热键配置格式应为「修饰键+按键」，如「Win+Numpad0」，当前：{hotKey}",
                nameof(hotKey)
            );

        uint modifiers = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!ModifierMap.TryGetValue(parts[i], out uint m))
                throw new ArgumentException($"未知修饰键：{parts[i]}", nameof(hotKey));
            modifiers |= m;
        }

        if (!KeyMap.TryGetValue(parts[^1], out uint vk))
            throw new ArgumentException($"未知按键：{parts[^1]}", nameof(hotKey));

        return (modifiers, vk);
    }
}
