using System;
using System.Collections.Generic;

namespace XAssistant.Services;

/// <summary>一颗流光球：屏幕分数坐标 + 半径（屏幕短边分数）+ 色相 + 漂移相位。</summary>
public readonly record struct AuroraBlob(double Hue, double X, double Y, double Radius, double Phase);

/// <summary>
/// 「流光溢彩」那层的几何与配色（<b>纯函数</b>，不碰窗口，所以能在离屏渲染工程里量）。
///
/// 两条硬规矩是用户定的：
/// 1. <b>相邻的球必须色相靠近</b> —— 沿屏幕一圈走回来，色相用三角波铺到端点再折回，
///    于是相邻两球只差 <see cref="HueStep"/>，而且环的接头处两侧同色、不跳色。
///    排满 360° 也能做到相邻近，但那是一圈彩虹灯带；收在有限跨度里才像"五彩斑斓"而不是 RGB 跑马灯。
/// 2. <b>屏幕中间必须透明</b> —— 球只许待在边缘环带上（球心到屏幕中心的距离必须大于
///    <see cref="KeepOutRadius"/>），外面再套一层径向遮罩：安全区内 alpha=0，向外爬到 1。
///    警告正文压在中线上，那一片不能有任何颜色糊上去。
/// </summary>
public static class AuroraField
{
    /// <summary>中心安全区（屏幕分数，宽高各占多少）：球心不许落进来，遮罩也在这圈里保持全透。</summary>
    public const double KeepOutX = 0.52, KeepOutY = 0.46;

    /// <summary>相邻两球允许的最大色相差（度数）：判据用。</summary>
    public const double MaxNeighbourHueStep = 34;

    /// <summary>跨度与默认起始色相（起始≈青蓝，跨度 150° → 青蓝→紫→洋红这一带）。</summary>
    public const double DefaultSpanDegrees = 150, DefaultBaseHue = 195;

    /// <summary>
    /// 默认球数：16:9 屏上环的周长约 6000 px，11 颗（半径 280-390 px）球心间隔 ~545 px 会漏缝 ——
    /// 离屏量到环上第 10 百分位只有 37/255；给到 16 颗才能连成一整圈。
    /// 顺带：跨度 150° 分 16 颗，相邻只差 20°，"相邻色相靠近"更稳。
    /// </summary>
    public const int DefaultBlobCount = 16;

    /// <summary>沿屏幕一圈色相用三角波铺出去再折回来：相邻差 = 2×跨度 ÷ (球数-1)。</summary>
    public static double HueStep(int count, double spanDegrees) =>
        2 * spanDegrees / Math.Max(1, Math.Max(3, count) - 1);

    /// <summary>
    /// 环的"方度"：球心落在超椭圆上。2 = 内切椭圆（四角一点球都没有，
    /// 离屏量出来角上 alpha = 0）；越大越贴矩形边，3.2 让四角与四边中点都吃满。
    /// </summary>
    public const double RingSquarishness = 3.2;

    /// <summary>把单位圆上的一个分量映射到超椭圆上（|u|^(2/方度)，保留符号）。</summary>
    public static double Along(double unit) =>
        Math.Sign(unit) * Math.Pow(Math.Abs(unit), 2.0 / RingSquarishness);

