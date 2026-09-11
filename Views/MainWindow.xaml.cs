using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XAssistant.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void NavigateSection(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string section }) DashboardSurface.ScrollToSection(section);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsShuttingDown) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
