using System.Collections.ObjectModel;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using XAssistant.Models;

namespace XAssistant.ViewModels;

public partial class UsageViewModel : ViewModelBase
{
    private const string DbPath = @"C:\ProgramData\XAssistant\UsageTracker\pc_usage.db";
    private const string PipeName = "UsageTrackerPipe";
    private readonly DispatcherTimer _refreshTimer;
    private readonly ILogger<UsageViewModel> _logger;

    [ObservableProperty]
    private string _todayUsageText = "00:00:00";

    [ObservableProperty]
    private ObservableCollection<DailyUsage> _history = new();
    private SessionEvent? _activeSessionStartEvent;

    // 会话事件集合
    [ObservableProperty]
    private ObservableCollection<SessionEvent> _sessionEvents = new();

    public UsageViewModel(ILogger<UsageViewModel> logger)
    {
        _logger = logger;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            // 1. 刷新今日使用时长（UI 上的大字）
            await RefreshTodayAsync();

            // 2. 如果有活跃的会话段，更新其累计时长
            if (_activeSessionStartEvent != null)
            {
                // 当前总有效秒数 - 该事件发生时的总秒数 = 本段已使用秒数
                long currentTotalSeconds = await TryGetCurrentTotalSecondsAsync();
                long segmentSeconds = currentTotalSeconds - _activeSessionStartEvent.TotalSeconds;
                _activeSessionStartEvent.FormattedCumulativeUsage = FormatSeconds(segmentSeconds);
            }
        };
        _refreshTimer.Start();
        _ = RefreshAllAsync();
    }

    // 辅助方法：安全获取当前总有效秒数（优先管道，失败则本地数据库）
    private async Task<long> TryGetCurrentTotalSecondsAsync()
    {
        try
        {
            var seconds = await GetTodaySecondsFromPipeAsync();
            return seconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "管道获取失败，尝试本地数据库");
            try
            {
                return LoadTodaySecondsFromDb();
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "数据库读取今日秒数失败");
                return 0;
            }
        }
    }

    private async Task RefreshAllAsync()
    {
        _logger.LogInformation("开始刷新全部数据");
        await RefreshTodayAsync();
        LoadHistory();
        LoadSessionEvents(); // 新增
    }

    private async Task<long> RefreshTodayAsync()
    {
        try
        {
            var seconds = await GetTodaySecondsFromPipeAsync();
            var ts = TimeSpan.FromSeconds(seconds);
            TodayUsageText =
                ts.TotalDays >= 1
                    ? $"{(int)ts.TotalDays} 天 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return seconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "管道获取今日秒数失败，回退到数据库");
            try
            {
                var seconds = LoadTodaySecondsFromDb();
                var ts = TimeSpan.FromSeconds(seconds);
                TodayUsageText =
                    $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} (数据库)";
                return seconds;
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "数据库读取今日秒数也失败");
                TodayUsageText = "无法获取";
                return 0;
            }
        }
    }

    private void LoadHistory()
    {
        var list = new ObservableCollection<DailyUsage>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Date, Seconds FROM DailyUsage ORDER BY Date DESC LIMIT 30";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(
                    new DailyUsage { Date = reader.GetString(0), Seconds = reader.GetInt64(1) }
                );
            }
            _logger.LogDebug("加载历史记录完成，共 {Count} 条", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载历史记录失败");
        }
        History = list;
    }

    // 加载最近的事件记录（例如最近 50 条）
    private void LoadSessionEvents()
    {
        var events = new ObservableCollection<SessionEvent>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"
            SELECT Id, EventType, Timestamp, Date, TotalSeconds
            FROM SessionEvents
            ORDER BY Id ASC"; // 升序，保证时间顺序
            var rawList = new List<SessionEvent>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rawList.Add(
                    new SessionEvent
                    {
                        Id = reader.GetInt32(0),
                        EventType = reader.GetString(1),
                        Timestamp = reader.GetString(2),
                        Date = reader.GetString(3),
                        TotalSeconds = reader.GetInt64(4),
                    }
                );
            }

            // ---------- 按会话段计算每个事件的累计时长 ----------
            SessionEvent? segmentStartEvent = null; // 当前段的开始事件
            DateTime? segmentStartTime = null;
            long segmentStartTotalSeconds = 0;

            for (int i = 0; i < rawList.Count; i++)
            {
                var evt = rawList[i];
                if (!DateTime.TryParse(evt.Timestamp, out DateTime currTime))
                    continue;

                // 新的一段开始
                if (evt.EventType == "ServiceStarted" || evt.EventType == "Resume")
                {
                    // 新段开始
                    // 如果之前有未结束的段，先忽略（正常数据不会出现）
                    segmentStartEvent = evt;
                    segmentStartTime = currTime;
                    segmentStartTotalSeconds = evt.TotalSeconds;

                    // 只有 Resume 才需要显示累计时长（ServiceStarted 永远不显示）
                    evt.FormattedCumulativeUsage = evt.EventType == "Resume" ? "…" : "";
                }
                else if (evt.EventType == "Suspend" || evt.EventType == "ServiceStopped")
                {
                    if (segmentStartEvent != null && segmentStartTime.HasValue)
                    {
                        long segmentSeconds = evt.TotalSeconds - segmentStartTotalSeconds;
                        string formatted = FormatSeconds(segmentSeconds);

                        // 结束事件始终显示该段时长
                        evt.FormattedCumulativeUsage = formatted;

                        // 开始事件：只有 Resume 才显示，ServiceStarted 留空
                        segmentStartEvent.FormattedCumulativeUsage =
                            segmentStartEvent.EventType == "Resume" ? formatted : "";

                        segmentStartEvent = null;
                        segmentStartTime = null;
                    }
                    else
                    {
                        evt.FormattedCumulativeUsage = FormatSeconds(0);
                    }
                }
            }

            // 如果遍历完后还有未结束的段（当前正在活跃的会话）
            if (segmentStartEvent != null)
            {
                // 活跃段：用当前系统时间 - 段开始时间 计算即时时长
                long activeSeconds = (long)(DateTime.Now - segmentStartTime!.Value).TotalSeconds;
                segmentStartEvent.FormattedCumulativeUsage = FormatSeconds(activeSeconds);
                // 标记为活跃段，留一个引用供定时器更新（见后文）
                _activeSessionStartEvent = segmentStartEvent;
            }
            else
            {
                _activeSessionStartEvent = null; // 没有活跃段
            }

            // ---------- 计算相邻事件间隔 ----------
            for (int i = 0; i < rawList.Count; i++)
            {
                if (i == 0)
                    rawList[i].TimeSincePrevious = "-";
                else
                {
                    var prevTime = DateTime.Parse(rawList[i - 1].Timestamp);
                    var currTime = DateTime.Parse(rawList[i].Timestamp);
                    rawList[i].TimeSincePrevious = FormatTimeSpan(currTime - prevTime);
                }
            }

            // 倒序，最新在最上面
            for (int i = rawList.Count - 1; i >= 0; i--)
                events.Add(rawList[i]);

            SessionEvents = events;

            _logger.LogDebug("加载会话事件完成，共 {Count} 条", events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载会话事件失败");
        }
    }

    // 工具方法：格式化秒数
    private static string FormatSeconds(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // 格式化时间差为可读字符串
    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60)
            return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        if (ts.TotalHours < 24)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
    }

    private static async Task<long> GetTodaySecondsFromPipeAsync()
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In);
        await client.ConnectAsync(2000);
        var buffer = new byte[256];
        var bytesRead = await client.ReadAsync(buffer, 0, buffer.Length);
        var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        return long.Parse(data);
    }

    private static long LoadTodaySecondsFromDb()
    {
        if (!System.IO.File.Exists(DbPath))
            return 0;

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Seconds FROM DailyUsage WHERE Date = $date";
        cmd.Parameters.AddWithValue("$date", DateTime.Today.ToString("yyyy-MM-dd"));
        var result = cmd.ExecuteScalar();
        return result is long sec ? sec : 0;
    }
}
