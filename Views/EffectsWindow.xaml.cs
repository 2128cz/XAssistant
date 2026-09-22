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

    /// <summary>一屏最多叠几行：与调度器同一个数，两边不许各说各话。</summary>
    private const int MaxRows = EffectSchedule.MaxRows;

    /// <summary>行间距（DIP）：斜线有高度，挤在一起会看成一片糊的。</summary>
    private const double RowGap = 12;

    /// <summary>
    /// 组层点亮的时长：必须明显短于扫描的 0.5 s。整层跟着淡入的话，扫到左半边时那里还半明半暗，
    /// 「扫到哪亮到哪」就被这层淡入糊成「整条一起慢慢亮起来」——观感与门禁都读不到那道擦边。
    /// </summary>
    private const double GroupOnSeconds = 0.12;

    /// <summary>粒子的布局盒边长与字形大小的比例：留够余量，转起来不会被盒子裁掉。</summary>
    private const double BoxRatio = 1.8;

    private static EffectsWindow? _shared;
    private readonly List<Particle> _live = new();
    private TimeSpan? _lastFrame;
    private bool _loopAttached;
    private bool _bannerOn;
    private Storyboard? _breathStory;

    /// <summary>
    /// 一条条带的完整规格：<see cref="ShowBanner"/>（旧调用）与 <see cref="ShowCommand"/>（xa 命令行）
    /// 都先换算成这个再进 <see cref="Banner"/>。颜色在这里已经是画刷（换刷要在 UI 线程，
    /// 换算那步就在 Dispatcher.Invoke 里，不跨线程留资源）。
    /// </summary>
    private readonly record struct BannerSpec(
        string? Text, Brush Color, double Hold, double FadeIn, double FadeOut, int Blinks,
        bool BorderOn, double BorderWidth, double BorderFade, double BorderCycle, double FontSize,
        bool Urgent, string Key)
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
                Urgent: false,
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
                Key: command.GroupKey ?? "solo:" + command.Text));
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
        // 整组到点由渲染帧来触发退场（不另开一个定时器）：这一帧一帧地走，本来就是判到点的地方，
        // 多一个 DispatcherTimer 就多一条「谁先清状态」的竞路线
        if (!_exiting && _groupUntil is { } until && Mono() >= until) ExitGroup();
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
        public required string Text { get; init; }
        public required BannerSpec Spec { get; set; }
        public Storyboard? Entry { get; set; }
        public Storyboard? Drift { get; set; }
    }

    private readonly List<BannerRow> _rows = new();
    private string? _groupKey;
    private TimeSpan? _nextEntryAt;      // 下一行的入场起点：连击时按 0.5 s 一档往后排
    private TimeSpan? _groupUntil;       // 整组什么时候退场（= 组里最迟那一行）
    private bool _exiting;
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
    /// ① 组键不同 → 旧的一叠让位，开新的一叠；
    /// ② 同键同正文 → 不新增行，把那一行顶到最前重新扫一遍（同一句又喊一遍不该排成两行）；
    /// ③ 同键新正文 → 排成下面的一行，入场排在已经排上的那几行之后（每条 0.5 s，不是一起炸开）。
    /// 三种情况都把整组的到点时刻推到「此刻 + 组里最长那一条」——这就是「新的进来，时间从它进来那刻重算」。
    /// </summary>
    private void Banner(BannerSpec spec)
    {
        var now = Mono();
        // 只要边框不要正文：清空这一叠，边框单独亮一会儿（-lable off 就落在这条路上）
        if (spec.Text is not { Length: > 0 } text)
        {
            StopGroup();
            ClearRows();
            _groupKey = spec.Key;
            Edge.Visibility = spec.BorderOn ? Visibility.Visible : Visibility.Collapsed;
            Rows.Visibility = Visibility.Collapsed;
            if (!spec.BorderOn) { if (_live.Count == 0) Close(); return; }
            BuildBands(spec.Color, spec.BorderWidth, spec.BorderFade);
            Corners.Children.Clear();
            _groupUntil = now + TimeSpan.FromSeconds(Math.Max(0.2, spec.Hold + spec.FadeOut));
            _bannerOn = true;
            AttachLoop();
            FadeIn(Rows, 0);
            FadeIn(Edge, GroupOnSeconds);
            AddBreath(spec);
            return;
        }

        bool fresh = _groupKey != spec.Key;
        if (fresh)
        {
            StopGroup();
            ClearRows();
            _groupKey = spec.Key;
            _nextEntryAt = null;
            Rows.Opacity = 0;
        }
        Rows.Visibility = Visibility.Visible;

        var twin = _rows.FirstOrDefault(row => row.Text == text);
        if (twin is not null)
        {
            _rows.Remove(twin);
            _rows.Insert(0, twin);
            twin.Spec = spec;
            RowStack.Children.Remove(twin.Root);
            RowStack.Children.Insert(0, twin.Root);
            EnterRow(twin, now);                       // 顶到最前并重扫：让「还在响」看得见
        }
        else
        {
            if (_rows.Count >= MaxRows) EvictOldestRow();
            var row = BuildRow(spec, text);
            _rows.Add(row);
            EnterRow(row, now);
        }

        ReTimeGroup(now, spec);
        ShowGroupBorder(spec);
        if (fresh) AddBlink(spec);
        _bannerOn = true;
        AttachLoop();
    }

    /// <summary>整组到点时刻：此刻 + 组里最长那一条的占屏时长。</summary>
    private void ReTimeGroup(TimeSpan now, BannerSpec spec)
    {
        double longest = _rows.Count == 0 ? spec.ScreenSeconds : _rows.Max(row => row.Spec.ScreenSeconds);
        _groupUntil = now + TimeSpan.FromSeconds(Math.Max(0.2, longest));
        if (Rows.Opacity < 0.99 && !_exiting) FadeIn(Rows, GroupOnSeconds);   // 第一行进来时把组层点亮
    }

    /// <summary>
    /// 边框与四角三角按**组里最高那一档**亮：一叠里混进一条紧急的，整屏的边框就该是红的、
    /// 该有四颗巨型三角——等级差的消息挤在一起时，说服力取上限而不是平均。
    /// </summary>
    private void ShowGroupBorder(BannerSpec newest)
    {
        var top = _rows.Select(row => row.Spec)
            .Concat(new[] { newest })
            .OrderByDescending(spec => spec.Urgent)
            .ThenByDescending(spec => spec.BorderWidth)
            .First();
        Edge.Visibility = top.BorderOn ? Visibility.Visible : Visibility.Collapsed;
        if (top.BorderOn)
        {
            BuildBands(top.Color, top.BorderWidth, top.BorderFade);
            FadeIn(Edge, GroupOnSeconds);
            AddBreath(top);
        }
        double height = top.FontSize * 1.25;
        BuildCorners(top, top.Color, height);
        if (top.Urgent) FadeIn(Corners, GroupOnSeconds);
        else { Corners.Opacity = 0; Corners.Children.Clear(); }
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
        double height = spec.FontSize * 1.25;
        var body = new TextBlock
        {
            Text = text,
            FontSize = spec.FontSize,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = spec.Color,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var left = new Border
        {
            Height = height,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 22, 0),
            Background = Hatch(spec.Color, height),
        };
        var right = new Border
        {
            Height = height,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(22, 0, 0, 0),
            Background = Hatch(spec.Color, height),
        };
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
        var at = _nextEntryAt is { } pending && pending > now ? pending : now;
        _nextEntryAt = at + TimeSpan.FromSeconds(SweepSeconds);
        var delay = at - now;

        StopRow(row);                                   // 重扫同一行：先把上一次挂在它身上的动画摘干净
        var hidden = HiddenRect(Width, Height);
        var shown = new Rect(0, 0, Width, Height);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var story = new Storyboard { BeginTime = delay };

        var reveal = new RectAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(SweepSeconds),
            FillBehavior = FillBehavior.HoldEnd,
        };
        reveal.KeyFrames.Add(new EasingRectKeyFrame(hidden, KeyTime.FromPercent(0)));
        reveal.KeyFrames.Add(new EasingRectKeyFrame(shown, KeyTime.FromPercent(1), ease));
        Storyboard.SetTarget(reveal, row.Content);
        Storyboard.SetTargetProperty(reveal, new PropertyPath("Clip.Rect"));
        story.Children.Add(reveal);

        var run = new DoubleAnimation(Width, -SweepWidth, new Duration(TimeSpan.FromSeconds(SweepSeconds)))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(run, row.Head);
        Storyboard.SetTargetProperty(run, new PropertyPath("RenderTransform.X"));
        story.Children.Add(run);

        var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(SweepSeconds) };
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
    /// 整组退场：每行的 Clip 反向收回（左边界回到屏右，线像被抽走），扫描头反向再跑一趟当擦除器，
    /// 正文同时往左带一小段——读成「平移出去」。边框与四角三角跟着一起淡掉，全屏效果就此结束。
    /// </summary>
    private void ExitGroup()
    {
        if (_exiting) return;
        _exiting = true;
        double fadeOut = _rows.Count == 0
            ? BannerFadeOutSeconds
            : Math.Clamp(_rows.Max(row => row.Spec.FadeOut), 0.2, 3);
        var shown = new Rect(0, 0, Width, Height);
        var hidden = HiddenRect(Width, Height);
        var story = new Storyboard();
        foreach (var row in _rows)
        {
            // 先把本地值对齐到「全开」，再停旧动画：Stop 会把 HoldEnd 的值撤掉，本地值不对齐就闪一下
            row.Mask.Rect = shown;
            StopRow(row);
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
        }
        foreach (var layer in new FrameworkElement[] { Rows, Edge, Corners })
        {
            var fade = new DoubleAnimation(Rows.Opacity, 0, new Duration(TimeSpan.FromSeconds(fadeOut)));
            Storyboard.SetTarget(fade, layer);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            story.Children.Add(fade);
        }
        story.Completed += (_, _) =>
        {
            // 被新一条或 HideNow 接过的不重复收尾
            if (!ReferenceEquals(_groupStory, story)) return;
            _groupStory = null;
            _exiting = false;
            _bannerOn = false;
            StopAnimation();
            if (_live.Count == 0) Close();
        };
        _groupStory = story;
        story.Begin();
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
        _groupUntil = null;
        _exiting = false;
        foreach (var layer in new FrameworkElement[] { Rows, RowsPulse, Edge, EdgePulse, Corners })
            layer.BeginAnimation(OpacityProperty, null);
        Rows.Opacity = Edge.Opacity = Corners.Opacity = 0;
        RowsPulse.Opacity = EdgePulse.Opacity = 1;   // 内层回到全亮：闪到暗端时被收起，下一轮开头不带旧值
        Rows.Visibility = Visibility.Visible;
    }

    /// <summary>把这一叠行全摘掉：停掉挂在行上的两套动画，再从排版里移走。</summary>
    private void ClearRows()
    {
        foreach (var row in _rows)
        {
            StopRow(row);
            RowStack.Children.Remove(row.Root);
        }
        _rows.Clear();
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
