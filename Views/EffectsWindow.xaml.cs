using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    public const double BannerFadeInSeconds = 1.0;

    /// <summary>默认持续秒数：闪烁铺在这一段里，不写时长时就是这一段撑住可读。</summary>
    public const double BannerHoldSeconds = 5.0;

    /// <summary>默认淡出秒数。</summary>
    public const double BannerFadeOutSeconds = 1.0;

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

    /// <summary>粒子的布局盒边长与字形大小的比例：留够余量，转起来不会被盒子裁掉。</summary>
    private const double BoxRatio = 1.8;

    private static EffectsWindow? _shared;
    private readonly List<Particle> _live = new();
    private TimeSpan? _lastFrame;
    private bool _loopAttached;
    private bool _bannerOn;
    private Storyboard? _bannerStory;
    private Storyboard? _breathStory;
    private Storyboard? _driftStory;

    /// <summary>
    /// 一条条带的完整规格：<see cref="ShowBanner"/>（旧调用）与 <see cref="ShowCommand"/>（xa 命令行）
    /// 都先换算成这个再进 <see cref="Banner"/>。颜色在这里已经是画刷（换刷要在 UI 线程，
    /// 换算那步就在 Dispatcher.Invoke 里，不跨线程留资源）。
    /// </summary>
    private readonly record struct BannerSpec(
        string? Text, Brush Color, double Hold, double FadeIn, double FadeOut, int Blinks,
        bool BorderOn, double BorderWidth, double BorderFade, double BorderCycle, double FontSize,
        bool Urgent);

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
    /// 保持字面签名不动：<see cref="Services.Keywords.KeywordWatcher"/> 与测试夹具直接调这个。
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
                BorderWidth: 50,
                BorderFade: 30,
                BorderCycle: 0,
                FontSize: 46,
                Urgent: false));
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
                Urgent: command.Urgent));
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
            : new Image { Source = source, Width = size };

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

    // ===== 警告语 + 边缘高亮：一个动作，一起淡入淡出 =====

    private void Banner(BannerSpec spec)
    {
        var accent = spec.Color;
        // 文字条带：不给正文就整段收起，只剩边框那圈渐变带在工作
        bool hasText = spec.Text is { Length: > 0 };
        Tape.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (hasText)
        {
            TapeText.Text = spec.Text;
            TapeText.Foreground = accent;
            TapeText.FontSize = spec.FontSize;
            // 斜线跟文字等高：行高由字号推，画刷的瓦片尺寸就按这个高算
            double height = spec.FontSize * 1.25;
            SlashLeft.Height = SlashRight.Height = height;
            // 两侧各一支画刷：慢移是挂在画刷的 Viewport 上的，两侧共用一支的话两条动画会互相踩
            SlashLeft.Background = Hatch(accent, height);
            SlashRight.Background = Hatch(accent, height);
            // 紧急档不在文字两边夹小三角，而是上下四颗巨型的（见 BuildCorners）：
            // 警告程度要从余光里就能看见，藏在正文旁边等于没提醒
            SlashLeft.Child = SlashRight.Child = null;
            BuildCorners(spec, accent, height);
        }

        // 四边渐变带：关了边框就整层收起，不再铺画刷
        Edge.Visibility = spec.BorderOn ? Visibility.Visible : Visibility.Collapsed;
        if (spec.BorderOn) BuildBands(accent, spec.BorderWidth, spec.BorderFade);

        // 同时只挂一条：上一句还在飞就先停下。两条动画同时抢 Tape.Opacity 的话，
        // 结果就是 HideBanner 归零后又被旧动画抬回去，且计数器只减不增、窗口永远关不掉
        StopAnimation();
        _bannerOn = true;
        AttachLoop();

        double total = Math.Max(0.1, spec.FadeIn + spec.Hold + spec.FadeOut);
        // 扫描头单趟取 0.5 s，但不许越过淡入：淡入只有 0.2 s 时扫入也得在那半秒内收住
        double sweep = Math.Clamp(Math.Min(SweepSeconds, Math.Max(0.15, spec.FadeIn)), 0.15, total);
        double revealAt = Math.Clamp(sweep / total, 0.01, 1);              // 扫完那一刻（开始有字的位置）
        double holdEndAt = Math.Clamp((spec.FadeIn + spec.Hold) / total, revealAt, 1);   // 退场起点
        // 全遮起步：Clip 是个零宽矩形，此刻一个字都不露——入场动画的起点必须是"什么都没有"，
        // 否则扫描头撞开的是已经亮着的线，看着就是原地淡入
        Tape.Clip = new RectangleGeometry(new Rect(Width, 0, 0, Height));

        // 中间这句、屏幕四边、四颗警告三角一起淡入淡出：三个目标各一份动画实例
        // （SetTarget 存在动画对象上，共用会互相踩）
        var story = new Storyboard();
        foreach (var target in new FrameworkElement[] { Edge, Corners })
        {
            var pulse = Pulse(spec, total);
            Storyboard.SetTarget(pulse, target);
            Storyboard.SetTargetProperty(pulse, new PropertyPath(OpacityProperty));
            story.Children.Add(pulse);
        }
        // 正文那条另算一份：淡入段压到扫描头跑完的那一刻（扫到哪亮到哪，不是先蒙亮再扫），
        // 省下的那截补进持续段——holdEndAt 不变，闪烁位置与退场起点都不跟着抖
        if (hasText)
        {
            var tapePulse = Pulse(spec with { FadeIn = sweep, Hold = spec.Hold + (spec.FadeIn - sweep) }, total);
            Storyboard.SetTarget(tapePulse, Tape);
            Storyboard.SetTargetProperty(tapePulse, new PropertyPath(OpacityProperty));
            story.Children.Add(tapePulse);
            Sweep.Child = BuildSweepBody(accent);
            story.Children.Add(Reveal(total, revealAt, holdEndAt));
            story.Children.Add(SweepGlow(total, revealAt, holdEndAt));
            story.Children.Add(SweepRun(total, revealAt, holdEndAt));
            story.Children.Add(ExitShift(total, holdEndAt));
        }
        else
        {
            Sweep.Child = null;
            Tape.Opacity = 0;
        }
        AddBreath(spec);
        if (hasText) AddDrift(sweep, spec.Hold);
        story.Completed += (_, _) =>
        {
            // 被新一条或 HideNow 接过的不重复收尾
            if (!ReferenceEquals(_bannerStory, story)) return;
            _bannerStory = null;
            _bannerOn = false;
            // 动画自然走完时也要把属性交回去，否则下一次本地赋值会被已结束的动画按住
            StopAnimation();
            if (_live.Count == 0) Close();
        };
        _bannerStory = story;
        story.Begin();
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
    /// 停掉主戏、边框呼吸、斜线慢移三套动画，把被它们在合成树上按住的属性交还本地并复位。
    /// 先置空引用再 Stop：Stop 会触发 Completed，旧引用留着会让收尾流程（关窗、清动画）重跑一遍。
    /// 新加的四步动画每一个都占一个属性（Clip.Rect / 扫描头 X / 扫描头 Opacity / 退场位移 / 画刷 Viewport），
    /// 少归还一个，下一次本地赋值就会被上一次残留的动画按住。
    /// </summary>
    private void StopAnimation()
    {
        var story = _bannerStory;
        _bannerStory = null;
        story?.Stop();
        var breath = _breathStory;
        _breathStory = null;
        breath?.Stop();
        var drift = _driftStory;
        _driftStory = null;
        drift?.Stop();
        Tape.BeginAnimation(OpacityProperty, null);
        Edge.BeginAnimation(OpacityProperty, null);
        Corners.BeginAnimation(OpacityProperty, null);
        EdgePulse.BeginAnimation(OpacityProperty, null);
        Sweep.BeginAnimation(OpacityProperty, null);
        SweepShift.BeginAnimation(TranslateTransform.XProperty, null);
        TapeShift.BeginAnimation(TranslateTransform.XProperty, null);
        if (Tape.Clip is RectangleGeometry mask) mask.BeginAnimation(RectangleGeometry.RectProperty, null);
        // 斜线那两支画刷不用归还：每次上屏都换新的一支，被动画按住的旧支已经不在树上
        Tape.Opacity = Edge.Opacity = Corners.Opacity = 0;
        EdgePulse.Opacity = 1;   // 内层回到全亮：呼吸跑到暗端时被收起，下一轮开头不带旧值
        Sweep.Opacity = 0;
        SweepShift.X = -SweepWidth;   // 扫描头停在屏左外：收起时不留一块透明带子在画面里
        TapeShift.X = 0;
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
    /// 三段时序：淡入 spec.FadeIn → 持续 spec.Hold（闪烁铺在这一段里）→ 淡出 spec.FadeOut，
    /// 段长由指令各自给，关键帧百分比按总长换算。
    /// FillBehavior.Stop：动画结束后 Opacity 回到 XAML 里那个 0。
    /// </summary>
    private static DoubleAnimationUsingKeyFrames Pulse(BannerSpec spec, double total)
    {
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(total),
            FillBehavior = FillBehavior.Stop,
        };
        double fadeInAt = Math.Clamp(spec.FadeIn / total, 0, 1);                         // 淡入结束 = 持续段的起点
        double holdEndAt = Math.Clamp((spec.FadeIn + spec.Hold) / total, fadeInAt, 1);   // 持续段终点 = 淡出的起点
        int blinks = Math.Clamp(spec.Blinks, 1, 5);
        if (fadeInAt > 0) pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(fadeInAt)));
        if (holdEndAt > fadeInAt)
        {
            for (int i = 0; i < blinks; i++)
            {
                // 闪烁只占持续段，两端不碰：最后一闪也不中途掉下去（否则只闪一下的场景先黑半屏再亮，看着像闪崩）
                double dimAt = fadeInAt + (holdEndAt - fadeInAt) * (i + 0.5) / blinks;
                double backAt = fadeInAt + (holdEndAt - fadeInAt) * (i + 1) / blinks;
                if (i < blinks - 1) pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.15, KeyTime.FromPercent(dimAt)));
                pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(backAt)));
            }
        }
        // 淡出为 0 时持续段直接顶到总长：末尾再放一帧 0 会和上一帧同时间点，跳过（释放动画时自然回 0）
        if (holdEndAt < 1) pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        return pulse;
    }

    /// <summary>
    /// 揭示遮罩：<see cref="Tape"/> 整层的 Clip 从「屏右一个零宽矩形」长成整个屏幕。左边界就是那道
    /// 往左跑的警戒线起点，所以线与字是被扫描头一路撞开出来的；退场反向收回（左边界回到屏右），
    /// 看着像被抽走。入场用 EaseOut（撞开时快、收住时缓），退场匀速。
    /// 关键帧按总长的百分比铺，和 <see cref="Pulse"/> 共用同一把尺子。
    /// </summary>
    private RectAnimationUsingKeyFrames Reveal(double total, double revealAt, double holdEndAt)
    {
        var hidden = new Rect(Width, 0, 0, Height);
        var shown = new Rect(0, 0, Width, Height);
        var reveal = new RectAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(total),
            FillBehavior = FillBehavior.Stop,
        };
        reveal.KeyFrames.Add(new EasingRectKeyFrame(hidden, KeyTime.FromPercent(0)));
        reveal.KeyFrames.Add(new EasingRectKeyFrame(shown, KeyTime.FromPercent(revealAt),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        if (holdEndAt < 1)
        {
            reveal.KeyFrames.Add(new LinearRectKeyFrame(shown, KeyTime.FromPercent(holdEndAt)));
            reveal.KeyFrames.Add(new LinearRectKeyFrame(hidden, KeyTime.FromPercent(1)));
        }
        Storyboard.SetTarget(reveal, Tape);
        Storyboard.SetTargetProperty(reveal, new PropertyPath("Clip.Rect"));
        return reveal;
    }

    /// <summary>
    /// 扫描头的位置：入场从屏右外冲到屏左外，跑在揭示边界前面（所以它是 Tape 的兄弟而不是孩子，
    /// 孩子的话会被自己拉开的那片 Clip 一起裁掉）；退场反向再跑一趟当擦除器。两趟之间停在屏左外。
    /// </summary>
    private DoubleAnimationUsingKeyFrames SweepRun(double total, double revealAt, double holdEndAt)
    {
        var run = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(total),
            FillBehavior = FillBehavior.Stop,
        };
        run.KeyFrames.Add(new EasingDoubleKeyFrame(Width, KeyTime.FromPercent(0)));
        run.KeyFrames.Add(new EasingDoubleKeyFrame(-SweepWidth, KeyTime.FromPercent(revealAt),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        if (holdEndAt < 1)
        {
            run.KeyFrames.Add(new LinearDoubleKeyFrame(-SweepWidth, KeyTime.FromPercent(holdEndAt)));
            run.KeyFrames.Add(new LinearDoubleKeyFrame(Width, KeyTime.FromPercent(1)));
        }
        Storyboard.SetTarget(run, SweepShift);
        Storyboard.SetTargetProperty(run, new PropertyPath(TranslateTransform.XProperty));
        return run;
    }

    /// <summary>扫描头只在两趟路上亮，跑完即灭：留一块半透明带子压在屏上不算效果，算脏。</summary>
    private DoubleAnimationUsingKeyFrames SweepGlow(double total, double revealAt, double holdEndAt)
    {
        var glow = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(total),
            FillBehavior = FillBehavior.Stop,
        };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(revealAt * 0.15)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(revealAt * 0.85)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(revealAt)));
        if (holdEndAt < 1)
        {
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(holdEndAt)));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent((holdEndAt + 1) / 2)));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        }
        Storyboard.SetTarget(glow, Sweep);
        Storyboard.SetTargetProperty(glow, new PropertyPath(OpacityProperty));
        return glow;
    }

    /// <summary>
    /// 退场位移：整条带子在淡出段往左带一小段（<see cref="ExitDrift"/>），配合 Clip 收回读成「平移出去」。
    /// 只给位移不给缩放——缩放会连正文一起变形，而正文这一路都不该动。
    /// </summary>
    private DoubleAnimationUsingKeyFrames ExitShift(double total, double holdEndAt)
    {
        var shift = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(total),
            FillBehavior = FillBehavior.Stop,
        };
        shift.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        if (holdEndAt < 1)
        {
            shift.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(holdEndAt)));
            shift.KeyFrames.Add(new EasingDoubleKeyFrame(-ExitDrift, KeyTime.FromPercent(1),
                new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        }
        else shift.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(shift, TapeShift);
        Storyboard.SetTargetProperty(shift, new PropertyPath(TranslateTransform.XProperty));
        return shift;
    }

    /// <summary>
    /// 警戒线的持续慢移：驻留段里每 <see cref="DriftSecondsPerTile"/> 秒挪走一个瓦片。
    /// 动的是画刷的 Viewport（采样窗往左挪一格），线框与正文各自不动，所以是「条纹在背景里流」，
    /// 不是「整条带子在飘」。位移取整整一个瓦片，循环接缝看不出来。
    /// 挂在单独一条 story 上：主戏那条随时会被下一条接管，慢移的节拍不该跟着一起被掐断。
    /// </summary>
    private void AddDrift(double sweep, double hold)
    {
        if (hold <= 0) return;
        double rounds = Math.Max(1, Math.Floor(hold / DriftSecondsPerTile));
        var drift = new Storyboard();
        foreach (var slash in new Border[] { SlashLeft, SlashRight })
        {
            if (slash.Background is not DrawingBrush hatch) continue;
            var tile = hatch.Viewport;
            var slide = new RectAnimation
            {
                From = tile,
                To = new Rect(tile.X + tile.Width, tile.Y, tile.Width, tile.Height),
                BeginTime = TimeSpan.FromSeconds(sweep),   // 等扫完再开始流，别跟入场抢方向感
                Duration = TimeSpan.FromSeconds(DriftSecondsPerTile),
                RepeatBehavior = new RepeatBehavior(rounds),
            };
            Storyboard.SetTarget(slide, slash);
            Storyboard.SetTargetProperty(slide, new PropertyPath("Background.Viewport"));
            drift.Children.Add(slide);
        }
        if (drift.Children.Count == 0) return;
        _driftStory = drift;
        drift.Begin();
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
        EdgeTop.Fill = Band(accent, width, fade, new Point(0, 0), new Point(0, 1));      // 上边：向下渐隐
        EdgeBottom.Fill = Band(accent, width, fade, new Point(0, 1), new Point(0, 0));   // 下边：向上渐隐
        EdgeLeft.Fill = Band(accent, width, fade, new Point(0, 0), new Point(1, 0));     // 左边：向右渐隐
        EdgeRight.Fill = Band(accent, width, fade, new Point(1, 0), new Point(0, 0));    // 右边：向左渐隐
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
        band.Freeze();   // 只读化：四条带是一次性铺的，冻结后渲染线程直接复用
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
