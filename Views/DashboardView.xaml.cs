using System.Windows;
using System.Windows.Controls;

namespace XAssistant.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    public DashboardView() => InitializeComponent();
    public void ScrollToSection(string section)
    {
        FrameworkElement target = section switch { "Analysis" => SessionSection, "Settings" => SettingsSection, _ => OverviewSection };
        if (section == "Settings") SettingsSection.IsExpanded = true;
        target.BringIntoView();
    }
    private void ShowHistory(object sender, RoutedEventArgs e) => HistorySection.IsExpanded = !HistorySection.IsExpanded;
    private void ShowKeyDetails(object sender, RoutedEventArgs e) => KeyDetails.IsExpanded = !KeyDetails.IsExpanded;
}
