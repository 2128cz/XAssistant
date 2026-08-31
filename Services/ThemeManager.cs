using System;
using System.Linq;
using System.Windows;

namespace XAssistant.Services;

/// <summary>
/// 应用主题管理器：运行时整体替换 Application 资源中的主题字典（浅色 / 深色）。
/// 主题字典键名在 ThemeLight.xaml 与 ThemeDark.xaml 中一一对应，
/// 界面全部通过 DynamicResource 引用，切换后自动重绘。
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    private const string ThemeDirectory = "pack://application:,,,/Themes/";

    /// <summary>当前生效的主题（Light / Dark）</summary>
    public static string Current { get; private set; } = Light;

    /// <summary>应用指定主题；theme 非法时回退浅色</summary>
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
                && m.Source.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase)
            );
        if (existing != null)
            app.Resources.MergedDictionaries.Remove(existing);

        // 加载新主题字典
        app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri(ThemeDirectory + fileName, UriKind.Absolute) }
        );
    }
}