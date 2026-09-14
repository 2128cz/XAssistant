using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using XAssistant.ViewModels;

namespace XAssistant.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    /// <summary>峰值线宿主高度的一半，用于防止线与两端标注贴边被裁。</summary>
    private const double RuleHalfHeight = 13;

    /// <summary>
    /// 两栏排布的宽度下限。PART 2 拿三份宽，热力图按 1120 的设计稿等比缩放，
    /// 栏宽低于约 770 时键帽字号就掉到 7 像素以下，读不动，不如并成单栏。
    /// </summary>
    private const double TwoColumnMinWidth = 1360;

    /// <summary>顶部四张统计卡排成一排的下限；再窄就把 58 号的缩写挤到裁字，改成两列两张。</summary>
    private const double CardsInRowMinWidth = 1000;

    private INotifyPropertyChanged? _observed;

    public static readonly DependencyProperty IsTwoColumnLayoutProperty = DependencyProperty.Register(
        nameof(IsTwoColumnLayout), typeof(bool), typeof(DashboardView), new PropertyMetadata(false));

    /// <summary>
    /// 「输入」「记录」是否左右并排。屏幕比例给结论，实际宽度兜底：
    /// 竖屏显示器一定单栏，横屏但窗口拖窄时也不硬撑，否则两栏会被 ScrollViewer 裁掉。
    /// </summary>
    public bool IsTwoColumnLayout
    {
        get => (bool)GetValue(IsTwoColumnLayoutProperty);
        private set => SetValue(IsTwoColumnLayoutProperty, value);
    }

    public static readonly DependencyProperty IsOverviewInRowProperty = DependencyProperty.Register(
        nameof(IsOverviewInRow), typeof(bool), typeof(DashboardView), new PropertyMetadata(false));

    /// <summary>概览那排统计卡是否排成一列四张。概览恒为通栏，所以只看自身宽度，不看两栏与否。</summary>
    public bool IsOverviewInRow
    {
        get => (bool)GetValue(IsOverviewInRowProperty);
        private set => SetValue(IsOverviewInRowProperty, value);
    }

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveViewModel();
        Loaded += (_, _) =>
        {
            RefreshLayoutSignals();
            LayoutPeakRule();
        };
    }

    private void ObserveViewModel()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnViewModelPropertyChanged;
        _observed = DataContext as INotifyPropertyChanged;
        if (_observed is not null) _observed.PropertyChanged += OnViewModelPropertyChanged;
        RefreshLayoutSignals();
        LayoutPeakRule();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.PeakLineRatio) or nameof(DashboardViewModel.HasPeak))
            LayoutPeakRule();
        else if (e.PropertyName == nameof(DashboardViewModel.IsWideLayout))
            RefreshLayoutSignals();
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e) => RefreshLayoutSignals();

    private void RefreshLayoutSignals()
    {
        bool screenIsWide = (DataContext as DashboardViewModel)?.IsWideLayout == true;
        IsTwoColumnLayout = screenIsWide && ActualWidth >= TwoColumnMinWidth;
        IsOverviewInRow = ActualWidth >= CardsInRowMinWidth;
    }

    private void OnPeakSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPeakRule();

    /// <summary>
    /// 将峰值的相对高度换算成控件像素。换算放在 View 层，因为 ViewModel 不知道图表实际尺寸，
    /// 而标注又不能进到 Stretch=Fill 的 Viewbox 里（文字会被非等比拉伸变形）。
    /// </summary>
    private void LayoutPeakRule()
    {
        if (!IsLoaded || DataContext is not DashboardViewModel vm) return;
        var height = PeakSurface.ActualHeight;
        if (height <= 0) return;
        var center = Math.Clamp(vm.PeakLineRatio * height, RuleHalfHeight, Math.Max(RuleHalfHeight, height - RuleHalfHeight));
        Canvas.SetTop(PeakRuleHost, center - RuleHalfHeight);
        PeakRuleHost.Visibility = vm.HasPeak ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>导航锚点就是三部分本身；页脚的 ⚙ 单独直达设置折叠区，得先展开才有内容可跳。</summary>
    public void ScrollToSection(string section)
    {
        if (section == "Settings")
        {
            SettingsSection.IsExpanded = true;
            SettingsSection.BringIntoView();
            return;
        }
        FrameworkElement target = section switch { "Input" => InputPart, "Record" => RecordPart, _ => OverviewPart };
        target.BringIntoView();
    }

    private void ShowHistory(object sender, RoutedEventArgs e) => HistorySection.IsExpanded = !HistorySection.IsExpanded;

    // 入口在「输入」栏的最近按键卡片上，折叠区在「记录」栏里，两栏并排时不滚过去看不到它展开了
    private void ShowKeyDetails(object sender, RoutedEventArgs e)
    {
        KeyDetails.IsExpanded = !KeyDetails.IsExpanded;
        if (KeyDetails.IsExpanded) KeyDetails.BringIntoView();
    }
}
