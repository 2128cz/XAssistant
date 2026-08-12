namespace XAssistant.Models;

public class DailyUsage
{
    public string Date { get; set; } = string.Empty;
    public long Seconds { get; set; }

    // 根据事件时间戳校正后的秒数（仅当有事件数据时存在）
    public long? CorrectedSeconds { get; set; }

    // 修正原因（如跨午夜会话拆分、未配对事件、计时差异）
    public string? CorrectionReason { get; set; }

    // 是否存在明显差异（比如超过5秒）
    public bool HasDiscrepancy =>
        CorrectedSeconds.HasValue && Math.Abs(Seconds - CorrectedSeconds.Value) > 5;

    // 原始记录格式化
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

    // 校正后的格式化时长（如果有校正值则显示，否则为空）
    public string? FormattedCorrectedTime
    {
        get
        {
            if (!CorrectedSeconds.HasValue)
                return null;
            var ts = TimeSpan.FromSeconds(CorrectedSeconds.Value);
            return ts.TotalDays >= 1
                ? $"{(int)ts.TotalDays} 天 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    // UI 可绑定此属性来显示差异提示或背景色
    public string DiscrepancyTooltip =>
        HasDiscrepancy
            ? $"原始记录：{FormattedTime}\n修正值：{FormattedCorrectedTime}"
            : string.Empty;
}
