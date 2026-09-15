using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Point 会跟窗口坐标撞名
using Point = System.Windows.Point;

namespace XAssistant.Views;

// 轻量 toast：右下角短暂浮现确认信息，自动消失、点击可提前关闭；不抢占焦点（ShowActivated=false）
public partial class ToastWindow : Window
{
    private const int DisplaySeconds = 3;
    private readonly DispatcherTimer _timer = new();
    private bool _dismissed;
    private int _generation;

    public ToastWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;

        // 淡入
        Opacity = 0;
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        );

        _timer.Interval = TimeSpan.FromSeconds(DisplaySeconds);
        _timer.Tick += (_, _) => Dismiss();
        _timer.Start();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 内容测量完成后定位到工作区顶部居中
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Top + 16;
    }

    /// <summary>
    /// 浮岛的下沿中心（屏幕 DIP）：它靠工作区顶部居中，横向中心与宽度无关，
    /// 所以不必等测量完就能报出这一点——粒子从这里往下吐。
    /// 起点放在岛下方而不是岛中心：两个透明窗比谁压在上面没意义，错开就解决了。
    /// </summary>
    public static Point Anchor
    {
        get
        {
            Rect workArea = SystemParameters.WorkArea;
            return new Point(workArea.Left + workArea.Width / 2, workArea.Top + 46);
        }
    }

    /// <summary>
    /// 复用同一个窗口：换文案并重新计时。每次新建一个，连击关键词就会在屏幕顶上叠出一摞提示条
    /// （上一版就是这个事故：五个 toast 同时挂着，后面的动画还被前面的挡了）。
    /// </summary>
    public void Reset(string message)
    {
        _generation++;          // 让可能正在飞的淡出动画作废，否则它到点会把刚复用的窗口关掉
        _dismissed = false;
        MessageText.Text = message;
        _timer.Stop();
        _timer.Start();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void Root_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Dismiss();

    // 淡出并关闭；防止重复触发（自动计时 + 手动点击）
    private void Dismiss()
    {
        if (_dismissed)
            return;
        _dismissed = true;
        _timer.Stop();
        int generation = _generation;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        // 只在没被 Reset 接管过时才真关：否则一条新提示刚复用这个窗口就被旧动画关掉
        fade.Completed += (_, _) => { if (generation == _generation) Close(); };
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
