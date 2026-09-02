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
    public string? ConnectionString { get; set; } // 速记库 PostgreSQL 连接串（复制自 xapp .env 的 DATABASE_URL，直插 quick_notes 表）；为空时自动读取 DatabaseUrlEnvPath 指向的 .env
    public string? DatabaseUrlEnvPath { get; set; } // xapp .env 路径；ConnectionString 未配置时自动读取其 DATABASE_URL（默认 C:\xapp-2026-06-30\.env）
    public string? SpaBaseUrl { get; set; } // xapp SPA 基址；null 则按构建配置默认（Debug :3009 / Release :9009）
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
    public string Theme { get; set; } = "Light"; // 界面主题：Light / Dark
}
