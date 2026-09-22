using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XAssistant.Services;

/// <summary>
/// 背景立绘的一份设置（开关 + 尺寸 + 位置 + 不透明度）。
/// 数值全是**屏幕百分比**而不是像素：换显示器、插拔副屏都不用重设，效果窗按虚拟屏自己换算。
/// </summary>
public sealed record IconBackdropSettings(
    bool On, double SizePercent, double XPercent, double YPercent, double OpacityPercent)
{
    /// <summary>默认：居中、占屏高 62%、16% 不透明度——压得住屏又不抢正文的读位。</summary>
    public static IconBackdropSettings Default { get; } = new(true, 62, 50, 50, 16);

    /// <summary>把用户填的野值钳回可用区间；非数字（面板刚建、手输字母）一律退回默认。</summary>
    public static IconBackdropSettings Clamp(IconBackdropSettings raw) => new(
        raw.On,
        ClampPercent(raw.SizePercent, Default.SizePercent, 5, 200),
        ClampPercent(raw.XPercent, Default.XPercent, 0, 100),
        ClampPercent(raw.YPercent, Default.YPercent, 0, 100),
        ClampPercent(raw.OpacityPercent, Default.OpacityPercent, 2, 100));

    private static double ClampPercent(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// 警告背后那颗「谁在说话」的剪影：按 <c>-from</c> 取对应 IDE 的图标，用图标自己的 alpha 当
/// <see cref="UIElement.OpacityMask"/>，颜色由这一组告警的档位染色。
///
/// 为什么走剪影而不是「让用户准备一张白色图」：白色图只是把染色推到美术侧，换个主题色就得重出图；
/// 拿 alpha 当遮罩、纯色铺底，任何一张带透明的 png 都能直接当立绘，颜色还跟着档位与主题走。
/// </summary>
public static class IconBackdrop
{
    /// <summary>当前生效的设置。<see cref="Modules.IconBackdropModule"/> 每 tick 从面板参数同步过来。</summary>
    public static IconBackdropSettings Current { get; set; } = IconBackdropSettings.Default;

    /// <summary>立绘图片目录：与消息卡徽章同一批图（256 px 见方），一处放图两处都能用。</summary>
    private static readonly string IconDir = Path.Combine(AppContext.BaseDirectory, "Assets", "IdeIcons");

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这条命令该垫哪张图：<c>-icon</c> 指定的文件优先（绝对或相对本目录都行），
    /// 否则按 <c>-from</c> 去 IdeIcons 里找同名 png。找不到返回 null——立绘整层收起，不硬凑。
    /// </summary>
    public static ImageSource? ImageFor(EffectCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.Icon)) return Load(command.Icon!);
        string from = command.Source?.Trim() ?? "";
        return from.Length == 0 ? null : Load(Path.Combine(IconDir, from.ToLowerInvariant() + ".png"));
    }

    /// <summary>读图。OnLoad 是为了不把文件锁住——用户随时可以换掉那张图。</summary>
    private static ImageSource? Load(string path)
    {
        try
        {
            string full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
            if (Cache.TryGetValue(full, out ImageSource? known)) return known;
            ImageSource? loaded = null;
            if (File.Exists(full))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(full, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                loaded = bmp;
            }
            Cache[full] = loaded;
            return loaded;
        }
        catch (Exception error) when (error is IOException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
