namespace XAssistant.Models;

public class AppSettings
{
    public MouseRecordingSettings Recording { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

public class MouseRecordingSettings
{
    public bool AutoStartRecording { get; set; }
}

public class GeneralSettings
{
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }   // 开机自启
}