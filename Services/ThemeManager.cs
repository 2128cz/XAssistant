using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Color / ColorConverter 会跟 WPF 的撞名
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace XAssistant.Services;

/// <summary>
/// 应用主题管理器：运行时整体替换 Application 资源中的主题字典（浅色 / 深色），
/// 并允许在主题之上再叠一层**色板**——只覆盖指定的几个画刷键，其余仍走主题原色。
/// 主题字典键名在 ThemeLight.xaml 与 ThemeDark.xaml 中一一对应，界面全部通过
/// DynamicResource 引用，切换或覆盖后自动重绘。
///
/// 有了这一层，主题色就是纯数据：任何代码（包括关键词换肤）都能给出
/// 「画刷键 → 颜色」的字典即时改外观，不必再为每种配色新增一个 XAML 文件。
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>
    /// 主题字典的位置。带上组件名而不只写 /Themes/…：相对形式是按「入口程序集」解的，
    /// 离屏夹具引用本程序时入口是夹具自己，不写组件名就找不到字典（App.xaml 里那句相对路径不受影响，
    /// 它编译进本程序集的 BAML，按本程序集解）。
    /// </summary>
    private const string ThemeDirectory = "pack://application:,,,/XAssistant;component/Themes/";

    /// <summary>
    /// 色板能覆盖的画刷键。写在这里是为了让代码知道自己能改什么（也用来挡掉拼错的键），
    /// 颜色本身仍由两份主题 XAML 给——色板只是叠在上面的可选一层。
    /// </summary>
    public static readonly IReadOnlyList<string> BrushKeys =
    [
        "WindowBackground", "MenuBackground", "CardBackground", "CardBorderBrush",
        "PanelBackground", "ControlBackground", "ControlBorder",
        "DataGridBackground", "DataGridHeaderBackground",
        "TextPrimary", "TextSecondary", "TextHint",
        "AccentBrush", "AccentText", "HighlightNumber", "LinkBrush", "DangerBrush", "SuccessBrush",
        "KeyChipBackground", "KeyChipText",
        "NavHoverBackground", "NavSelectedBackground", "NavSelectedForeground",
        "DashboardNumber", "DashboardWatermark",
    ];

    /// <summary>当前垫底的主题（Light / Dark）</summary>
    public static string Current { get; private set; } = Light;

    /// <summary>当前生效的色板名；null 表示没有覆盖层，就是主题原色。</summary>
    public static string? PaletteName { get; private set; }

    /// <summary>应用指定主题；theme 非法时回退浅色。切主题会一并抹掉色板覆盖。</summary>
    public static void Apply(string theme)
    {
        var app = System.Windows.Application.Current;
        if (app == null)
            return;

        bool isDark = string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase);
        Current = isDark ? Dark : Light;

        string fileName = isDark ? "ThemeDark.xaml" : "ThemeLight.xaml";

        // 移除旧主题字典（匹配 Themes 目录下的主题文件）
        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(m =>
                m.Source != null
                && m.Source.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase)
            );
        if (existing != null)
            app.Resources.MergedDictionaries.Remove(existing);

        // 色板是叠在主题上的覆盖层，垫底主题都换了，覆盖层不能再留着
        RemovePalette();

        // 加载新主题字典
        app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri(ThemeDirectory + fileName, UriKind.Absolute) }
        );
    }

    /// <summary>
    /// 在指定主题上叠一层色板：colors 里认得的画刷键被覆盖，认不出的键一律忽略
    /// （宁可少改一个颜色，也不要因为 JSON 里打错一个字让界面出现没样式的控件）。
    /// </summary>
    public static void ApplyPalette(string @base, string name, IReadOnlyDictionary<string, string> colors)
    {
        var app = System.Windows.Application.Current;
        if (app == null)
            return;
        if (Current != @base)
            Apply(@base);

        var palette = new ResourceDictionary();
        foreach ((string key, string value) in colors)
        {
            if (!BrushKeys.Contains(key, StringComparer.Ordinal))
                continue;
            try
            {
                palette[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            }
            catch (FormatException)
            {
                // 颜色写错了就跳过这一条：换肤是彩蛋，不该有让界面崩掉的可能
            }
        }
        if (palette.Count == 0)
        {
            RemovePalette();
            return;
        }

        RemovePalette();
        // 追加在末尾：合并字典里后加入的优先，DynamicResource 会立刻跟着重绘
        app.Resources.MergedDictionaries.Add(palette);
        _palette = palette;
        PaletteName = name;
    }

    /// <summary>撤掉色板，回到当前主题的原色。</summary>
    public static void ClearPalette()
    {
        RemovePalette();
        var app = System.Windows.Application.Current;
        if (app != null)
            // 重新走一遍 Apply 把主题字典放回末尾（色板曾排在它后面，摘掉后顺序仍是对的，这里只是保证幂等）
            Apply(Current);
    }

    /// <summary>读某个画刷键当前生效的颜色。色板与主题都算在内；键不存在或不是画刷时返回 null。</summary>
    public static Color? ColorOf(string key)
    {
        var app = System.Windows.Application.Current;
        if (app == null)
            return null;
        return app.Resources[key] switch
        {
            SolidColorBrush brush => brush.Color,
            Color color => color,
            _ => null,
        };
    }

    private static ResourceDictionary? _palette;

    private static void RemovePalette()
    {
        var app = System.Windows.Application.Current;
        if (_palette != null && app != null)
            app.Resources.MergedDictionaries.Remove(_palette);
        _palette = null;
        PaletteName = null;
    }
}
