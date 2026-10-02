using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using XAssistant.Services;
using XAssistant.Services.Keywords;
using WinForms = System.Windows.Forms;

// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Image / Brush / Brushes / Color / Point / FontFamily / Pen 会跟 WPF 的撞名
using Image = System.Windows.Controls.Image;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FontFamily = System.Windows.Media.FontFamily;
using Rectangle = System.Windows.Shapes.Rectangle;
using Binding = System.Windows.Data.Binding;

namespace XAssistant.Views;

/// <summary>
/// 关键词彩蛋的效果层：一块铺满虚拟屏幕的透明窗口，管两件事。
///
/// 1) 粒子：顶部浮岛（toast）下方一次只给一颗，方向来自下半圆 180° 扇面（从正右经正下到正左），
///    沿自己的随机矢量直行（没有重力，轨迹不会往下弯），
///    转着越飞越慢、停在半空，然后原地缩小消失。每颗一份 <see cref="ParticleState"/>，
///    渲染帧上各算一次 <see cref="ParticleMotion.Step"/>。刻意不做"一次炸一把"：几十颗同屏既费渲染又像撒沙子。
/// 2) 警告语 + 边缘高亮：触发时屏幕中间一句大字（两侧斜线夹着，不垫背景框），同时屏幕四边亮一圈；
///    两者共用同一段关键帧，算一个效果。只占一行与四边，后景照常可见。
///
/// 三条硬约束：不抢焦点（ShowActivated=False）、点击穿透（WS_EX_TRANSPARENT）、
/// 没事就关窗（粒子收完、警告带淡完就 Close，不留一块全屏透明窗在上面）。
/// </summary>
public sealed partial class EffectsWindow : Window
{
    /// <summary>同屏粒子数上限。连击关键词时宁可少而清楚，不要多而糊。</summary>
    private const int MaxParticles = 12;

    /// <summary>默认淡入秒数（用户定的节奏：淡入 1 s → 持续 5 s → 淡出 1 s）。</summary>
    public const double BannerFadeInSeconds = 0.5;

    /// <summary>默认持续秒数：闪烁铺在这一段里，不写时长时就是这一段撑住可读。</summary>
    public const double BannerHoldSeconds = 8.0;

    /// <summary>默认淡出秒数。</summary>
    public const double BannerFadeOutSeconds = 0.5;

    /// <summary>
    /// 扫描头单趟的时长（用户定的节奏：一条动画 0.5 s）。淡入比它短时以淡入为准，
    /// 免得「扫入」跑到淡入前面去——那时条带还没亮完，扫的东西看不见。
    /// </summary>
    public const double SweepSeconds = 0.5;

    /// <summary>警戒线慢移一格（一个瓦片）要几秒：驻留段里循环，文本不动，所以只有线在走。</summary>
    private const double DriftSecondsPerTile = 2.4;

    /// <summary>退场时整条带子往左带的距离（DIP）：给「平移出去」一点位移感，又不至于把字推走。</summary>
    private const double ExitDrift = 80;

    /// <summary>扫描头的宽度（与 XAML 里那个 Border 一致）：平移量的两端都按它算。</summary>
    private const double SweepWidth = 150;

    /// <summary>
    /// 上下给边框带留的让位量（DIP）：正文叠到这个高度以内就不再往下长，免得压在渐隐带上。
    /// 带子的实际跨度是 带宽 + 淡出长，取不到规格时用一档常见的值顶着。
    /// </summary>
    private const double FallbackEdgeSpan = 96;

    /// <summary>行间距（DIP）：斜线有高度，挤在一起会看成一片糊的。</summary>
    private const double RowGap = 12;

    /// <summary>正文限宽：屏宽的 80%。剩下的两成留给左右两道斜线，读起来也才知道字到哪里为止。</summary>
    private const double TextWidthRatio = 0.8;

    /// <summary>
    /// 组层点亮的时长：必须明显短于扫描的 0.5 s。整层跟着淡入的话，扫到左半边时那里还半明半暗，
    /// 「扫到哪亮到哪」就被这层淡入糊成「整条一起慢慢亮起来」——观感与门禁都读不到那道擦边。
    /// </summary>
    private const double GroupOnSeconds = 0.12;

    /// <summary>
    /// 边框改级的过渡时长：与一条消息的擦边同量级（0.5 s）。屏上多出一条紧急的、或最后一条紧急的
    /// 下屏了，全屏那圈带子就在这半秒里自己挪到新颜色，而不是硬盖一层。
    /// </summary>
    private const double BorderMorphSeconds = 0.5;

    /// <summary>粒子的布局盒边长与字形大小的比例：留够余量，转起来不会被盒子裁掉。</summary>
    private const double BoxRatio = 1.8;

    private static EffectsWindow? _shared;
    private readonly List<Particle> _live = new();
    private TimeSpan? _lastFrame;
    private bool _loopAttached;
    private bool _bannerOn;
    private Storyboard? _breathStory;
    private readonly List<LinearGradientBrush> _bands = new();

    /// <summary>
    /// 一条条带的完整规格：<see cref="ShowBanner"/>（旧调用）与 <see cref="ShowCommand"/>（xa 命令行）
    /// 都先换算成这个再进 <see cref="Banner"/>。颜色在这里已经是画刷（换刷要在 UI 线程，
    /// 换算那步就在 Dispatcher.Invoke 里，不跨线程留资源）。
    /// </summary>
    private readonly record struct BannerSpec(
        string? Text, Brush Color, double Hold, double FadeIn, double FadeOut, int Blinks,
        bool BorderOn, double BorderWidth, double BorderFade, double BorderCycle, double FontSize,
        bool Urgent, string Key, IconBackdropStyle? Backdrop, string? Glyph, string? IconPath,
        bool Aurora, int AuroraCount)
    {
        /// <summary>这一行占屏多久：与 <see cref="EffectCommand.ScreenSeconds"/> 同一口径。</summary>
        public double ScreenSeconds => FadeIn + Hold + FadeOut;
    }

    /// <summary>边框呼吸的暗端（亮端是 1）：只收 45%，看着是「亮暗之间循环」，不是「闪灭」。</summary>
    private const double BreathLow = 0.55;

    /// <summary>
    /// 一颗粒子。数值状态交给 <see cref="ParticleMotion"/>，这里只挂视觉。
    ///
    /// 位置走**布局**（Canvas.Left/Top），缩放与旋转走 **RenderTransform**，两者不叠进同一个矩阵：
    /// 布局给的是盒子左上角，而状态里存的是中心，所以每帧要减掉半边长（Half）——这就是"坐标留在原地"
    /// 的那笔补偿。要是像上一版那样把平移也塞进变换组，收尾缩放到 0 时整个组合矩阵会把粒子拽回原点，
    /// 看着就是"突然跳回左上角再原地消失"。
    /// </summary>
    private sealed class Particle
    {
        public required FrameworkElement Body { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required RotateTransform Spin { get; init; }
        public double Half;
        public ParticleState State;
    }

    /// <summary>当前还在飞的粒子数。设置区与离屏夹具都读它。</summary>
    public static int LiveCount => _shared?._live.Count ?? 0;

    private EffectsWindow()
    {
        InitializeComponent();
        // 虚拟屏幕：多显示器时粒子与警告带能跟着跨过去，而不是只在下半块主屏里飘
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Closed += (_, _) =>
        {
            DetachLoop();
            if (ReferenceEquals(_shared, this)) _shared = null;
        };
    }

    // ===== 对外动作 =====
    
