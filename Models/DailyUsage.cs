namespace XAssistant.Models;

public class DailyUsage
{
    public string Date { get; set; } = string.Empty;
    public long Seconds { get; set; }

    public string FormattedTime
    {
        get
        {
            var ts = TimeSpan.FromSeconds(Seconds);
            return ts.TotalDays >= 1
                ? $"{(int)ts.TotalDays} 天 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }
}
