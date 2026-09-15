using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using XAssistant.Services;
using WinForms = System.Windows.Forms;

// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Image / Brush / Brushes / Point / FontFamily / Pen 会跟 WPF 的撞名
using Image = System.Windows.Controls.Image;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;

namespace XAssistant.Views;

/// <summary>
/// 关键词彩蛋的效果层：一块铺满虚拟屏幕的透明窗口，管两件事。
///
/// 1) 粒子：顶部浮岛（toast）下方一次只给一颗，沿自己的随机矢量直行（没有重力，轨迹不会往下弯），
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

    /// <summary>粒子的布局盒边长与字形大小的比例：留够余量，转起来不会被盒子裁掉。</summary>
    private const double BoxRatio = 1.8;

    private static EffectsWindow? _shared;
    private readonly List<Particle> _live = new();
    private TimeSpan? _lastFrame;
    private bool _loopAttached;
    private bool _bannerOn;
    private Storyboard? _bannerStory;

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
    /// 从 origin（屏幕 DIP，浮岛下沿就是这一系）朝下给一颗粒子。
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
    /// color 留空用当前强调色；seconds 是总时长，blinks 是闪几下。
    /// </summary>
    public static void ShowBanner(string text, Brush? color = null, double seconds = 1.9, int blinks = 1)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            _shared ??= new EffectsWindow();
            if (!_shared.IsVisible) _shared.Show();
            _shared.Banner(text, color, seconds, blinks);
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
    
        // 朝下方 60° 锥角内随机一个方向：正下方是 π/2（屏幕 y 轴朝下），左右各让 30°
        double direction = Math.PI / 2 + (Random.Shared.NextDouble() - 0.5) * (Math.PI / 3);
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

    private void Banner(string text, Brush? color, double seconds, int blinks)
    {
        var accent = color ?? TryFindResource("AccentBrush") as Brush ?? Brushes.Gainsboro;
        TapeText.Text = text;
        TapeText.Foreground = accent;
        // 斜线跟文字等高：行高由字号推，画刷的瓦片尺寸就按这个高算
        double height = TapeText.FontSize * 1.25;
        SlashLeft.Height = SlashRight.Height = height;
        SlashLeft.Background = SlashRight.Background = Hatch(accent, height);
        EdgeGlow.BorderBrush = accent;
        EdgeLine.BorderBrush = accent;
        // 同时只挂一条：上一句还在飞就先停下。两条动画同时抢 Tape.Opacity 的话，
        // 结果就是 HideBanner 归零后又被旧动画抬回去，且计数器只减不增、窗口永远关不掉
        _bannerStory?.Stop();
        _bannerStory = null;
        _bannerOn = true;
        AttachLoop();

        // 中间这句与屏幕四边同时闪：四个目标各一份动画实例（SetTarget 存在动画对象上，共用会互相踩）
        var story = new Storyboard();
        foreach (var target in new FrameworkElement[] { Tape, EdgeGlow, EdgeLine })
        {
            var pulse = Pulse(Math.Max(0.6, seconds), Math.Clamp(blinks, 1, 5));
            Storyboard.SetTarget(pulse, target);
            Storyboard.SetTargetProperty(pulse, new PropertyPath(OpacityProperty));
            story.Children.Add(pulse);
        }
        story.Completed += (_, _) =>
        {
            // 被新一条或 HideNow 接过的不重复收尾
            if (!ReferenceEquals(_bannerStory, story)) return;
            _bannerStory = null;
            _bannerOn = false;
            // 动画自然走完时也要把属性交回去，否则下一次本地赋值会被已结束的动画按住
            foreach (var target in new FrameworkElement[] { Tape, EdgeGlow, EdgeLine })
                target.BeginAnimation(OpacityProperty, null);
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
    ///    要 BeginAnimation(prop, null) 把属性交还给本地值，再写 0。
    /// </summary>
    private void HideNow()
    {
        _bannerOn = false;
        var story = _bannerStory;
        _bannerStory = null;
        story?.Stop();
        foreach (var target in new FrameworkElement[] { Tape, EdgeGlow, EdgeLine })
            target.BeginAnimation(OpacityProperty, null);
        Tape.Opacity = EdgeGlow.Opacity = EdgeLine.Opacity = 0;
        if (_live.Count == 0) Close();
    }

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

    /// <summary>淡入 → 中间闪 blinks 下 → 淡掉。FillBehavior.Stop：动画结束后 Opacity 回到 XAML 里那个 0。</summary>
    private static DoubleAnimationUsingKeyFrames Pulse(double seconds, int blinks)
    {
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds),
            FillBehavior = FillBehavior.Stop,
        };
        const double fade = 0.09;               // 首尾各占这么一段比例做淡入淡出
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(fade)));
        for (int i = 0; i < blinks; i++)
        {
            // 最后一闪不在中间掉下去：否则只闪一下的场景会先黑半屏再亮，看着像闪崩
            double dimAt = fade + (1 - 2 * fade) * (i + 0.5) / blinks;
            double backAt = fade + (1 - 2 * fade) * (i + 1) / blinks;
            if (i < blinks - 1) pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.15, KeyTime.FromPercent(dimAt)));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(backAt)));
        }
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        return pulse;
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
