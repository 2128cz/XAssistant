using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XAssistant.Views;

// 轻量 toast：右下角短暂浮现确认信息，自动消失、点击可提前关闭；不抢占焦点（ShowActivated=false）
public partial class ToastWindow : Window
{
    private const int DisplaySeconds = 3;
    private readonly DispatcherTimer _timer = new();
    private bool _dismissed;

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

    private void Root_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Dismiss();

    // 淡出并关闭；防止重复触发（自动计时 + 手动点击）
    private void Dismiss()
    {
        if (_dismissed)
            return;
        _dismissed = true;
        _timer.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
