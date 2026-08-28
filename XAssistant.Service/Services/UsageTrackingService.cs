using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace XAssistant.Service;

public class UsageTrackingService : BackgroundService
{
    private readonly ILogger<UsageTrackingService> _logger;
    private readonly Stopwatch _stopwatch = new();
    private long _todaySeconds;
    private DateTime _todayDate;
    private bool _isRunning;
    private readonly object _lock = new(); // 线程同步锁
    private const string DbFolder = @"C:\ProgramData\XAssistant\UsageTracker";
    private const string DbFile = "pc_usage.db";
    private static readonly string DbPath = Path.Combine(DbFolder, DbFile);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(7); // 定时保存间隔

    public UsageTrackingService(ILogger<UsageTrackingService> logger)
    {
        _logger = logger;
        _todayDate = DateTime.Today;
        _todaySeconds = LoadTodaySeconds();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        StartSession();
    }

    private void InitializeDatabase()
    {
        Directory.CreateDirectory(DbFolder);
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS DailyUsage (
                Date TEXT PRIMARY KEY,
                Seconds INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SessionEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventType TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                Date TEXT NOT NULL,
                TotalSeconds INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    // 线程安全的“获取今日总秒数”
    public long GetTodaySeconds()
    {
        lock (_lock)
        {
            return _todaySeconds + (_isRunning ? (long)_stopwatch.Elapsed.TotalSeconds : 0);
        }
    }

    private void StartSession()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                _stopwatch.Start();
                _isRunning = true;
                _logger.LogInformation("Session started.");
            }
        }
    }

    private void StopSessionAndSave()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                var elapsed = _stopwatch.Elapsed;
                _stopwatch.Stop();
                _todaySeconds += (long)elapsed.TotalSeconds;
                SaveTodaySeconds(_todaySeconds);
                _stopwatch.Reset();
                _isRunning = false;
            }
        }
    }

    private volatile bool _isSuspended;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _isSuspended = true;
            StopSessionAndSave();
            LogEvent("Suspend");
            _logger.LogInformation("System suspending, session stopped and saved.");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _isSuspended = false;
            CheckDayRollover();
            LogEvent("Resume");
            StartSession();
            _logger.LogInformation("System resumed, new session started.");
        }
    }

    private void CheckDayRollover()
    {
        lock (_lock)
        {
            var today = DateTime.Today;
            if (_todayDate != today)
            {
                _todayDate = today;
                _todaySeconds = LoadTodaySeconds(); // 新一天的初始值
                _logger.LogInformation("Day rolled over, loaded: {Seconds}s", _todaySeconds);
            }
        }
    }

    private void SaveTodaySeconds(long seconds)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DailyUsage(Date, Seconds) VALUES ($date, $seconds)
            ON CONFLICT(Date) DO UPDATE SET Seconds = $seconds
            """;
        cmd.Parameters.AddWithValue("$date", _todayDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$seconds", seconds);
        cmd.ExecuteNonQuery();
    }

    private long LoadTodaySeconds()
    {
        if (!File.Exists(DbPath))
            return 0;
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Seconds FROM DailyUsage WHERE Date = $date";
        cmd.Parameters.AddWithValue("$date", DateTime.Today.ToString("yyyy-MM-dd"));
        var result = cmd.ExecuteScalar();
        return result is long sec ? sec : 0;
    }

    /// <summary>
    /// 向 SessionEvents 表插入一条事件记录
    /// </summary>
    /// <param name="eventType">事件类型，如 Start、Suspend、Resume、Stop</param>
    private void LogEvent(string eventType)
    {
        try
        {
            var now = DateTime.Now;
            var totalSeconds = GetTodaySeconds(); // 线程安全调用

            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SessionEvents (EventType, Timestamp, Date, TotalSeconds)
                VALUES ($type, $timestamp, $date, $seconds)
                """;
            cmd.Parameters.AddWithValue("$type", eventType);
            cmd.Parameters.AddWithValue("$timestamp", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$date", now.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$seconds", totalSeconds);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // 记录事件失败不应影响主流程
            _logger.LogWarning(ex, "Failed to log event {EventType}", eventType);
        }
    }

    // ---- 后台主循环：专门处理命名管道请求 ----
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 确保数据库及表结构存在
        InitializeDatabase();

        // 如果需要，处理跨天
        CheckDayRollover();
        if (!_isRunning)
        {
            StartSession();
        }

        LogEvent("ServiceStarted");

        _logger.LogInformation(
            "Service started. Listening on named pipe with periodic save every {Interval}s.",
            SaveInterval.TotalSeconds
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                "UsageTrackerPipe",
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous
            );

            // 绑定一个可取消的 token，用于定时中断等待
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            var connectionTask = server.WaitForConnectionAsync(cts.Token);
            var delayTask = Task.Delay(SaveInterval, stoppingToken);

            // 谁先完成
            var completed = await Task.WhenAny(connectionTask, delayTask);

            if (completed == connectionTask)
            {
                // 管道连接成功
                try
                {
                    var seconds = GetTodaySeconds();
                    var data = Encoding.UTF8.GetBytes(seconds.ToString());
                    await server.WriteAsync(data, 0, data.Length, stoppingToken);
                    await server.FlushAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to respond to pipe client");
                }
            }
            else
            {
                // 定时保存触发，取消未完成的连接等待
                cts.Cancel();
                try
                {
                    await connectionTask; // 如果没抛异常，说明连接成功！
                    // 补救：发送当前数据
                    var seconds = GetTodaySeconds();
                    var data = Encoding.UTF8.GetBytes(seconds.ToString());
                    await server.WriteAsync(data, 0, data.Length, stoppingToken);
                    await server.FlushAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // 连接确实被取消了，无需处理
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "补救回复失败");
                }

                // 正常执行定时保存
                StopSessionAndSave(); // 将秒表累积写入数据库
                CheckDayRollover(); // 检查跨天
                if (!_isSuspended)
                {
                    StartSession();
                }
                else
                {
                    _logger.LogDebug("Skipping session start because system is suspended.");
                }
            }

            server.Dispose();
        }
    }

    public override void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopSessionAndSave();
        LogEvent("ServiceStopped");
        base.Dispose();
    }
}