    /// <summary>
    /// 沿边缘排 N 颗球：球心落在超椭圆那一圈（<see cref="RingSquarishness"/>），并且**按真实弧长均分**。
    /// 两处都不肯让步：按角度均分会在四角留空（量出来角上只剩 19/255，等于四角是黑的）；
    /// 而弧长必须按像素算 —— 分数空间里 0.17 的横向步长是 330 px、纵向只有 183 px，
    /// 用分数弧长均分会把球都挤到上下边去。
    /// </summary>
    public static List<AuroraBlob> Blobs(int count, double baseHue, double spanDegrees, double aspect = DefaultAspect)
    {
        int n = Math.Max(4, count);
        var ring = RingPoints(n, aspect);
        var blobs = new List<AuroraBlob>(n);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            double triangle = 1 - Math.Abs(2 * t - 1);        // 0 → 1 → 0：铺出去再折回来
            blobs.Add(new AuroraBlob(
                Wrap(baseHue + spanDegrees * triangle),
                ring[i].X, ring[i].Y,
                0.26 + 0.05 * (i % 3),
                i * 0.7));
        }
        return blobs;
    }

    /// <summary>环上点的屏幕比例缺省值（16:9）；真机按虚拟屏实际宽高传。</summary>
    public const double DefaultAspect = 16.0 / 9;

    /// <summary>沿环按**像素弧长**均分取 N 个点（密采样超椭圆 → 累计像素弧长 → 等弧长插值）。</summary>
    public static List<(double X, double Y)> RingPoints(int count, double aspect = DefaultAspect)
    {
        const int samples = 720;
        double w = Math.Max(0.5, aspect), h = 1.0;            // 只看比例：1 像素的 x/y 权重
        var xs = new double[samples + 1];
        var ys = new double[samples + 1];
        var cum = new double[samples + 1];
        for (int s = 0; s <= samples; s++)
        {
            double angle = Math.PI * 2 * s / samples - Math.PI / 2;
            xs[s] = 0.5 + 0.5 * Along(Math.Cos(angle));
            ys[s] = 0.5 + 0.5 * Along(Math.Sin(angle));
            if (s > 0)
                cum[s] = cum[s - 1] + Math.Sqrt(Math.Pow((xs[s] - xs[s - 1]) * w, 2) + Math.Pow((ys[s] - ys[s - 1]) * h, 2));
        }
        double total = cum[samples];
        var points = new List<(double, double)>(count);
        for (int i = 0; i < count; i++)
        {
            double want = total * i / count;                  // 不含收尾那点，免得首尾重复
            int at = 1;
            while (at < samples && cum[at] < want) at++;
            double span = cum[at] - cum[at - 1];
            double k = span <= 0 ? 0 : (want - cum[at - 1]) / span;
            points.Add((xs[at - 1] + (xs[at] - xs[at - 1]) * k, ys[at - 1] + (ys[at] - ys[at - 1]) * k));
        }
        return points;
    }


    /// <summary>
    /// t 秒时这颗球的色相：整片色环一起慢慢转（"流"就是这么来的），外加**所有球共用**的一点点呼吸。
    /// 这里刻意不给每颗球各自的色相抖动：抖动会直接叠到相邻差上（实测各摆 ±9° 时相邻差从设计的 30°
    /// 涨到 35.8°，正好越过"相邻必须色相靠近"的上限）。要活起来交给位置与亮度，色相只随整体转。
    /// </summary>
    public static double HueAt(AuroraBlob blob, double seconds, double degreesPerSecond = 6) =>
        Wrap(blob.Hue + seconds * degreesPerSecond + Math.Sin(seconds * 0.35) * 3);

    /// <summary>t 秒时这颗球的球心：沿自己那根半径来回呼吸几个百分点，不钉死。</summary>
    public static (double X, double Y) CenterAt(AuroraBlob blob, double seconds)
    {
        double angle = Math.Atan2(blob.Y - 0.5, blob.X - 0.5);
        double radius = 1 + Math.Sin(seconds * 0.45 + blob.Phase) * 0.045;
        return (0.5 + 0.5 * Math.Cos(angle) * radius, 0.5 + 0.5 * Math.Sin(angle) * radius);
    }

    /// <summary>
    /// 中心遮罩的色标（径向比例, alpha）：0 = 屏幕中心，1 = 屏幕角落。
    /// 安全区那一圈以内必须全 0；椭圆遮罩比矩形安全区"瘦"，所以内缘再收一点才真盖得住正文。
    /// </summary>
    public static List<(double Offset, double Alpha)> MaskStops()
    {
        double inner = 0.62;   // 内缘起步：正文限宽屏宽八成（文字会伸到径向 0.8），起步太早等于把颜色糊在字底下
        return new List<(double, double)>
        {
            (0, 0), (inner, 0), (0.78, 0.22), (0.92, 0.72), (1, 1),
        };
    }

    /// <summary>正文带（中心 50% 宽 × 30% 高）里允许的最大 alpha（0-255）：超了就是颜色糊到字底下了。</summary>
    public const byte TextBandMaxAlpha = 26;

    /// <summary>
    /// 沿屏幕一圈采样（**到最近那条边的距离** ≤ 6% 的那条框带；不能用椭圆半径划带 —— 矩形的角
    /// 在椭圆半径 1.41 处，按半径划根本框不到角）的 alpha 第 10 百分位下限：
    /// 判的是"一整圈都在发光"，不是"某个点上很亮"——球之间留缝的话这条就红。
    /// </summary>
    public const byte RingP10MinAlpha = 55;

    /// <summary>四个角那一块（横向与纵向都离中心 &gt; 0.75）的同一判据：允许比边中点暗一档。</summary>
    public const byte CornerP10MinAlpha = 40;

    /// <summary>球心到屏幕中心的距离（椭圆归一化，1 = 屏幕边）：判据用它证明没有球压在中线上。</summary>
    public static double DistanceToCentre(AuroraBlob blob) =>
        Math.Sqrt(Math.Pow((blob.X - 0.5) / 0.5, 2) + Math.Pow((blob.Y - 0.5) / 0.5, 2));

    /// <summary>安全区半轴对应的归一化半径：球心距离必须大于它。</summary>
    public static double KeepOutRadius() => Math.Sqrt(
        Math.Pow(KeepOutX / 2 / 0.5, 2) + Math.Pow(KeepOutY / 2 / 0.5, 2));

    /// <summary>色相取模到 0-360。</summary>
    public static double Wrap(double hue) => (hue % 360 + 360) % 360;

    /// <summary>两个色相之间的最短夹角（0-180）：判"相邻"必须用这个，直接相减会在 350↔10 那种地方骗人。</summary>
    public static double HueGap(double a, double b)
    {
        double d = Math.Abs(Wrap(a) - Wrap(b)) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>HSL（色相 0-360、饱和度与亮度 0-1）→ 0-1 的 RGB。WPF 没有这个换算，自己给一份。</summary>
    public static (double R, double G, double B) ToRgb(double hue, double saturation, double lightness)
    {
        double h = Wrap(hue) / 60.0;
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double m = lightness - chroma / 2;
        double x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
