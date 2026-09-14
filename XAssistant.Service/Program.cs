using Serilog;
using Serilog.Events;
using XAssistant.Service;

// 日志目录跟数据库同住一处（见 UsagePaths），日志比库先要用，所以这里先建目录
var logDir = UsagePaths.LogFolder;
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logDir, "usage-tracker.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // 使用 Serilog 作为日志提供程序
    builder.Services.AddSerilog();

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "XAssistant.UsageTracker";
    });

    builder.Services.AddHostedService<UsageTrackingService>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