    /// <summary>
    /// 从 origin（屏幕 DIP，浮岛下沿就是这一系）朝下 180° 扇面里给一颗粒子。
    /// WPF 不支持彩色 emoji 字体，字形是单色轮廓；tint 留空则跟着当前强调色。
    /// </summary>
    public static void Emit(Point origin, string glyph, string? imagePath, Brush? tint = null)
    {
        var app = System.Windows.Application.Current;
        if (app is null || (glyph.Length == 0 && string.IsNullOrEmpty(imagePath))) return;
        // 钩子回调不一定在 UI 线程，窗口只能在 UI 线程碰
        app.Dispatcher.Invoke(() =>
        {
            var source = LoadImage(imagePath);
            if (glyph.Length == 0 && source is null) return;
            _shared ??= new EffectsWindow();
            if (!_shared.IsVisible) _shared.Show();
            _shared.Spawn(origin, glyph, source, tint);
        });
    }
    
    /// <summary>
    /// 屏幕中间横一句警告语：左右两道斜线铺满屏宽，同时四边亮一圈。
    /// color 留空用当前强调色；seconds 是总时长（默认 = 淡入 1 s + 持续 5 s + 淡出 1 s），blinks 是闪几下。
    /// 保持字面签名不动：老的调用点（测试夹具、外部脚本）直接调这个。
    /// <b>关键词彩蛋已经不走这里</b>——它走 <c>EffectQueue</c> 排队（见 KeywordWatcher.KeywordCommand）。
    /// </summary>
    public static void ShowBanner(string text, Brush? color = null, double seconds = SlashParser.DefaultSeconds, int blinks = 1)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            _shared ??= new EffectsWindow();
            if (!_shared.IsVisible) _shared.Show();
            // 旧调用没有新参数：边框按默认宽度静态亮着（cycle 0 = 不循环），首尾按旧规则各收缩到三分之一
            double span = Math.Max(0.6, seconds);
            double fadeIn = Math.Min(BannerFadeInSeconds, span / 3);
            double fadeOut = Math.Min(BannerFadeOutSeconds, span / 3);
            _shared.Banner(new BannerSpec(
                Text: text,
                Color: color ?? _shared.Accent(),
                Hold: Math.Max(0.1, span - fadeIn - fadeOut),
                FadeIn: fadeIn,
                FadeOut: fadeOut,
                Blinks: blinks,
                BorderOn: true,
                BorderWidth: 60,
                BorderFade: 36,
                BorderCycle: 0,
                FontSize: EffectCommand.DefaultFontSize,
                Urgent: false,
                Backdrop: null,
                // 彩蛋自己会甩粒子（KeywordWatcher 直接调 Emit），这条路上不再来一颗
                Glyph: null, IconPath: null,
                Aurora: false, AuroraCount: AuroraField.DefaultBlobCount,
                // 彩蛋按「这句词」成组：同一个词连敲是重新计时，不同词各开一叠，不互相叠成两行
                Key: "keyword:" + text));
        });
    }

    /// <summary>
    /// 一条解析好的效果指令（cmd / MCP 都走这里）：文字、颜色、三段节奏、边框带宽与呼吸循环
    /// 全部按参数来。文字为空又不要边框的指令直接不开窗——省得留一块全屏透明窗在上面。
    /// </summary>
    public static void ShowCommand(EffectCommand command)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        if (command.Hide) { HideBanner(); return; }
        if (command.Text is null && !command.BorderOn) return;
        app.Dispatcher.Invoke(() =>
        {
            _shared ??= new EffectsWindow();
            if (!_shared.IsVisible) _shared.Show();
            _shared.Banner(new BannerSpec(
                Text: command.Text,
                Color: EffectCommand.BrushOf(command.Color) ?? _shared.Accent(),
                Hold: command.Hold,
                FadeIn: command.FadeIn,
                FadeOut: command.FadeOut,
                Blinks: command.Blinks,
                BorderOn: command.BorderOn,
                BorderWidth: command.BorderWidth,
                BorderFade: command.BorderFade,
                BorderCycle: command.BorderCycle,
                FontSize: command.FontSize,
                Urgent: command.Urgent,
                // 没有归组键的裸消息各成一组（= 后来的把前一叠换掉），与调度器「不成组就排队」同一口径；
                // 同一句裸话重发仍然并到同一行，不会在屏上叠出两行一模一样的
                Key: command.GroupKey ?? "solo:" + command.Text,
                // 立绘按来源查发布表：没有模块认领这个来源、又没写 -icon，就是 null = 这一层什么都不画
                Backdrop: IconBackdrop.Resolve(command),
                // 上屏同时甩一颗粒子：图是这台 IDE 的图标，角标是这条消息的类型（询问 ❓ / 完成 ✔ / 错误 ❌）
                Glyph: Notice.GlyphOf(command), IconPath: IconBackdrop.BadgePathFor(command),
                Aurora: command.Aurora, AuroraCount: command.AuroraBlobs));
        });
    }
    
    /// <summary>立刻收起当前那句警告语与四边高亮（斜杠命令再敲一个 / 就是干这个）。</summary>
    public static void HideBanner()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() => _shared?.HideNow());
    }
    
    /// <summary>
    /// 撒花：从顶部浮岛下方一把撒开多色纸屑，给外部（无头命令、MCP）当「做完了」的庆祝。
    /// 一次只给这么多，同屏上限仍然由 MaxParticles 兜着。
    /// </summary>
    public static void Confetti(int count = 10)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            _shared ??= new EffectsWindow();
            if (!_shared.IsVisible) _shared.Show();
            var palette = _shared.ThemePalette();
            string[] glyphs = ["\u2726", "\u2740", "\u2741", "\u25C6", "\u2733"];   // ✦  ❁ ◆ 
            for (int i = 0; i < Math.Clamp(count, 1, MaxParticles); i++)
                _shared.Spawn(ToastWindow.Anchor, glyphs[i % glyphs.Length], null, palette[i % palette.Length]);
        });
    }
    
    // ===== 粒子：一次一颗，逐帧各算一次 =====
    
    private void Spawn(Point screenDip, string glyph, ImageSource? source, Brush? tint)
    {
        if (_live.Count >= MaxParticles) return;
        // 调用方给的是屏幕 DIP，画布原点在虚拟屏左上角：进来先减掉这一段
        var center = new Point(screenDip.X - SystemParameters.VirtualScreenLeft, screenDip.Y - SystemParameters.VirtualScreenTop);
    
        // 下半圆 180° 扇面里随机一个方向（正右 ↔ 正左，正下为中心）：粒子从浮岛像洒下来，不是窄锥
        double direction = ParticleMotion.ScatterDirection(Random.Shared.NextDouble());
        // 阻力很轻（ParticleMotion.Drag）：初速要够高才能飘到一千像素开外，再低就成「飘一下就停了」
        double speed = 800 + Random.Shared.NextDouble() * 500;
        double size = 26 + Random.Shared.NextDouble() * 14;
        FrameworkElement inner = source is null
            ? new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = size,
                Foreground = tint ?? TryFindResource("AccentBrush") as Brush ?? Brushes.Gainsboro,
            }
            : BadgeChip(glyph, source, size, tint);

        // 固定尺寸的盒子：缩放只作用在盒子上，盒子本身的位置由布局钉住，缩到 0 也还在原地
        double box = size * BoxRatio;
        inner.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        inner.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        var body = new Grid { Width = box, Height = box, Children = { inner } };
        var scale = new ScaleTransform(1, 1);
        var spin = new RotateTransform();
        body.RenderTransformOrigin = new Point(0.5, 0.5);
        body.RenderTransform = new TransformGroup { Children = { scale, spin } };
        Canvas.SetLeft(body, center.X - box / 2);
        Canvas.SetTop(body, center.Y - box / 2);
        Stage.Children.Add(body);

        _live.Add(new Particle
        {
            Body = body,
            Scale = scale,
            Spin = spin,
            Half = box / 2,
            State = new ParticleState(
                X: center.X, Y: center.Y,
                Vx: Math.Cos(direction) * speed,
                Vy: Math.Sin(direction) * speed,
                Angle: 0,
                SpinRate: (Random.Shared.Next(0, 2) == 0 ? 1 : -1) * (90 + Random.Shared.NextDouble() * 120),
                Age: 0,
                Life: 3.0 + Random.Shared.NextDouble() * 0.8),
        });
        AttachLoop();
    }

    /// <summary>
    /// 一颗粒子的一枚「来料标签」：正脸是发消息那台 IDE 的图标，右下角贴这条消息的类型角标。
    /// 角标用这条消息自己的颜色染（红档❌、黄档❓、完成✔），所以一眼看得懂又不用另配色板。
    /// </summary>
    private FrameworkElement BadgeChip(string glyph, ImageSource source, double size, Brush? tint)
    {
        var chip = new Grid();
        chip.Children.Add(new Image { Source = source, Width = size, Height = size });
        if (glyph.Length > 0)
            chip.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = size * 0.55,
                Foreground = tint ?? TryFindResource("AccentBrush") as Brush ?? Brushes.Gainsboro,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            });
        return chip;
    }

    // ===== 流光溢彩（-aurora on）：边缘一圈彩色球 + 中心淡出遮罩 =====

    /// <summary>一颗流光球：几何定义 + 挂在画布上的图元 + 那份刻意不 Freeze 的画刷（每帧改色）。</summary>
    private sealed class AuroraBall
    {
        public required AuroraBlob Blob { get; init; }
        public required System.Windows.Shapes.Ellipse Body { get; init; }
        public required RadialGradientBrush Brush { get; init; }
        public required double Radius { get; init; }
    }

    /// <summary>
    /// 按这一组重新铺一圈球（换组时才重建：球数与屏幕比例变了才需要重来）。
    /// 画刷不能 Freeze —— 每帧要改色标颜色，冻住就写不进去。
    /// </summary>
    private void BuildAurora(BannerSpec spec)
    {
        AuroraBlobs.Children.Clear();
        _aurora.Clear();
        if (!spec.Aurora || Width <= 0 || Height <= 0) return;
        double shortSide = Math.Min(Width, Height);
        foreach (AuroraBlob blob in AuroraField.Blobs(spec.AuroraCount,
                     AuroraField.DefaultBaseHue, AuroraField.DefaultSpanDegrees, Width / Height))
        {
            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(165, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            double side = blob.Radius * shortSide * 2;
            var body = new System.Windows.Shapes.Ellipse { Width = side, Height = side, Fill = brush };
            AuroraBlobs.Children.Add(body);
            _aurora.Add(new AuroraBall { Blob = blob, Body = body, Brush = brush, Radius = blob.Radius * shortSide });
        }
        Aurora.OpacityMask = AuroraMask();
    }

    /// <summary>每帧推进：整圈色相慢慢转，球沿自己那根半径呼吸。位置走布局（Canvas.Left/Top）。</summary>
    private void StepAurora(double seconds)
    {
        if (_aurora.Count == 0) return;
        // 流光一秒只算这么多步：每步要改 16 颗球的色停与位置，按显示刷新率（120 Hz 大屏上）
        // 全量算一遍纯属浪费——它本来就是一层缓慢的柔光，20 Hz 肉眼分不出，
        // 而每帧改渐变颜色会让这一层每帧重新光栅化（实测占掉约 1/4 个核心）。
        if (seconds - _auroraClock < 0.05) return;
        _auroraClock = seconds;
        foreach (AuroraBall ball in _aurora)
        {
            var (r, g, b) = AuroraField.ToRgb(AuroraField.HueAt(ball.Blob, seconds), 0.85, 0.58);
            byte R = (byte)Math.Round(r * 255), G = (byte)Math.Round(g * 255), B = (byte)Math.Round(b * 255);
            ball.Brush.GradientStops[0].Color = Color.FromArgb(165, R, G, B);
            ball.Brush.GradientStops[1].Color = Color.FromArgb(90, R, G, B);
            ball.Brush.GradientStops[2].Color = Color.FromArgb(0, R, G, B);
            var (cx, cy) = AuroraField.CenterAt(ball.Blob, seconds);
            Canvas.SetLeft(ball.Body, cx * Width - ball.Radius);
            Canvas.SetTop(ball.Body, cy * Height - ball.Radius);
        }
    }

    /// <summary>
    /// 中心淡出遮罩：径向按 <see cref="AuroraField.MaskStops"/> 从全透爬到全实。
    /// 用 RelativeToBoundingBox（半径 0.5 = 各自半轴），于是椭圆天然按屏幕比例拉伸，
    /// 换显示器、插副屏都不用重算 —— 绝对模式下那套圆心/半径得跟着 Width/Height 走，多一份出错的机会。
    /// </summary>
    private static Brush AuroraMask()
    {
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        foreach ((double offset, double alpha) in AuroraField.MaskStops())
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255), offset));
        return brush;
    }

    /// <summary>撒花用的主题色盘：取当前生效的几支画刷，所以换肤后撒出来的颜色也跟着变。</summary>
    private Brush[] ThemePalette()
        => new[] { "AccentBrush", "DangerBrush", "SuccessBrush", "LinkBrush", "HighlightNumber" }
            .Select(key => TryFindResource(key) as Brush)
            .Where(brush => brush is not null)
            .Select(brush => brush!)
            .DefaultIfEmpty(Brushes.Gainsboro)
            .ToArray();

    /// <summary>渲染帧：把每一颗粒子各算一次。同一帧共用一个 dt，才不会有的快有的慢。</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        // 掉帧、切窗口、断点回来时空档可能几百毫秒，按 50 ms 封顶：衰减走闭式解，跨步也不会算飞
        double dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 0;
        _lastFrame = time;
        // 整组到点由渲染帧来触发退场（不另开一个定时器）：这一帧一帧地走，本来就是判到点的地方，
        // 多一个 DispatcherTimer 就多一条「谁先清状态」的竞路线
        var clock = Mono();
        // 逐行判到点：谁到了自己擦出的时刻谁开始走，剩下那几条继续摆着。
        // 擦出**住在这一行的预算里**（Until 就是它彻底消失的时刻），所以调度器交出屏幕的这一刻
        // 正好是最后一条擦完的一刻——下一条不会把正在擦的上一行抹掉，边框也不会抢在正文走完前先收。
        foreach (var row in _rows.Where(item => !item.Exiting && clock >= ExitAt(item)).ToArray()) ExitRow(row);
        // 流光自己也要在这一帧上走一步：它的时钟就是渲染时钟，不开第二个计时器（多一条计时器就多一条竞路线）
        if (_aurora.Count > 0 && Aurora.Visibility == Visibility.Visible) StepAurora(clock.TotalSeconds);
        if (_borderOnly && _borderUntil <= clock)
        {
            _borderOnly = false;
            _bannerOn = _rows.Count > 0;
            RefreshBorder(clock, fresh: false);
        }
        if (dt <= 0) return;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var particle = _live[i];
            particle.State = ParticleMotion.Step(particle.State, dt);
            var s = particle.State;
            // 中心 → 左上角：布局只认左上角，这一笔减掉的就是"坐标留在原地"的补偿
            Canvas.SetLeft(particle.Body, s.X - particle.Half);
            Canvas.SetTop(particle.Body, s.Y - particle.Half);
            particle.Spin.Angle = s.Angle;
            double shrink = s.Life - s.Age < ParticleMotion.ShrinkTail ? ParticleMotion.Scale(s) : 1;
            particle.Scale.ScaleX = particle.Scale.ScaleY = shrink;
            particle.Body.Opacity = ParticleMotion.Opacity(s);
            if (ParticleMotion.Expired(s))
            {
                Stage.Children.Remove(particle.Body);
                _live.RemoveAt(i);
            }
        }
        if (_live.Count == 0 && !_bannerOn) Close();
    }
    // ===== 一叠警告：同组竖排、逐条扫入、整组一起退 =====

    /// <summary>
    /// 屏上的一行：斜线 + 正文 + 斜线挂在被揭示的 Clip 里，扫描头是不被裁的兄弟节点。
    /// 每行一份遮罩、一份入场动画，所以「逐条 0.5 s 入场」就是各行的入场故事按到达顺序错开挂上。
    ///
    /// 平移走 RenderTransform、揭示走 Clip，两者不叠进同一个矩阵：布局给的是盒子左上角，
    /// 遮罩要的是「整屏宽的一块」，混在一个变换里做收尾缩放就会把整行拽回原点（上一版踩过）。
    /// </summary>
    private sealed class BannerRow
    {
        public required Grid Root { get; init; }
        public required Grid Content { get; init; }
        public required RectangleGeometry Mask { get; init; }
        public required Border SlashLeft { get; init; }
        public required Border SlashRight { get; init; }
        public required Border Head { get; init; }
        public required TranslateTransform HeadShift { get; init; }
        public required TranslateTransform Shift { get; init; }
        public required TextBlock Body { get; init; }
        public required string Text { get; init; }
        public required BannerSpec Spec { get; set; }
        public TimeSpan Until { get; set; }
        /// <summary>入场擦边真正走完的时刻（错峰入场的行比打头那条晚）：擦出不许早于它。</summary>
        public TimeSpan EntryDone { get; set; }
        public bool Exiting { get; set; }
        public Storyboard? Entry { get; set; }
        public Storyboard? Exit { get; set; }
        public Storyboard? Drift { get; set; }
    }

    private readonly List<BannerRow> _rows = new();
    /// <summary>流光层的球：一份状态（球定义 + 图元 + 那份没冻住的画刷），每帧改色改位。</summary>
    private readonly List<AuroraBall> _aurora = new();
    private double _auroraClock;
    private string? _groupKey;
    private TimeSpan? _nextEntryAt;      // 下一行的入场起点：连击时按 0.5 s 一档往后排
    private bool _borderOnly;            // 只亮边框、不要正文（-lable off）
    private BannerSpec _borderSpec;
    private TimeSpan _borderUntil;
    private bool _borderUp;              // 边框此刻是亮着的吗（降级只做一次，不逐帧重放）
    private Storyboard? _groupStory;
    private Storyboard? _blinkStory;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// 「全遮」的那个矩形：宽度给 1 DIP 而不是 0——零宽矩形在 WPF 里是退化值，拿它当动画端点会被
    /// 折叠成 (0,0,0,0)，于是揭示从「左边界 0」开始长成从左往右推开，方向整个反掉。
    /// </summary>
    private static Rect HiddenRect(double width, double height) => new Rect(width, 0, 1, height);

    /// <summary>单调时钟：退场与入场错峰都按它算，不用墙上时间（用户改系统时钟不该把警告带卡住）。</summary>
    private static TimeSpan Mono() => Clock.Elapsed;
    /// <summary>
    /// 一条命令进来，三种去向：
    /// ① 组键不同 → 旧的一叠整体让位，开新的一叠；
    /// ② 同键同正文 → 不新增行，把那一行顶到最前、只给它自己重新上表并重扫（同一句又喊一遍）；
    /// ③ 同键新正文 → 排成下面的一行，入场排在已经排上的那几行之后（每条各扫各的 0.5 s）。
    ///
    /// 每条消息都是分立的一条：自己上屏、等满自己那一段、自己下屏。这里只给**这一行**上时间表，
    /// 既不给同叠的别人续命，也不把别人提前掐掉；边框则跟着「此刻屏上还有什么」实时改级。
    /// </summary>
    private void Banner(BannerSpec spec)
    {
        var now = Mono();
        // 只要边框不要正文：清空这一叠，边框单独亮一会儿（-lable off 落在这条路上）
        if (spec.Text is not { Length: > 0 } text)
        {
            StopGroup();
            ClearRows();
            _groupKey = spec.Key;
            _borderOnly = true;
            _borderSpec = spec;
            _borderUntil = now + Span(spec);
            _bannerOn = true;
            BuildAurora(spec);
            AttachLoop();
            RefreshBorder(now, fresh: true);
            return;
        }
        _borderOnly = false;

        bool fresh = _groupKey != spec.Key;
        if (fresh)
        {
            StopGroup();
            ClearRows();
            _groupKey = spec.Key;
            _nextEntryAt = null;
            Rows.Opacity = 0;
            BuildAurora(spec);      // 换组才重铺一圈球（球数或开关跟着这一组的第一条命令）
        }
        Rows.Visibility = Visibility.Visible;

        // 正文一样就是两条消息：两场对话的「请求人类介入」正文常常一字不差，归并等于把上一条吃掉
        // （用户点名过）。要防重复得靠命令自己写 -tag 声明「这是同一条告警又喊了一遍」，那一支在调度器里。
        // 屏幕真的摆满了才动老行——摆得下几行按**量出来的行高**算（RowCapacity），不写死行数。
        if (LiveRows >= RowCapacity()) EvictOldestRow();
        var row = BuildRow(spec, text);
        _rows.Add(row);
        row.Until = now + Span(spec);
        EnterRow(row, now);

        if (Rows.Opacity < 0.99) FadeIn(Rows, GroupOnSeconds);
        if (fresh) AddBlink(spec);
        // 每条消息上屏甩一颗粒子：正脸是这台 IDE 的图标，右下角贴这条消息的类型角标（❓/✔/❌）。
        // 取不到图标就只剩角标——不摆空盒子，也不摆一颗看不见的粒子（全透明那种图直接判没有）。
        if (spec.Glyph is { Length: > 0 } mark) Spawn(ToastWindow.Anchor, mark, LoadImage(spec.IconPath), spec.Color);
        _bannerOn = true;
        RefreshBorder(now, fresh);
        AttachLoop();
    }

    /// <summary>一条消息在屏上停多久：淡入（= 擦边）+ 持续 + 淡出（= 擦出）。</summary>
    private static TimeSpan Span(BannerSpec spec) => TimeSpan.FromSeconds(Math.Max(0.1, spec.ScreenSeconds));

    /// <summary>这一行开始擦出的时刻：整段退场动画走完，刚好落在 <see cref="BannerRow.Until"/> 上。</summary>
    private static TimeSpan ExitAt(BannerRow row) => EffectTiming.ExitStart(
        row.EntryDone, row.Until, EffectTiming.ExitSeconds(row.Spec.FadeOut, SweepSeconds));

    /// <summary>还没开始退场的行数——一屏最多叠几行的上限按它算（正在擦出去的那条已经不算数了）。</summary>
    private int LiveRows => _rows.Count(row => !row.Exiting);

    /// <summary>
    /// 还在屏上占位的行（擦到一半的也算）：边框定级与四角让位的高度都按这些条走。
    /// 「正在擦出去」的那一条还看得见，所以它那一档的颜色得撑到它彻底消失，
    /// 不然就是用户说的「消息没播完 border 先关」。
    /// </summary>
    private List<BannerSpec> Occupied(TimeSpan now) =>
        _rows.Where(row => row.Until > now).Select(row => row.Spec).ToList();

    /// <summary>
    /// 边框与四角按**此刻屏上还有什么**实时定级：只剩普通消息就用普通的颜色，混进一条紧急的就升到
    /// 紧急档，最后一条紧急的下屏之后它自己得降回去。升降级走同一种过渡（渐变色标自己动 0.5 s），
    /// 不许直接盖一层新颜色——那看着就是「红的是另贴上去的一张」。
    /// </summary>
    private void RefreshBorder(TimeSpan now, bool fresh)
    {
        var live = Occupied(now);
        if (_borderOnly && _borderUntil > now && _borderSpec is { } only) live.Add(only);
        if (live.Count == 0)
        {
            if (!_borderUp) return;
            _borderUp = false;
            double outSeconds = Math.Clamp(live.Count == 0 ? BannerFadeOutSeconds : 0.5, 0.2, 3);
            FadeOut(Edge, outSeconds);
            FadeOut(Corners, outSeconds);
            FadeOut(BackdropLayer, outSeconds);
            FadeOut(Aurora, outSeconds);   // 流光跟边框同一时刻收：整屏的边缘色不能留在桌面上
            var breath = _breathStory;
            _breathStory = null;
            breath?.Stop();
            EdgePulse.BeginAnimation(OpacityProperty, null);
            EdgePulse.Opacity = 1;
            return;
        }

        var top = live.OrderByDescending(spec => spec.Urgent)
            .ThenByDescending(spec => spec.BorderWidth)
            .ThenByDescending(spec => spec.FontSize)
            .First();
        Edge.Visibility = top.BorderOn ? Visibility.Visible : Visibility.Collapsed;
        if (top.BorderOn)
        {
            MorphBands(top, fresh ? GroupOnSeconds : BorderMorphSeconds);
            AddBreath(top);
        }
        // 四角三角要让开整叠的高度：多行正文比一行高，量出来的实际高度才算数
        double band = _rows.Where(row => row.Until > now)
            .Select(row => row.Body.ActualHeight)
            .DefaultIfEmpty(top.FontSize * 1.25)
            .Max();
        BuildCorners(top, top.Color, Math.Max(band, top.FontSize * 1.25));
        Corners.Visibility = top.Urgent ? Visibility.Visible : Visibility.Collapsed;
        if (LayOutBackdrop(top)) BackdropLayer.Visibility = Visibility.Visible;
        else BackdropLayer.Visibility = Visibility.Collapsed;

        // 第一次点亮用短淡入，之后的升降级用过渡时长：同一套语言，只是没有「从无到有」那一段
        double seconds = fresh ? GroupOnSeconds : BorderMorphSeconds;
        _borderUp = true;
        if (Edge.Visibility == Visibility.Visible) FadeIn(Edge, seconds);
        if (Corners.Visibility == Visibility.Visible) FadeIn(Corners, seconds);
        if (BackdropLayer.Visibility == Visibility.Visible) FadeIn(BackdropLayer, seconds);
        if (_aurora.Count > 0)
        {
            Aurora.Visibility = Visibility.Visible;
            FadeIn(Aurora, seconds);
        }
    }

    /// <summary>
    /// 渐变带改色改宽：四支画刷的色标与四条边的长度各挂一条动画，0.5 s 内自己挪过去。
    /// 画刷与色标都不能 Freeze——冻住的 Freezable 挂不上动画，改色就会变成硬切。
    /// </summary>
    private void MorphBands(BannerSpec top, double seconds)
    {
        double span = top.BorderWidth + top.BorderFade;
        double mid = span <= 0 ? 1 : top.BorderWidth / span;
        if (_bands.Count == 0)
        {
            BuildBands(top.Color, top.BorderWidth, top.BorderFade);   // 头一次没有「上一个颜色」可过渡
            return;
        }
        var rects = new[] { EdgeTop, EdgeBottom, EdgeLeft, EdgeRight };
        for (int i = 0; i < rects.Length; i++)
        {
            var brush = _bands[i];
            Animate(brush.GradientStops[0], GradientStop.ColorProperty, WithAlpha(top.Color, 0.85), seconds);
            Animate(brush.GradientStops[1], GradientStop.ColorProperty, WithAlpha(top.Color, 0.30), seconds);
            Animate(brush.GradientStops[2], GradientStop.ColorProperty, WithAlpha(top.Color, 0), seconds);
            Animate(brush.GradientStops[1], GradientStop.OffsetProperty, mid, seconds);
            var size = i < 2 ? HeightProperty : WidthProperty;
            Animate(rects[i], size, span, seconds);
        }
        // 立绘跟着档位换色：形状不动，只把填充色挪过去
        if (Backdrop.Background is SolidColorBrush tint)
            Animate(tint, SolidColorBrush.ColorProperty, WithAlpha(top.Color, 1), seconds);
    }

    /// <summary>
    /// 给一个 Freezable / 图元挂一条到值动画。走 <c>BeginAnimation</c> 而不是 Storyboard：
    /// 色标与代码建出来的图元都不在名字作用域里，SetTarget 那条路要靠名字解析，解析不上就是静默不生效。
    /// </summary>
    private static void Animate(DependencyObject target, DependencyProperty property, object to, double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0.05, seconds));
        AnimationTimeline anim = property == GradientStop.OffsetProperty
            ? new DoubleAnimation((double)to, duration) { FillBehavior = FillBehavior.HoldEnd }
            : property == GradientStop.ColorProperty
                ? new ColorAnimation((Color)to, duration) { FillBehavior = FillBehavior.HoldEnd }
                : new DoubleAnimation((double)to, duration) { FillBehavior = FillBehavior.HoldEnd };
        if (target is UIElement element) element.BeginAnimation(property, anim);
        else ((Animatable)target).BeginAnimation(property, anim);
    }

    /// <summary>
    /// 一屏摆得下几行正文：**按量出来的行高算**，行数不写死。可用高度＝窗口高减去上下给边框带的让位量；
    /// 行距＝行的实际高度 ＋ 它自带的上下外边距（ActualHeight 不含 Margin，不补这一项会高估容量）。
    /// 一行都还没建起来时按字号估一档，第一条上屏后立刻换成实测值（换行会把行撑高，实测才作数）。
    /// </summary>
    private int RowCapacity()
    {
        double edge = _borderSpec is { } spec ? spec.BorderWidth + spec.BorderFade : FallbackEdgeSpan;
        double usable = Math.Max(120, Height - 2 * edge);
        double pitch = _rows.Count > 0
            ? _rows.Average(row => row.Root.ActualHeight + row.Root.Margin.Top + row.Root.Margin.Bottom)
            : EffectCommand.DefaultFontSize * 1.25 + 2 * RowGap;
        return Math.Max(1, (int)Math.Floor(usable / Math.Max(1, pitch)));
    }

    /// <summary>
    /// 给调度器问「这一屏摆得下几行」的入口：窗口不在（还没放过效果）＝没有"屏幕占满"这回事，还给无上限。
    /// 容量是**每次现算**的（窗口高度、当前字号、边框带宽都会变），所以对外给的是函数、不是一次性的数。
    /// </summary>
    public static int RowCapacityHint()
    {
        EffectsWindow? window = _shared;
        if (window is null || !window.IsVisible || window.ActualHeight <= 0) return int.MaxValue;
        try { return window.RowCapacity(); } catch { return int.MaxValue; }
    }

    /// <summary>行数到顶：挤掉最老那一行（打头那条不动，它代表这一叠的开头）。</summary>
    private void EvictOldestRow()
    {
        var oldest = _rows.Count > 1 ? _rows[1] : _rows[0];
        _rows.Remove(oldest);
        StopRow(oldest);
        RowStack.Children.Remove(oldest.Root);
    }

    /// <summary>建一行：三列（斜线 | 正文 | 斜线）装进被 Clip 揭示的内容层，外面套一个不被裁的扫描头。</summary>
    private BannerRow BuildRow(BannerSpec spec, string text)
    {
        // 一行的高度由**排版量出来**，不由字号猜：正文限宽 80% 屏宽，超了就自己换行，
        // 换出几行来这条消息就占多高。斜线带绑在正文的实际高度上，所以线跟着字一起长。
        double tile = spec.FontSize * 1.25;
        var body = new TextBlock
        {
            Text = text,
            FontSize = spec.FontSize,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = spec.Color,
            MaxWidth = Math.Max(240, Width * TextWidthRatio),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var left = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 22, 0),
            Background = Hatch(spec.Color, tile),
        };
        var right = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(22, 0, 0, 0),
            Background = Hatch(spec.Color, tile),
        };
        // 斜线的瓦片始终是「一行高」，带子变高时靠 TileMode 往下**重复**（延展），
        // 不拉伸、也不换字号——拉一下斜笔就糊了，加字号又变成另一种东西
        foreach (var slash in new Border[] { left, right })
            slash.SetBinding(Border.HeightProperty, new Binding(nameof(FrameworkElement.ActualHeight))
            {
                Source = body, Mode = BindingMode.OneWay,
            });
        var content = new Grid { Clip = new RectangleGeometry(HiddenRect(Width, Height)) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(body, 1);
        Grid.SetColumn(right, 2);
        content.Children.Add(left);
        content.Children.Add(body);
        content.Children.Add(right);

        var headShift = new TranslateTransform(Width, 0);
        var head = new Border
        {
            Width = SweepWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0,
            Child = BuildSweepBody(spec.Color),
            RenderTransform = headShift,
        };
        var shift = new TranslateTransform();
        var root = new Grid
        {
            Margin = new Thickness(0, RowGap, 0, RowGap),
            RenderTransform = shift,
            IsHitTestVisible = false,
        };
        root.Children.Add(content);
        root.Children.Add(head);
        RowStack.Children.Add(root);
        return new BannerRow
        {
            Root = root,
            Content = content,
            Mask = (RectangleGeometry)content.Clip,
            SlashLeft = left,
            SlashRight = right,
            Head = head,
            HeadShift = headShift,
            Shift = shift,
            Body = body,
            Text = text,
            Spec = spec,
        };
    }

    /// <summary>
    /// 一行的入场：扫描头从屏右冲到屏左（0.5 s），Clip 跟着把这一行从右往左揭示出来，
    /// 斜纹从这一刻起开始慢移。FillBehavior.HoldEnd：扫完就停在全开，等整组到点再一起退。
    /// 连击时按 <see cref="_nextEntryAt"/> 往后错开，所以是「一条播完才播下一条」而不是三条一起炸。
    ///
    /// BeginTime 是**相对这条 story 开跑那一刻的延时**，不是绝对时刻：把时钟读数直接塞进去，
    /// 第二行就会「延后三十秒才揭示」——屏上看着就是它根本没来（第一行没事，因为那时读数还接近零）。
    /// </summary>
    private void EnterRow(BannerRow row, TimeSpan now)
    {
        double sweep = Math.Clamp(row.Spec.FadeIn <= 0 ? SweepSeconds : row.Spec.FadeIn, 0.15, 1.5);
        var at = _nextEntryAt is { } pending && pending > now ? pending : now;
        _nextEntryAt = at + TimeSpan.FromSeconds(sweep);
        var delay = at - now;
        row.EntryDone = at + TimeSpan.FromSeconds(sweep);

        StopRow(row);                                   // 重扫同一行：先把上一次挂在它身上的动画摘干净
        var hidden = HiddenRect(Width, Height);
        var shown = new Rect(0, 0, Width, Height);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var story = new Storyboard { BeginTime = delay };
        var wipe = TimeSpan.FromSeconds(sweep);

        var reveal = new RectAnimationUsingKeyFrames
        {
            Duration = wipe,
            FillBehavior = FillBehavior.HoldEnd,
        };
        reveal.KeyFrames.Add(new EasingRectKeyFrame(hidden, KeyTime.FromPercent(0)));
        reveal.KeyFrames.Add(new EasingRectKeyFrame(shown, KeyTime.FromPercent(1), ease));
        Storyboard.SetTarget(reveal, row.Content);
        Storyboard.SetTargetProperty(reveal, new PropertyPath("Clip.Rect"));
        story.Children.Add(reveal);

        var run = new DoubleAnimation(Width, -SweepWidth, new Duration(wipe))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(run, row.Head);
        Storyboard.SetTargetProperty(run, new PropertyPath("RenderTransform.X"));
        story.Children.Add(run);

        var glow = new DoubleAnimationUsingKeyFrames { Duration = wipe };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.15)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.85)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(glow, row.Head);
        Storyboard.SetTargetProperty(glow, new PropertyPath(OpacityProperty));
        story.Children.Add(glow);

        row.Entry = story;
        story.Begin();
        DriftRow(row, delay);
    }

    /// <summary>
    /// 这一行的斜纹慢移：每 <see cref="DriftSecondsPerTile"/> 秒挪走一个瓦片，无限循环到整组退场。
    /// 动的是画刷的 Viewport（采样窗往左挪一格），线框与正文各自不动，所以是「条纹在背景里流」，
    /// 不是「整条带子在飘」。位移取整整一个瓦片，循环接缝看不出来。
    /// </summary>
    private void DriftRow(BannerRow row, TimeSpan delay)
    {
        var drift = new Storyboard { BeginTime = delay };
        foreach (var slash in new Border[] { row.SlashLeft, row.SlashRight })
        {
            if (slash.Background is not DrawingBrush hatch) continue;
            var tile = hatch.Viewport;
            var slide = new RectAnimation(tile, new Rect(tile.X + tile.Width, tile.Y, tile.Width, tile.Height),
                new Duration(TimeSpan.FromSeconds(DriftSecondsPerTile)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(slide, slash);
            Storyboard.SetTargetProperty(slide, new PropertyPath("Background.Viewport"));
            drift.Children.Add(slide);
        }
        row.Drift = drift;
        drift.Begin();
    }

    /// <summary>摘掉一行身上挂着的入场与慢移动画（重扫与退场前都要先做这一步，否则旧动画按住新赋值）。</summary>
    private static void StopRow(BannerRow row)
    {
        var entry = row.Entry;
        row.Entry = null;
        entry?.Stop();
        var drift = row.Drift;
        row.Drift = null;
        drift?.Stop();
    }
    /// <summary>
    /// 一行到自己的点：Clip 反向收回（左边界回到屏右，线像被抽走）、扫描头反向再跑一趟当擦除器、
    /// 正文往左带一小段——读成「平移出去」。只走它自己这一行，同叠里别的消息照自己的表继续摆着。
    /// 摘掉之后必须让边框重新定级：最后一条紧急的下屏，全屏边框就该自己降回普通的颜色。
    /// </summary>
    private void ExitRow(BannerRow row)
    {
        if (row.Exiting) return;
        row.Exiting = true;
        double fadeOut = EffectTiming.ExitSeconds(row.Spec.FadeOut, SweepSeconds);
        var shown = new Rect(0, 0, Width, Height);
        var hidden = HiddenRect(Width, Height);

        row.Mask.Rect = shown;      // 先把本地值对齐到全开：Stop 会撤掉 HoldEnd，不对齐就闪一下
        StopRow(row);
        var story = new Storyboard();

        var retract = new RectAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(fadeOut) };
        retract.KeyFrames.Add(new EasingRectKeyFrame(shown, KeyTime.FromPercent(0)));
        retract.KeyFrames.Add(new EasingRectKeyFrame(hidden, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(retract, row.Content);
        Storyboard.SetTargetProperty(retract, new PropertyPath("Clip.Rect"));
        story.Children.Add(retract);

        var wipe = new DoubleAnimation(-SweepWidth, Width, new Duration(TimeSpan.FromSeconds(fadeOut)));
        Storyboard.SetTarget(wipe, row.Head);
        Storyboard.SetTargetProperty(wipe, new PropertyPath("RenderTransform.X"));
        story.Children.Add(wipe);

        var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(fadeOut) };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.2)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.8)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(glow, row.Head);
        Storyboard.SetTargetProperty(glow, new PropertyPath(OpacityProperty));
        story.Children.Add(glow);

        var leave = new DoubleAnimation(0, -ExitDrift, new Duration(TimeSpan.FromSeconds(fadeOut)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(leave, row.Root);
        Storyboard.SetTargetProperty(leave, new PropertyPath("RenderTransform.X"));
        story.Children.Add(leave);

        story.Completed += (_, _) =>
        {
            if (!ReferenceEquals(row.Exit, story)) return;
            row.Exit = null;
            if (!_rows.Remove(row)) return;
            RowStack.Children.Remove(row.Root);
            _bannerOn = _rows.Count > 0 || _borderOnly;
            RefreshBorder(Mono(), fresh: false);
            if (_rows.Count == 0 && !_borderOnly && _live.Count == 0) Close();
        };
        row.Exit = story;
        story.Begin();
    }

    /// <summary>把某层的 Opacity 在若干秒内带到 0（边框与四角降级时用；正文那条自己会擦出去）。</summary>
    private static void FadeOut(FrameworkElement layer, double seconds)
    {
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromSeconds(Math.Max(0.05, seconds))))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        layer.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>把某层的 Opacity 在若干秒内抬到 1（组层、边框、四角都用它，FillBehavior 停在末值）。</summary>
    private static void FadeIn(FrameworkElement layer, double seconds)
    {
        var fade = new DoubleAnimation(1, new Duration(TimeSpan.FromSeconds(Math.Max(0.05, seconds))))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        Storyboard.SetTarget(fade, layer);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        var story = new Storyboard { Children = { fade } };
        story.Begin();
    }

    /// <summary>停掉整组那条退场/闪烁动画（不退场也要停：留着会把下一次赋的 Opacity 按住）。</summary>
    private void StopGroup()
    {
        var story = _groupStory;
        _groupStory = null;
        story?.Stop();
        var blink = _blinkStory;
        _blinkStory = null;
        blink?.Stop();
    }



    /// <summary>
    /// 收起。两个坑都踩到过：
    /// ① 不能拿「当前有没有在闪」当闸门——那个标志位一旦被上一句的 Completed 抢先清掉，
    ///    收起就变成空操作，条带永远留在屏上；所以这里无条件执行。
    /// ② 光 story.Stop() 不够：动画还在合成树上抢着 Opacity，本地赋的 0 会被盖回去。
    ///    要 BeginAnimation(prop, null) 把属性交还给本地值，再写 0——这套动作都在 <see cref="StopAnimation"/> 里。
    /// </summary>
    private void HideNow()
    {
        _bannerOn = false;
        StopAnimation();
        if (_live.Count == 0) Close();
    }

    /// <summary>
    /// 停掉整组退场、闪烁、边框呼吸与每行的入场/慢移动画，把被它们按住的属性交还本地并复位。
    /// 先置空引用再 Stop：Stop 会触发 Completed，旧引用留着会让收尾流程（关窗、清动画）重跑一遍。
    /// 光 story.Stop() 不够：动画还在合成树上抢着 Opacity，本地赋的 0 会被盖回去，
    /// 所以每一层都要 BeginAnimation(prop, null) 把属性交还给本地值——这套动作全在这里。
    /// </summary>
    private void StopAnimation()
    {
        StopGroup();
        var breath = _breathStory;
        _breathStory = null;
        breath?.Stop();
        ClearRows();
        _groupKey = null;
        _nextEntryAt = null;
        foreach (var layer in new FrameworkElement[] { Rows, RowsPulse, Edge, EdgePulse, Corners, BackdropLayer, Aurora })
            layer.BeginAnimation(OpacityProperty, null);
        Rows.Opacity = Edge.Opacity = Corners.Opacity = BackdropLayer.Opacity = Aurora.Opacity = 0;
        AuroraBlobs.Children.Clear();
        _aurora.Clear();
        Backdrop.Child = null;
        RowsPulse.Opacity = EdgePulse.Opacity = 1;   // 内层回到全亮：闪到暗端时被收起，下一轮开头不带旧值
        Rows.Visibility = Visibility.Visible;
    }

    /// <summary>把这一叠行全摘掉：停掉挂在行上的两套动画，再从排版里移走。</summary>
    private void ClearRows()
    {
        foreach (var row in _rows)
        {
            StopRow(row);
            var exit = row.Exit;
            row.Exit = null;
            exit?.Stop();
            RowStack.Children.Remove(row.Root);
        }
        _rows.Clear();
        _borderOnly = false;
        _borderUp = false;
        _borderUntil = default;
    }

    /// <summary>
    /// 整叠一起闪：持续段里按 <c>Blinks</c> 下几次暗坑（掉到 0.2 不熄全）。
    /// 闪在组层的内层 RowsPulse 上，外层 Rows 归淡入淡出——两条动画不能抢同一个 Opacity。
    /// 新语法固定 1 下（= 不闪），只有旧语法会配多下。
    /// </summary>
    private void AddBlink(BannerSpec spec)
    {
        int blinks = Math.Clamp(spec.Blinks, 0, 5);
        if (blinks < 2 || spec.Hold <= 0) return;
        var blink = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromSeconds(SweepSeconds),
            Duration = TimeSpan.FromSeconds(spec.Hold),
        };
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        for (int i = 0; i < blinks; i++)
        {
            blink.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromPercent((i + 0.5) / blinks * 0.9)));
            blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent((i + 1) / (double)blinks * 0.9)));
        }
        Storyboard.SetTarget(blink, RowsPulse);
        Storyboard.SetTargetProperty(blink, new PropertyPath(OpacityProperty));
        var story = new Storyboard { Children = { blink } };
        _blinkStory = story;
        story.Begin();
    }

    /// <summary>
    /// 立绘铺一次：边长按屏高百分比算，中心对到 (x%, y%)，颜色用这一组的档位色、形状用图标自己的
    /// alpha。返回 false = 这一条没有立绘（没人认领这个来源、或那张图取不到），这一层就整个空着——
    /// 这里不留任何默认形状，画不出来就是什么都不画。
    /// </summary>
    private bool LayOutBackdrop(BannerSpec spec)
    {
        if (spec.Backdrop is not { } style)
        {
            Backdrop.OpacityMask = null;
            Backdrop.Background = null;
            return false;
        }
        double side = Math.Clamp(Height * style.SizePercent / 100, 40, Math.Max(40, Height));
        Backdrop.Width = side;
        Backdrop.Height = side;
        Backdrop.Margin = new Thickness(
            Math.Clamp(Width * style.XPercent / 100 - side / 2, -side, Width),
            Math.Clamp(Height * style.YPercent / 100 - side / 2, -side, Height), 0, 0);
        Backdrop.Background = spec.Color;
        Backdrop.OpacityMask = new ImageBrush(style.Image) { Stretch = Stretch.Uniform };
        Backdrop.Opacity = Math.Clamp(style.OpacityPercent / 100, 0.02, 1);
        return true;
    }

    /// <summary>
    /// 四颗巨型警告三角：上下各两颗，分列屏宽 1/4 与 3/4 处（你那张示意图就是这个布局）。
    /// 尺寸按屏高算并夹在 140–340 DIP：再小压不住整屏，再大就顶到正文。
    /// 每一颗都得单独建一份——一个 Visual 不能同时挂在两个父级下。
    /// </summary>
    private void BuildCorners(BannerSpec spec, Brush accent, double bandHeight)
    {
        Corners.Children.Clear();
        if (!spec.Urgent || spec.Text is not { Length: > 0 }) return;
        double size = Math.Clamp(Height * 0.2, 140, 340);
        double centerY = Height / 2;
        double edge = bandHeight / 2 + 12;
        for (int i = 0; i < 4; i++)
        {
            double x = Width * (i % 2 == 0 ? 0.25 : 0.75) - size / 2;
            double y = i < 2 ? centerY - edge - size - 12 : centerY + edge + 12;
            y = Math.Clamp(y, 8, Math.Max(8, Height - size - 8));
            var glyph = WarningGlyph.Build(accent, size);
            Canvas.SetLeft(glyph, x);
            Canvas.SetTop(glyph, y);
            Corners.Children.Add(glyph);
        }
    }

    /// <summary>默认颜色：跟着主题强调色走，换肤后立即生效；资源缺失时退回中性灰。</summary>
    private Brush Accent() => TryFindResource("AccentBrush") as Brush ?? Brushes.Gainsboro;

    /// <summary>
    /// 斜线画刷：一道 45° 斜笔平铺成瓦片，铺满整列——只贴着文字写四个斜杠不叫警告。
    /// 瓦片高就是文字行高，所以斜线与字等高；笔色用当前那支强调色，换肤后跟着变。
    /// </summary>
    private static DrawingBrush Hatch(Brush pen, double height)
    {
        double tile = height * 0.9;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, tile, height))));
        // 两端各伸出瓦片一半：斜笔才能完整跨过瓦片边界，平铺出来看不出接缝
        group.Children.Add(new GeometryDrawing(null, new Pen(pen, Math.Max(2, height / 6)),
            new LineGeometry(new Point(-tile, height * 2), new Point(tile * 2, -height))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, tile, height),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }

    /// <summary>
    /// 扫描头：一层渐隐底 + 一格密斜纹 + 左缘一道亮线（行进方向的那一头）。斜纹直接复用
    /// <see cref="Hatch"/>、只把瓦片缩小，所以它撞开的东西跟它本身同源，看着才像「同一套警戒带被拉了出来」。
    /// </summary>
    private static FrameworkElement BuildSweepBody(Brush accent) => new Grid
    {
        Children =
        {
            new Rectangle { Fill = Trail(accent) },
            new Rectangle { Fill = Hatch(accent, 22), Opacity = 0.9 },
            new Rectangle { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Fill = Solid(accent, 0.95) },
        },
    };

    /// <summary>扫描头的底：左缘亮、往右散到全透明——它在往左跑，拖在后面的那截才该淡。</summary>
    private static LinearGradientBrush Trail(Brush accent)
    {
        var trail = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        trail.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.55), 0));
        trail.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.12), 0.6));
        trail.GradientStops.Add(new GradientStop(WithAlpha(accent, 0), 1));
        trail.Freeze();
        return trail;
    }

    private static SolidColorBrush Solid(Brush accent, double alpha)
    {
        var solid = new SolidColorBrush(WithAlpha(accent, alpha));
        solid.Freeze();
        return solid;
    }

    /// <summary>
    /// 边框亮度呼吸：持续段里每 spec.BorderCycle 秒一圈，在内层 EdgePulse 的 Opacity 上于 1 与
    /// <see cref="BreathLow"/> 之间循环——外层 Edge 已被主戏的淡入淡出占着，两条动画不能抢同一个属性。
    /// 圈数只取持续段放得下的整圈：零头让边框亮着不动，收尾交给淡出。
    /// </summary>
    private void AddBreath(BannerSpec spec)
    {
        if (!spec.BorderOn || spec.BorderCycle <= 0 || spec.Hold <= 0) return;
        double rounds = Math.Max(1, Math.Floor(spec.Hold / spec.BorderCycle));
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var breath = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromSeconds(spec.FadeIn),   // 等淡入完成再开始呼吸
            Duration = TimeSpan.FromSeconds(spec.BorderCycle),
            RepeatBehavior = new RepeatBehavior(rounds),
        };
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0), ease));
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(BreathLow, KeyTime.FromPercent(0.5), ease));
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), ease));
        Storyboard.SetTarget(breath, EdgePulse);
        Storyboard.SetTargetProperty(breath, new PropertyPath(OpacityProperty));
        _breathStory = new Storyboard { Children = { breath } };
        _breathStory.Begin();
    }

    /// <summary>
    /// 四边渐变带各铺一次：从屏边向内渐隐，总长 = 主带宽 + 淡出延伸带宽。
    /// 四条各做一支按当前颜色现配透明度梯度的画刷（<see cref="Band"/>），换色时整条重铺。
    /// </summary>
    private void BuildBands(Brush accent, double width, double fade)
    {
        double span = width + fade;
        EdgeTop.Height = EdgeBottom.Height = EdgeLeft.Width = EdgeRight.Width = span;
        _bands.Clear();
        // 上边向下渐隐、下边向上、左边向右、右边向左：四条各一支画刷，换级时四支一起动
        foreach (var pair in new (FrameworkElement, Point, Point)[]
                 {
                     (EdgeTop, new Point(0, 0), new Point(0, 1)),
                     (EdgeBottom, new Point(0, 1), new Point(0, 0)),
                     (EdgeLeft, new Point(0, 0), new Point(1, 0)),
                     (EdgeRight, new Point(1, 0), new Point(0, 0)),
                 })
        {
            var brush = Band(accent, width, fade, pair.Item2, pair.Item3);
            _bands.Add(brush);
            ((System.Windows.Shapes.Shape)pair.Item1).Fill = brush;
        }
    }

    /// <summary>
    /// 一条渐变带：屏边那道 0.85 透明度（全亮就成一条硬描边，不叫「淡化」），到主带宽处收到 0.3，
    /// 再顺延伸带收到全透明；中点位置按主带占比算——-border 的两个宽度直接决定亮区与尾段的比例。
    /// </summary>
    private static LinearGradientBrush Band(Brush accent, double width, double fade, Point from, Point to)
    {
        double mid = width + fade <= 0 ? 1 : width / (width + fade);
        var band = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        band.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.85), 0));
        band.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.30), mid));
        band.GradientStops.Add(new GradientStop(WithAlpha(accent, 0), 1));
        // 刻意不 Freeze：档位升降时这四支画刷的色标要自己动 0.5 s，冻住的 Freezable 挂不上动画
        return band;
    }

    /// <summary>把一支画刷的颜色换个透明度重出一色；非纯色画刷（现有资源与解析器都不会给）退回中性灰。</summary>
    private static Color WithAlpha(Brush brush, double alpha)
    {
        Color color = brush is SolidColorBrush solid ? solid.Color : Colors.Gainsboro;
        return Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
    }

    // ===== 渲染循环的挂与卸（CompositionTarget.Rendering 是静态事件，忘了退订就是常驻耗电）=====

    private void AttachLoop()
    {
        if (_loopAttached) return;
        _loopAttached = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void DetachLoop()
    {
        if (!_loopAttached) return;
        _loopAttached = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>读图片。OnLoad 是为了不把文件锁住——用户随时可以换掉那张图。</summary>
    private static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is System.IO.IOException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int newLong);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowLong(hwnd, GwlExStyle,
            GetWindowLong(hwnd, GwlExStyle) | WsExLayered | WsExTransparent | WsExToolWindow);
    }
}
