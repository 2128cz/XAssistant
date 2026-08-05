namespace XAssistant.Models;

public class AppSettings
{
    public RecordingSettings Recording { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public QuickNoteSettings QuickNote { get; set; } = new();
}

public class QuickNoteSettings
{
    public string HotKey { get; set; } = "Win+Numpad0"; // 速记全局热键（默认 Win+小键盘0）
    public string? Browser { get; set; } // 浏览器偏好：chrome / edge；null 则优先 Chrome、Edge 兜底
    public string? SpaBaseUrl { get; set; } // xapp SPA 基址；null 则按构建配置默认（Debug :3009 / Release :9009）
    public string TitleMarker { get; set; } = "灵感速记"; // 捕获视图页面标题前缀，用于识别捕获窗是否已打开（与 xapp 契约）
    public int WindowWidth { get; set; } = 420;
    public int WindowHeight { get; set; } = 560;
}

public class RecordingSettings
{
    public bool AutoStartRecording { get; set; } // 鼠标点击录制自动启动
    public bool AutoStartKeyRecording { get; set; } // 键盘按键录制自动启动
}

public class GeneralSettings
{
    public bool StartMinimized { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 720;
    public bool IsLogExpanded { get; set; } = false;
}
