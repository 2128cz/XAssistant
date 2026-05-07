namespace XAssistant.Models;

public class AppUsageItem
{
    public string ProcessName { get; set; } = string.Empty;
    public long TotalSeconds { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public string FormattedUsage => FormatSeconds(TotalSeconds);
    public string DisplayName => char.ToUpper(ProcessName[0]) + ProcessName[1..];

    public string FormattedStartTime => StartTime?.ToString("HH:mm:ss") ?? "--";
    public string FormattedEndTime => EndTime?.ToString("HH:mm:ss") ?? "--";

    private static string FormatSeconds(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
