namespace XAssistant.Service;

/// <summary>
/// 后台服务落盘位置的约定。这几个路径是服务与主程序之间的跨进程契约，不是随手挑的目录：
/// 主程序读 pc_usage.db 用的是主仓库 `Services/AppDataPathHelper.GetUsageTrackerFolder()`，
/// 两边必须算出同一个路径，改一处就要同步另一处。
///
/// 取 CommonApplicationData 而不是写死 C:\ProgramData：盘符与目录重定向都跟着系统走。
/// 这里刻意不能改成相对路径——Windows 服务的当前目录是 System32，相对路径会把库写进系统目录（且多半没有写权限）；
/// 也不能改用 %APPDATA%：服务以 LocalSystem 身份运行，它的 ApplicationData 与登录用户的并不是同一个。
/// </summary>
internal static class UsagePaths
{
    /// <summary>数据目录。由服务建库时创建；主程序只读，不创建。</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "XAssistant",
        "UsageTracker");

    /// <summary>使用时长库的文件名，与主程序读的那份同名。</summary>
    public const string DatabaseFileName = "pc_usage.db";

    /// <summary>使用时长库的完整路径。</summary>
    public static string Database { get; } = Path.Combine(Folder, DatabaseFileName);

    /// <summary>Serilog 按天滚动的日志目录。</summary>
    public static string LogFolder { get; } = Path.Combine(Folder, "logs");
}
