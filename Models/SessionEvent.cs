using CommunityToolkit.Mvvm.ComponentModel;

namespace XAssistant.Models;

public partial class SessionEvent : ObservableObject
{
    public int Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public long TotalSeconds { get; set; }

    [ObservableProperty]
    private string _timeSincePrevious = string.Empty;

    [ObservableProperty]
    private string _formattedCumulativeUsage = string.Empty;
}
