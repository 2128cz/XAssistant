using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using XAssistant.Services;
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Brush / Point 会跟 WPF 的撞名
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace XAssistant.Views;

/// <summary>
/// 顶部持久消息栈：`xa -s ... -lable on 24 "…"` 那句大字在屏幕中间淡完就没了，
/// 这里把同样的话钉成一张卡片留在屏幕顶上——新消息插最前，旧的往下排着可回看，
/// 不自动消失（要人工接管的那种提醒，几秒后就不该凭空蒸发）。
///
/// 卡片左侧是来源徽章：`-from qoder` 这类平台标记拿 IDE 图标（Assets\IdeIcons\，
/// 由 extract-ide-icons.ps1 从各家 exe 抽取的 256px 帧）贴进「淡化主题色圆底」，
/// 没图标退字母徽章，没来源退纯色条——多平台聚合后得一眼看出消息是谁发的。
///
/// 窗归常驻主程序持有：无头 xa 实例放完全屏效果就退，养不住持久窗，
/// 所以消息经 <see cref="Services.NotificationPipe"/> 转发进来（详见 App 的接线）。
/// 三条约束跟 ToastWindow 一致：不抢焦点、无边框、卡片清空后自己关窗。
/// </summary>
public sealed partial class MessageStackWindow : Window
{
    /// <summary>同屏卡片上限：超了淘汰最旧的，栈不至于滚成一面墙。</summary>
    public const int MaxCards = 10;

    /// <summary>栈顶与桌面顶端的间距：让开 ToastWindow 那条 +16 的浮岛带，两者互不遮挡。</summary>
    private const int TopOffset = 80;

    /// <summary>来源图标目录（exe 旁的内容拷贝）与加载缓存：没抽到图标的平台只碰一次磁盘。</summary>
    private static readonly string IconDir = Path.Combine(AppContext.BaseDirectory, "Assets", "IdeIcons");
    private static readonly Dictionary<string, ImageSource?> IconCache = new();

    private static MessageStackWindow? _shared;

    /// <summary>当前钉在栈上的卡片数。设置与离屏夹具都读它。</summary>
    public static int CardCount => _shared?.Cards.Children.Count ?? 0;

