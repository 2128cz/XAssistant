using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using XAssistant.ViewModels;

namespace XAssistant.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    /// <summary>峰值线宿主高度的一半，用于防止线与两端标注贴边被裁。</summary>
    private const double RuleHalfHeight = 13;

    private INotifyPropertyChanged? _observed;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveViewModel();
        Loaded += (_, _) => LayoutPeakRule();
    }

    private void ObserveViewModel()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnViewModelPropertyChanged;
        _observed = DataContext as INotifyPropertyChanged;
        if (_observed is not null) _observed.PropertyChanged += OnViewModelPropertyChanged;
        LayoutPeakRule();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.PeakLineRatio) or nameof(DashboardViewModel.HasPeak))
            LayoutPeakRule();
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

    public void ScrollToSection(string section)
    {
        FrameworkElement target = section switch { "Analysis" => SessionSection, "Settings" => SettingsSection, _ => OverviewSection };
        if (section == "Settings") SettingsSection.IsExpanded = true;
        target.BringIntoView();
    }
    private void ShowHistory(object sender, RoutedEventArgs e) => HistorySection.IsExpanded = !HistorySection.IsExpanded;
    private void ShowKeyDetails(object sender, RoutedEventArgs e) => KeyDetails.IsExpanded = !KeyDetails.IsExpanded;
}
