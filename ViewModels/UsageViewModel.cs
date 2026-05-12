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
    private DateTime? _activeSessionStartTime;
    private long _todayCorrectedSeconds; // 根据事件时间戳计算的今日总秒数

    private volatile bool _isRefreshing;

    [ObservableProperty]
    private ObservableCollection<SessionEvent> _sessionEvents = new();

    public UsageViewModel(ILogger<UsageViewModel> logger)
    {
        _logger = logger;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_isRefreshing)
                return;
            _isRefreshing = true;
            try
            {
                await RefreshTodayAsync();
                await LoadHistoryAsync();
                await LoadSessionEventsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "定时刷新数据失败");
            }
            finally
            {
                _isRefreshing = false;
            }
        };
        _refreshTimer.Start();
        _ = RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        _logger.LogInformation("开始刷新全部数据");
        await RefreshTodayAsync();
        await LoadHistoryAsync();
        await LoadSessionEventsAsync();
    }

    private async Task<long> RefreshTodayAsync()
    {
        try
        {
            // 从管道获取服务端缓存值（可能不准）
            var pipeSeconds = await GetTodaySecondsFromPipeAsync();

            // 基于事件时间戳计算今日实际使用时长
            var eventSeconds = _todayCorrectedSeconds;
            if (_activeSessionStartTime.HasValue)
            {
                eventSeconds += (long)(DateTime.Now - _activeSessionStartTime.Value).TotalSeconds;
            }

            // 如果管道值和事件计算值差距大于5秒，记录警告并优先使用事件计算值
            // if (Math.Abs(pipeSeconds - eventSeconds) > 5)
            // {
            //     _logger.LogWarning(
            //         "今日使用时长不一致：管道 {Pipe}s vs 事件计算 {Event}s，采用事件计算值",
            //         pipeSeconds,
            //         eventSeconds
            //     );
            // }

            var ts = TimeSpan.FromSeconds(eventSeconds);
            TodayUsageText =
                ts.TotalDays >= 1
                    ? $"{(int)ts.TotalDays} 天 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

            return eventSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "管道获取今日秒数失败，回退到数据库");
            try
            {
                var dbSeconds = LoadTodaySecondsFromDb();
                var ts = TimeSpan.FromSeconds(dbSeconds);
                TodayUsageText =
                    $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} (数据库)";
                return dbSeconds;
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "数据库读取今日秒数也失败");
                TodayUsageText = "无法获取";
                return 0;
            }
        }
    }

    private async Task LoadHistoryAsync()
    {
        var list = new ObservableCollection<DailyUsage>();
        await Task.Run(() =>
        {
            try
            {
                // 1. 读取数据库原始记录
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT Date, Seconds FROM DailyUsage ORDER BY Date DESC LIMIT 30";
                var dbRecords = new List<DailyUsage>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        dbRecords.Add(
                            new DailyUsage
                            {
                                Date = reader.GetString(0),
                                Seconds = reader.GetInt64(1),
                            }
                        );
                    }
                }

                // 2. 从事件时间戳计算每一天的实际总秒数（已完成段）
                var eventDailySeconds = new Dictionary<string, long>();
                cmd.CommandText =
                    @"
                    SELECT Id, EventType, Timestamp, Date
                    FROM SessionEvents
                    ORDER BY Id ASC";
                using (var reader = cmd.ExecuteReader())
                {
                    DateTime? segmentStart = null;
                    foreach (var row in reader.Cast<System.Data.Common.DbDataRecord>())
                    {
                        var eventType = reader.GetString(1);
                        var timestampStr = reader.GetString(2);
                        var date = reader.GetString(3);
                        if (!DateTime.TryParse(timestampStr, out var currTime))
                            continue;

                        if (eventType == "ServiceStarted" || eventType == "Resume")
                        {
                            segmentStart = currTime;
                        }
                        else if (
                            (eventType == "Suspend" || eventType == "ServiceStopped")
                            && segmentStart.HasValue
                        )
                        {
                            var segmentSeconds = (long)(currTime - segmentStart.Value).TotalSeconds;
                            if (!eventDailySeconds.ContainsKey(date))
                                eventDailySeconds[date] = 0;
                            eventDailySeconds[date] += segmentSeconds;
                            segmentStart = null;
                        }
                    }
                }

                // 3. 对比并填充修正值
                foreach (var record in dbRecords)
                {
                    if (eventDailySeconds.TryGetValue(record.Date, out var corrected))
                    {
                        record.CorrectedSeconds = corrected;
                    }
                    list.Add(record);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台加载历史记录失败");
            }
        });
        History = list;
    }

    private async Task LoadSessionEventsAsync()
    {
        var events = new ObservableCollection<SessionEvent>();
        SessionEvent? activeStart = null;
        DateTime? activeStartTime = null;
        var dailyCorrectedSeconds = new Dictionary<string, long>();

        await Task.Run(() =>
        {
            var rawList = new List<SessionEvent>();
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText =
                    @"
                SELECT Id, EventType, Timestamp, Date, TotalSeconds
                FROM SessionEvents
                ORDER BY Id ASC";
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

                // _logger.LogInformation("从数据库读取到 {Count} 条事件记录", rawList.Count);

                // 遍历事件，用时间戳计算每个段的时长，并累加每日总秒数
                SessionEvent? segmentStartEvent = null;
                DateTime? segmentStartTime = null;

                for (int i = 0; i < rawList.Count; i++)
                {
                    var evt = rawList[i];
                    if (!DateTime.TryParse(evt.Timestamp, out DateTime currTime))
                    {
                        // _logger.LogWarning(
                        //     "事件 {Id} 的时间戳无法解析: {Timestamp}",
                        //     evt.Id,
                        //     evt.Timestamp
                        // );
                        continue;
                    }

                    if (evt.EventType == "ServiceStarted" || evt.EventType == "Resume")
                    {
                        segmentStartEvent = evt;
                        segmentStartTime = currTime;
                        evt.FormattedCumulativeUsage = evt.EventType == "Resume" ? "…" : "";
                        // _logger.LogDebug(
                        //     "会话段开始: 类型={Type}, 时间={Time}, ID={Id}",
                        //     evt.EventType,
                        //     evt.Timestamp,
                        //     evt.Id
                        // );
                    }
                    else if (evt.EventType == "Suspend" || evt.EventType == "ServiceStopped")
                    {
                        if (segmentStartEvent != null && segmentStartTime.HasValue)
                        {
                            long segmentSeconds = (long)
                                (currTime - segmentStartTime.Value).TotalSeconds;

                            // _logger.LogInformation(
                            //     "会话段结束: 开始={StartTime}, 结束={EndTime}, 日期={Date}, 段秒数={SegmentSeconds}s",
                            //     segmentStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                            //     currTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            //     evt.Date,
                            //     segmentSeconds
                            // );

                            // 累加到当日校正总秒数
                            string dateKey = evt.Date;
                            if (!dailyCorrectedSeconds.ContainsKey(dateKey))
                                dailyCorrectedSeconds[dateKey] = 0;
                            dailyCorrectedSeconds[dateKey] += segmentSeconds;

                            // _logger.LogDebug(
                            //     "日期 {Date} 累计秒数更新为 {Total}s",
                            //     dateKey,
                            //     dailyCorrectedSeconds[dateKey]
                            // );

                            evt.FormattedCumulativeUsage = FormatSeconds(segmentSeconds);
                            if (segmentStartEvent.EventType == "Resume")
                                segmentStartEvent.FormattedCumulativeUsage = FormatSeconds(
                                    segmentSeconds
                                );

                            segmentStartEvent = null;
                            segmentStartTime = null;
                        }
                        else
                        {
                            // _logger.LogWarning(
                            //     "结束事件 {Id}（类型={Type}）没有匹配的开始事件",
                            //     evt.Id,
                            //     evt.EventType
                            // );
                            evt.FormattedCumulativeUsage = FormatSeconds(0);
                        }
                    }
                }

                // 处理末尾未结束的活跃段
                if (segmentStartEvent != null && segmentStartTime.HasValue)
                {
                    long activeSeconds = (long)(DateTime.Now - segmentStartTime.Value).TotalSeconds;

                    // _logger.LogInformation(
                    //     "存在未结束的活跃段: 开始时间={StartTime}, 当前实时秒数={ActiveSeconds}s",
                    //     segmentStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                    //     activeSeconds
                    // );

                    // 注意：不再累加到 dailyCorrectedSeconds，活跃部分在 RefreshTodayAsync 中实时计算
                    segmentStartEvent.FormattedCumulativeUsage = FormatSeconds(activeSeconds);
                    activeStart = segmentStartEvent;
                    activeStartTime = segmentStartTime;
                }
                else
                {
                    // _logger.LogInformation("没有未结束的活跃段");
                    activeStart = null;
                    activeStartTime = null;
                }

                // 计算相邻事件间隔
                for (int i = 0; i < rawList.Count; i++)
                {
                    if (i == 0)
                        rawList[i].TimeSincePrevious = "-";
                    else
                    {
                        if (
                            DateTime.TryParse(rawList[i - 1].Timestamp, out var prevTime)
                            && DateTime.TryParse(rawList[i].Timestamp, out var currTime)
                        )
                        {
                            rawList[i].TimeSincePrevious = FormatTimeSpan(currTime - prevTime);
                        }
                        else
                        {
                            rawList[i].TimeSincePrevious = "?";
                        }
                    }
                }

                // 倒序加入集合
                for (int i = rawList.Count - 1; i >= 0; i--)
                    events.Add(rawList[i]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台加载会话事件失败");
            }
        });

        SessionEvents = events;
        _activeSessionStartEvent = activeStart;
        _activeSessionStartTime = activeStartTime;
        _todayCorrectedSeconds = dailyCorrectedSeconds.TryGetValue(
            DateTime.Now.ToString("yyyy-MM-dd"),
            out var todaySec
        )
            ? todaySec
            : 0;

        // _logger.LogInformation(
        //     "加载会话事件完成：今日已完成会话秒数={CompletedSeconds}s, 活跃会话开始时间={ActiveStart}, 活跃实时秒数={ActiveSeconds}s",
        //     _todayCorrectedSeconds,
        //     activeStartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "无",
        //     activeStartTime.HasValue ? (long)(DateTime.Now - activeStartTime.Value).TotalSeconds : 0
        // );
    }

    private static string FormatSeconds(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

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