    private MessageStackWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (ReferenceEquals(_shared, this)) _shared = null; };
        // 卡片增删会改窗口宽度（SizeToContent），跟着重新水平居中
        SizeChanged += (_, _) => Center();
    }

    /// <summary>
    /// 钉一条消息：tint 是这条命令的颜色（小圆牌底与描边），source 是 `-from` 的平台标记，
    /// urgent 会在行首多画一颗自绘警告三角（不用 ⚠ 字形：单色、不可控尺寸、缺字体就掉方框）。
    /// 空文本不入栈——只亮边框的指令没话可留。任意线程可调，内部调度回 UI 线程。
    /// </summary>
    public static void Push(string? text, Brush tint, string? source = null, bool urgent = false)
    {
        var app = System.Windows.Application.Current;
        if (app is null || string.IsNullOrWhiteSpace(text)) return;
        app.Dispatcher.Invoke(() =>
        {
            _shared ??= new MessageStackWindow();
            if (!_shared.IsVisible) _shared.Show();
            _shared.AddCard(text.Trim(), tint, source, urgent);
        });
    }

    /// <summary>撤下全部卡片并关窗（「清空全部」按钮与托盘退出走的都是这个语义）。</summary>
    public static void ClearAll()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() => _shared?.Close());
    }

    private void AddCard(string text, Brush tint, string? source, bool urgent)
    {
        while (Cards.Children.Count >= MaxCards)
            Cards.Children.RemoveAt(Cards.Children.Count - 1);   // 尾端是最旧的
        var card = BuildCard(text, tint, source, urgent);
        Cards.Children.Insert(0, card);                          // 新消息插最前
        card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));   // 与 Toast 同款淡入
    }

    /// <summary>一张卡片：来源徽章（或纯色条）+ 消息文本 + HH:mm 时间戳 + 单条 ✕。</summary>
    private Border BuildCard(string text, Brush tint, string? source, bool urgent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lead = BuildBadge(tint, source, urgent);
        Grid.SetColumn(lead, 0);
        grid.Children.Add(lead);

        // SetResourceReference 而不是直接赋刷：换主题时字色会跟着走，DynamicResource 的代码版
        var message = new TextBlock
        {
            Name = "StackCardText", Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center,
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Grid.SetColumn(message, 1);
        grid.Children.Add(message);

        var time = new TextBlock
        {
            Name = "StackCardTime",
            Text = DateTime.Now.ToString("HH:mm"), FontSize = 11,
            Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        time.SetResourceReference(TextBlock.ForegroundProperty, "TextHint");
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var close = new TextBlock
        {
            Name = "StackCardClose", Text = "✕", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "关掉这条",
        };
        close.SetResourceReference(TextBlock.ForegroundProperty, "TextHint");
        Grid.SetColumn(close, 3);
        grid.Children.Add(close);

        // 删的是整张卡（Cards 的直接子元素是这张 Border）：挂着 grid 去 Remove 会炸在「不是本集合的孩子」上
        var card = new Border { Child = grid, Margin = new Thickness(0, 0, 0, 6) };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveCard(card); };
        return card;
    }

    /// <summary>
    /// 卡片头部：有来源标记就贴「淡化 tint 圆底 + IDE 图标」徽章（没图标退首字母），
    /// 没来源（打字彩蛋、手写 xa）保持原来的 3px 色条。
    /// </summary>
    private static FrameworkElement BuildBadge(Brush tint, string? source, bool urgent)
    {
        FrameworkElement core = BuildCore(tint, source);
        if (!urgent) return core;
        // 行首挂一颗自绘小三角：只靠颜色分不出“要命”与“知会”，而字形警告符不可控
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(WarningGlyph.Build(tint, 22, HorizontalAlignment.Left));
        ((FrameworkElement)core).Margin = new Thickness(6, 1, 8, 1);
        row.Children.Add(core);
        return row;
    }

    private static FrameworkElement BuildCore(Brush tint, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new System.Windows.Shapes.Rectangle
            {
                Width = 3, Fill = tint, Margin = new Thickness(0, 1, 8, 1),
                VerticalAlignment = VerticalAlignment.Stretch,
            };

        var badge = new Border
        {
            Width = 28, Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Fade(tint, 0.22),
            BorderBrush = Fade(tint, 0.6),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "来源：" + source,
        };
        ImageSource? icon = IconOf(source);
        if (icon is not null)
            badge.Child = new System.Windows.Controls.Image
            {
                Source = icon, Width = 18, Height = 18,
                Stretch = Stretch.Uniform, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            };
        else
            badge.Child = new TextBlock   // 字母徽章：没抽到图标的平台（claude/cursor…）退首字母
            {
                Text = source.Length > 0 ? char.ToUpperInvariant(source[0]).ToString() : "?",
                FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = tint, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        return badge;
    }

    /// <summary>平台名 → 256px 图标（磁盘缓存；文件缺失记 null，下次不再碰盘）。</summary>
    private static ImageSource? IconOf(string source)
    {
        string key = source.Trim().ToLowerInvariant();
        if (IconCache.TryGetValue(key, out ImageSource? cached)) return cached;
        ImageSource? loaded = null;
        try
        {
            string file = Path.Combine(IconDir, key + ".png");
            if (File.Exists(file))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 一次读尽，不留文件句柄
                bmp.UriSource = new Uri(file, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                loaded = bmp;
            }
        }
        catch (Exception) { loaded = null; }   // 坏图当没图：退字母徽章，不连累整张卡
        IconCache[key] = loaded;
        return loaded;
    }

    /// <summary>命令色淡化为徽章底：只改 alpha，不另发明颜色，分级色与主题保持同一系。</summary>
    private static Brush Fade(Brush tint, double alpha)
        => tint is SolidColorBrush solid
            ? new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), solid.Color.R, solid.Color.G, solid.Color.B))
            : tint;

    private void RemoveCard(UIElement card)
    {
        Cards.Children.Remove(card);
        if (Cards.Children.Count == 0) Close();   // 栈空了就下线，不留一块空壳在上面
    }

    private void ClearButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Close();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Center();
    }

    private void Center()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Top + TopOffset;
    }
}
