using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Brush / Point 会跟 WPF 的撞名
using Brush = System.Windows.Media.Brush;

namespace XAssistant.Views;

/// <summary>
/// 顶部持久消息栈：`xa -s ... -lable on 24 "…"` 那句大字在屏幕中间淡完就没了，
/// 这里把同样的话钉成一张卡片留在屏幕顶上——新消息插最前，旧的往下排着可回看，
/// 不自动消失（要人工接管的那种提醒，几秒后就不该凭空蒸发）。
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
    /// 钉一条消息：tint 是这条命令的颜色（左侧色条）。空文本不入栈——只亮边框的指令没话可留。
    /// 任意线程可调，内部调度回 UI 线程。
    /// </summary>
    public static void Push(string? text, Brush tint)
    {
        var app = System.Windows.Application.Current;
        if (app is null || string.IsNullOrWhiteSpace(text)) return;
        app.Dispatcher.Invoke(() =>
        {
            _shared ??= new MessageStackWindow();
            if (!_shared.IsVisible) _shared.Show();
            _shared.AddCard(text.Trim(), tint);
        });
    }

    /// <summary>撤下全部卡片并关窗（「清空全部」按钮与托盘退出走的都是这个语义）。</summary>
    public static void ClearAll()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() => _shared?.Close());
    }

    private void AddCard(string text, Brush tint)
    {
        while (Cards.Children.Count >= MaxCards)
            Cards.Children.RemoveAt(Cards.Children.Count - 1);   // 尾端是最旧的
        var card = BuildCard(text, tint);
        Cards.Children.Insert(0, card);                          // 新消息插最前
        card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));   // 与 Toast 同款淡入
    }

    /// <summary>一张卡片：左侧竖色条 + 消息文本 + HH:mm 时间戳 + 单条 ✕。</summary>
    private Border BuildCard(string text, Brush tint)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = new System.Windows.Shapes.Rectangle
        {
            Width = 3, Fill = tint, Margin = new Thickness(0, 1, 8, 1),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);

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
