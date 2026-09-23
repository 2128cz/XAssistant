using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XAssistant.Services;

/// <summary>
/// 一个来源（命令行的 <c>-from</c>）的背景立绘参数。只由<b>这个来源对应的模块</b>发布
/// （<see cref="Modules.IconBackdropModule"/> 基类），模块关掉 / 勾掉就撤回。
/// 数值全是<b>屏幕百分比</b>而不是像素：换显示器、插拔副屏都不用重设，效果窗按虚拟屏自己换算。
/// </summary>
/// <param name="Source">来源名：与 <c>-from</c> 对齐，也是默认图名（<c>IdeIcons/&lt;source&gt;.png</c>）。</param>
/// <param name="IconFile">图标覆盖：留空 = 按来源自动取图；可以只写名字，也可以写路径。</param>
public sealed record IconBackdropTuning(
    string Source, string IconFile,
    double SizePercent, double XPercent, double YPercent, double OpacityPercent);

/// <summary>
/// 为某一条命令算好的立绘：图 + 怎么摆。<b>算不出来就是 null，效果窗那一层什么都不画</b>——
/// 这里刻意不留「默认形状」，因为一张来路不明的圆角矩形比没有立绘更让人摸不着头脑。
/// </summary>
public sealed record IconBackdropStyle(
    ImageSource Image,
    double SizePercent, double XPercent, double YPercent, double OpacityPercent);

/// <summary>
/// 警告背后那颗「谁在说话」的剪影：<b>来源 → 参数</b>的发布表 + 按命令行解析成一张图。
///
/// 为什么是发布表而不是一个全局开关（上一版就是全局开关，踩了坑）：立绘属于「这个 IDE 的消息长什么样」，
/// 只有那个 IDE 的模块知道自己该垫哪张图。全局开关配一份静态默认值，等于所有来源共用一个默认——
/// 模块没激活、甚至从没激活过时默认值照样是「开」，面板上把卡关掉屏上还垫着。发布表没有这条路：
/// 没人发布 = 表里没有 = 画不出来。
///
/// 染色走剪影而不是「让用户准备一张白色图」：白色图只是把染色推到美术侧，换个主题色就得重出图；
/// 拿图标自己的 alpha 当 <see cref="UIElement.OpacityMask"/>、纯色铺底，任何一张带透明的 png
/// 都能直接当立绘，颜色还跟着档位与主题走。
/// </summary>
public static class IconBackdrop
{
    /// <summary>没有任何模块认领、但命令自己写了 <c>-icon</c> 时的摆放：居中、屏高 62%、16% 不透明度。</summary>
    public const double NeutralSizePercent = 62, NeutralXPercent = 50, NeutralYPercent = 50, NeutralOpacityPercent = 16;

    /// <summary>面板各行的默认值与钳制区间，基类与效果窗共用这一份口径。</summary>
    public const double MinSize = 5, MaxSize = 200, MinOpacity = 2, MaxOpacity = 100;

    /// <summary>来源名（忽略大小写）→ 该来源当前发布的参数。只有激活中且勾了「启用」的模块在往里写。</summary>
    private static readonly ConcurrentDictionary<string, IconBackdropTuning> Published =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>立绘图片目录：与消息卡徽章同一批图（256 px 见方），一处放图两处都能用。</summary>
    private static readonly string IconDir = Path.Combine(AppContext.BaseDirectory, "Assets", "IdeIcons");

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>某个来源发布了什么（没模块认领返回 null）。</summary>
    public static IconBackdropTuning? TuningFor(string source) =>
        string.IsNullOrWhiteSpace(source) ? null
        : Published.TryGetValue(source.Trim(), out IconBackdropTuning? tuning) ? tuning : null;

    /// <summary>发布／覆盖一个来源的参数（值相同就不算改动，模块每 tick 无脑调也无害）。</summary>
    public static void Publish(IconBackdropTuning tuning) => Published[tuning.Source.Trim()] = tuning;

    /// <summary>撤回一个来源：模块被关掉、或它面板上的「启用」被勾掉时走这里。</summary>
    public static void Retract(string source)
    {
        if (!string.IsNullOrWhiteSpace(source)) Published.TryRemove(source.Trim(), out _);
    }

    /// <summary>调试／自检用：当前有几个来源在发布（不参与上屏判定）。</summary>
    public static int PublishedCount => Published.Count;

    /// <summary>
    /// 这一条命令该垫什么：命令自带的 <c>-icon</c> 优先；否则要有人认领这个来源
    /// （<c>-from</c> 在发布表里），拿它配置的图标，没配就按来源名去 IdeIcons 取同名图。
    /// 两条都不满足、或取到的图当剪影不合格，返回 null = 这一层什么都不画。
    /// </summary>
    public static IconBackdropStyle? Resolve(EffectCommand command)
    {
        string named = command.Icon?.Trim() ?? "";
        IconBackdropTuning? tuning = TuningFor(command.Source ?? "");
        if (named.Length == 0 && tuning is null) return null;   // 没人指定图标，也没人认领这个来源

        // 指定了就照指定的看：那张图当剪影不合格就是不画，悄悄换成别的图只会让人更摸不着头脑
        string wanted = named.Length > 0 ? named : tuning!.IconFile?.Trim() ?? "";
        var image = wanted.Length > 0 ? LoadIcon(wanted, "") : LoadIcon("", tuning!.Source);
        if (image is null) return null;

        return tuning is null
            ? new IconBackdropStyle(image, NeutralSizePercent, NeutralXPercent, NeutralYPercent, NeutralOpacityPercent)
            : new IconBackdropStyle(image, tuning.SizePercent, tuning.XPercent, tuning.YPercent, tuning.OpacityPercent);
    }

    /// <summary>按「先写的名字优先」逐个试：路径 → 面板里配的名字 → 来源同名图。</summary>
    private static ImageSource? LoadIcon(string raw, string source)
    {
        if (raw.Length > 0)
        {
            // 只写了个名字（不带目录）就当 IdeIcons 里的图名看；写了路径就照路径读
            var first = raw.Contains('/') || raw.Contains('\\') ? raw : InIconDir(raw);
            var found = Load(first);
            if (found is not null) return found;
        }
        return source.Length > 0 ? Load(InIconDir(source)) : null;
    }

    private static string InIconDir(string name) =>
        Path.Combine(IconDir, (Path.HasExtension(name) ? name : name + ".png").ToLowerInvariant());

    /// <summary>
    /// 剪影可用的透明度区间。两头都得拦：
    /// 全透明的图（本机 <c>dsh.png</c> 实测平均 alpha = 0）当遮罩等于什么都不画；
    /// 几乎全不透明的图（<c>xassistant.png</c> 覆盖 1.00、<c>trae.png</c> 0.95）当遮罩就是一块实心方砖——
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
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            return null;   // 读不出 alpha 就当不能用：宁可少一张立绘，不在屏上摆一块方砖
        }
    }

    /// <summary>读图。OnLoad 是为了不把文件锁住——用户随时可以换掉那张图。</summary>
    private static ImageSource? Load(string pathOrName)
    {
        try
        {
            string full = Path.IsPathRooted(pathOrName)
                ? pathOrName
                : Path.Combine(AppContext.BaseDirectory, pathOrName);
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
