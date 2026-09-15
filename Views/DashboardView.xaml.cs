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
    /// 通用行内分栏的页面宽度下限。右栏钉死 340，主体格至少要再剩 566：
    /// 六格速率两列一排（每格 163 装得下 44 号读数）、练习句一行、词频面板三个读数并排，都只到这个口径。
    /// </summary>
    private const double RailSplitMinWidth = 940;

    /// <summary>
    /// 热力图那一行另立一档下限：键盘是 1120 的设计稿，键帽名 10 号，等比缩到八成以下就掉进 8 px 读不动。
    /// 反推主体格 ≥ 896 + 控件自身的内边距与边框 42 = 938，加右栏 340 与格的左右留白 34，就是 1312。
    /// 低于这个宽度，键盘独占整行，排行与最近按键摊平成键盘下方的一条带。
    /// </summary>
    private const double HeatRailMinWidth = 1312;

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
    /// 除热力图以外的每一行是否切成「主体 + 右栏」：节奏行、练习行、记录行共用这一档判据。
    /// </summary>
    public bool IsRailSplit
    {
        get => (bool)GetValue(IsRailSplitProperty);
        private set => SetValue(IsRailSplitProperty, value);
    }

    public static readonly DependencyProperty IsHeatRailSplitProperty = DependencyProperty.Register(
        nameof(IsHeatRailSplit), typeof(bool), typeof(DashboardView), new PropertyMetadata(false));

    /// <summary>
    /// 热力图那一行是否切成「键盘 + 右栏」。判据比上一档严得多：整页只有这一块的主体是被缩放的 1120 设计稿，
    /// 右边塞进 340 的读数栏就要让出键帽的可读性，够不够宽只看这一行自己。
    /// </summary>
    public bool IsHeatRailSplit
    {
        get => (bool)GetValue(IsHeatRailSplitProperty);
        private set => SetValue(IsHeatRailSplitProperty, value);
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
        double heatFloor = vm.PrefersWideRail ? RailHardMinWidth : HeatRailMinWidth;
        IsRailSplit = vm.AllowsRailSplit && ActualWidth >= floor;
        IsHeatRailSplit = vm.AllowsRailSplit && ActualWidth >= heatFloor;
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
