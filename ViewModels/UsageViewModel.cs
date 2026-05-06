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

    private volatile bool _isRefreshing;

    // 会话事件集合
    [ObservableProperty]
    private ObservableCollection<SessionEvent> _sessionEvents = new();

    public UsageViewModel(ILogger<UsageViewModel> logger)
    {
        _logger = logger;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            // 防止上一次刷新还未结束就再次触发
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

    private async Task LoadHistoryAsync()
    {
        var list = new ObservableCollection<DailyUsage>();
        await Task.Run(() =>
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT Date, Seconds FROM DailyUsage ORDER BY Date DESC LIMIT 30";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(
                        new DailyUsage { Date = reader.GetString(0), Seconds = reader.GetInt64(1) }
                    );
                }
                // _logger.LogDebug("后台加载历史记录完成，共 {Count} 条", list.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台加载历史记录失败");
            }
        });
        History = list;
    }

    // 加载最近的事件记录（例如最近 50 条）在后台线程读取数据库，回到 UI 线程更新集合
    private async Task LoadSessionEventsAsync()
    {
        var events = new ObservableCollection<SessionEvent>();
        SessionEvent? activeStart = null;

        await Task.Run(() =>
        {
            // 从数据库读取原始数据
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

                // 计算每个事件的累计时长、间隔等（原 LoadSessionEvents 中的逻辑）
                SessionEvent? segmentStartEvent = null;
                DateTime? segmentStartTime = null;
                long segmentStartTotalSeconds = 0;

                for (int i = 0; i < rawList.Count; i++)
                {
                    var evt = rawList[i];
                    if (!DateTime.TryParse(evt.Timestamp, out DateTime currTime))
                        continue;

                    if (evt.EventType == "ServiceStarted" || evt.EventType == "Resume")
                    {
                        segmentStartEvent = evt;
                        segmentStartTime = currTime;
                        segmentStartTotalSeconds = evt.TotalSeconds;
                        evt.FormattedCumulativeUsage = evt.EventType == "Resume" ? "…" : "";
                    }
                    else if (evt.EventType == "Suspend" || evt.EventType == "ServiceStopped")
                    {
                        if (segmentStartEvent != null && segmentStartTime.HasValue)
                        {
                            long segmentSeconds = evt.TotalSeconds - segmentStartTotalSeconds;
                            string formatted = FormatSeconds(segmentSeconds);
                            evt.FormattedCumulativeUsage = formatted;
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

                // 如果末尾还有未结束的段（当前活跃会话）
                if (segmentStartEvent != null)
                {
                    long activeSeconds = (long)
                        (DateTime.Now - segmentStartTime!.Value).TotalSeconds;
                    segmentStartEvent.FormattedCumulativeUsage = FormatSeconds(activeSeconds);
                    activeStart = segmentStartEvent;
                }
                else
                {
                    activeStart = null;
                }

                // 计算相邻事件间隔
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

                // 倒序加入集合
                for (int i = rawList.Count - 1; i >= 0; i--)
                    events.Add(rawList[i]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台加载会话事件失败");
            }
        });

        // 回到 UI 线程更新绑定源
        SessionEvents = events;
        _activeSessionStartEvent = activeStart;
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
