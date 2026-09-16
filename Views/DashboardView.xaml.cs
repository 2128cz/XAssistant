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
    /// 行内分栏的页面宽度下限。右栏钉死 340，主体格至少要再剩 566：
    /// 六格速率两列一排（每格 163 装得下 44 号读数）、练习句一行、词频面板三个读数并排，都只到这个口径。
    /// 热力图行也用这一档：键盘会被挤窄到设计稿的一半，但键帽名靠 KeyboardHeatmap 的字号补偿拉回可读，
    /// 所以不必再为它单立一个宽得多的下限（那等于让它在常见窗口宽度上整行独占、白占一屏高）。
    /// </summary>
    private const double RailSplitMinWidth = 940;

    /// <summary>顶部四张统计卡排成一排的下限；再窄就把 58 号的缩写挤到裁字，改成两列两张。</summary>
    private const double CardsInRowMinWidth = 1000;

    /// <summary>
    /// 「横向 · 分栏」档位只保留这条硬下限：340 的右栏加主体不塌掉的最小值。
    /// 这一档是给「我就是要把读数放右边」的显式选择用的，越过它列就只剩几 px，不是排版而是裁切。
    /// </summary>
    private const double RailHardMinWidth = 700;

    private INotifyPropertyChanged? _observed;

    public static readonly DependencyProperty IsRailSplitProperty = DependencyProperty.Register(
        nameof(IsRailSplit), typeof(bool), typeof(DashboardView), new PropertyMetadata(false));

    /// <summary>
    /// 每一行是否切成「主体 + 右栏」：节奏、热力图、练习、记录四行共用这一档判据。
    /// 不区分横竖屏，也不让某一行单独退回整行独占——那就是浪费空间的来源。
    /// </summary>
    public bool IsRailSplit
    {
        get => (bool)GetValue(IsRailSplitProperty);
        private set => SetValue(IsRailSplitProperty, value);
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
        else if (e.PropertyName is nameof(DashboardViewModel.AllowsRailSplit) or nameof(DashboardViewModel.PrefersWideRail))
            RefreshLayoutSignals();
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e) => RefreshLayoutSignals();

    private void RefreshLayoutSignals()
    {
        // 只看页面实际宽度，不看显示器横竖屏：竖屏 1080 的页面照样放得下 340 的读数栏，
        // 而「屏幕比例一票否决」曾把 1100 宽的窗口整页摊平。唯一的否决权留给设置里的「纵向 · 单栏」。
        if (DataContext is not DashboardViewModel vm) return;
        double floor = vm.PrefersWideRail ? RailHardMinWidth : RailSplitMinWidth;
        IsRailSplit = vm.AllowsRailSplit && ActualWidth >= floor;
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

    // 入口在「输入」板的右栏（最近按键），折叠区在下一块「记录」板里，不滚过去看不到它展开了
    private void ShowKeyDetails(object sender, RoutedEventArgs e)
    {
        KeyDetails.IsExpanded = !KeyDetails.IsExpanded;
        if (KeyDetails.IsExpanded) KeyDetails.BringIntoView();
    }
}
