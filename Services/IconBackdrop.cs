using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;

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

    /// <summary>
    /// 剪影可用的透明度区间。两头都得拦：
    /// 全透明的图（本机 `dsh.png` 实测平均 alpha = 0）当遮罩等于什么都不画；
    /// 几乎全不透明的图（`xassistant.png` 覆盖 1.00、`trae.png` 0.95）当遮罩就是一块实心方砖——
    /// 用户原话「显示一个纯色块没搞懂要干啥」。只有中间那段真的带着形状。
    /// </summary>
    public const double MinOpaqueRatio = 0.05;
    public const double MaxOpaqueRatio = 0.90;

    /// <summary>这张图能不能当剪影用（取不到、或透明度落在形状区间外都算不能用）。</summary>
    public static bool Usable(ImageSource? image) =>
        image is BitmapSource source && OpaqueRatio(source) is { } ratio
        && ratio >= MinOpaqueRatio && ratio <= MaxOpaqueRatio;

    /// <summary>不透明像素占比：按 alpha 通道抽样，每四个像素取一个（512² 也就 6.5 万次比较）。</summary>
    private static double? OpaqueRatio(BitmapSource image)
    {
        try
        {
            var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int width = pixels.PixelWidth;
            int height = pixels.PixelHeight;
            if (width == 0 || height == 0) return null;
            int stride = width * 4;
            var buffer = new byte[stride * height];
            pixels.CopyPixels(buffer, stride, 0);
            int total = 0;
            int solid = 0;
            for (int y = 0; y < height; y += 2)
                for (int x = 0; x < width; x += 2)
                {
                    total++;
                    if (buffer[y * stride + x * 4 + 3] > 128) solid++;
                }
            return total == 0 ? null : (double)solid / total;
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException)
        {
            return null;   // 读不出 alpha 就当不能用：宁可少一张立绘，不在屏上摆一块方砖
        }
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
                if (Usable(bmp)) loaded = bmp;
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
