using System.Collections.ObjectModel;
using System.IO;
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

    private long? _todayUsageSeconds;
    private bool _isTodayUsageCorrected;

    /// <summary>Numeric value underlying TodayUsageText; null until available or after a read failure.</summary>
    public long? TodayUsageSeconds => _todayUsageSeconds;

    /// <summary>True for the event-based value including the active session; false for database fallback.</summary>
    public bool IsTodayUsageCorrected => _isTodayUsageCorrected;

    [ObservableProperty]
    private ObservableCollection<DailyUsage> _history = new();

    private SessionEvent? _activeSessionStartEvent;
    private DateTime? _activeSessionStartTime;
    private long _todayCorrectedSeconds; // 根据事件时间戳计算的今日总秒数

    private volatile bool _isRefreshing;

    /// <summary>上一次管道可用性；仅在状态跳变时记日志，不每 5 秒刷一条</summary>
    private bool? _pipeAvailable;

    /// <summary>后台服务数据库是否存在；同样只在状态跳变时记日志</summary>
    private bool? _serviceInstalled;

    [ObservableProperty]
    private ObservableCollection<SessionEvent> _sessionEvents = new();

    public UsageViewModel(ILogger<UsageViewModel> logger, bool startMonitoring = true)
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
        if (startMonitoring)
        {
            _refreshTimer.Start();
            _ = RefreshAllAsync();
        }
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
        // 从管道获取服务端缓存值（可能不准）；服务未启动是常态，用返回值表达失败而不是抛异常
        long? pipeSeconds = await Task.Run(TryReadPipeSeconds);
        LogPipeStateChange(pipeSeconds.HasValue);
        if (!pipeSeconds.HasValue)
            return FallbackToDatabase();

        try
        {
            // 基于事件时间戳计算今日实际使用时长
            var eventSeconds = _todayCorrectedSeconds;
            if (_activeSessionStartTime.HasValue)
            {
                eventSeconds += (long)(DateTime.Now - _activeSessionStartTime.Value).TotalSeconds;
            }

            var ts = TimeSpan.FromSeconds(eventSeconds);
            SetTodayUsageSeconds(eventSeconds, isCorrected: true);
            TodayUsageText =
                ts.TotalDays >= 1
                    ? $"{(int)ts.TotalDays} 天 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

            return eventSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "事件计算今日时长失败，回退到数据库");
            return FallbackToDatabase();
        }
    }

    private long FallbackToDatabase()
    {
        try
        {
            var dbSeconds = LoadTodaySecondsFromDb();
            var ts = TimeSpan.FromSeconds(dbSeconds);
            SetTodayUsageSeconds(dbSeconds, isCorrected: false);
            TodayUsageText = $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} (数据库)";
            return dbSeconds;
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx, "数据库读取今日秒数也失败");
            SetTodayUsageSeconds(null, isCorrected: false);
            TodayUsageText = "无法获取";
            return 0;
        }
    }

    /// <summary>管道可用性与旧行为一致（不可用则走数据库），但只在状态变化时记一条日志</summary>
    private void LogPipeStateChange(bool available)
    {
        if (_pipeAvailable == available)
            return;

        _pipeAvailable = available;
        if (available)
            _logger.LogInformation("使用时长服务管道已连接");
        else
            _logger.LogInformation("使用时长服务管道不可用，改用数据库值（服务是否已安装并启动？）");
    }

    /// <summary>
    /// 后台服务的库由服务自己创建，文件不存在意味着服务未安装/未启动过，
    /// 这是功能关闭而不是异常，因此不开库、不记堆栈，只在状态跳变时记一条。
    /// </summary>
    private bool IsServiceInstalled()
    {
        var installed = File.Exists(DbPath);
        if (_serviceInstalled != installed)
        {
            if (installed)
                _logger.LogInformation("检测到后台服务数据库，电脑使用时长功能启用");
            else
                _logger.LogWarning(
                    "未找到后台服务数据库 {DbPath}，电脑使用时长功能停用（安装并启动 XAssistant.Service 后自动恢复）",
                    DbPath
                );
            _serviceInstalled = installed;
        }
        return installed;
    }

    private void SetTodayUsageSeconds(long? seconds, bool isCorrected)
    {
        bool secondsChanged = _todayUsageSeconds != seconds;
        bool sourceChanged = _isTodayUsageCorrected != isCorrected;
        _todayUsageSeconds = seconds;
        _isTodayUsageCorrected = isCorrected;
        if (secondsChanged)
            OnPropertyChanged(nameof(TodayUsageSeconds));
        if (sourceChanged)
            OnPropertyChanged(nameof(IsTodayUsageCorrected));
    }

    private async Task LoadHistoryAsync()
    {
        var list = new ObservableCollection<DailyUsage>();
        if (!IsServiceInstalled())
        {
            History = list;
            return;
        }
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

                // 2. 从事件时间戳计算每一天的实际总秒数（已完成段），并收集修正原因
                var eventDailySeconds = new Dictionary<string, long>();
                // 跨午夜会话造成的归属差异：键为日期，值为「修正值相对数据库记录的增减秒数」
                var straddleDeltas = new Dictionary<string, long>();
                // 跨午夜会话的原因文本，按日期收集
                var straddleNotes = new Dictionary<string, List<string>>();
                // 存在未配对结束事件（缺少开始事件）的日期
                var unmatchedDates = new List<string>();
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
                            var start = segmentStart.Value;
                            // 跨午夜的会话段按自然日拆分，避免整段被记到结束当天
                            var byDay = SplitSegmentByDay(start, currTime);
                            foreach (var (dayKey, seconds) in byDay)
                            {
                                eventDailySeconds[dayKey] =
                                    eventDailySeconds.GetValueOrDefault(dayKey) + seconds;
                            }

                            // 服务按「会话整段记在开始日」记账；跨午夜时记录归属差异供原因展示
                            if (start.Date != currTime.Date)
                            {
                                var startKey = start.ToString("yyyy-MM-dd");
                                var startDaySec = byDay[startKey];
                                var crossOut = byDay.Values.Sum() - startDaySec;
                                if (crossOut > 0)
                                {
                                    straddleDeltas[startKey] =
                                        straddleDeltas.GetValueOrDefault(startKey) - crossOut;
                                    if (!straddleNotes.ContainsKey(startKey))
                                        straddleNotes[startKey] = new List<string>();
                                    straddleNotes[startKey]
                                        .Add(
                                            $"会话 {start:MM-dd HH:mm}→{currTime:HH:mm} 跨午夜，其中 {FormatSecondsShort(crossOut)} 实属次日"
                                        );
                                }
                                foreach (var (dayKey, seconds) in byDay)
                                {
                                    if (dayKey == startKey)
                                        continue;
                                    straddleDeltas[dayKey] =
                                        straddleDeltas.GetValueOrDefault(dayKey) + seconds;
                                    if (!straddleNotes.ContainsKey(dayKey))
                                        straddleNotes[dayKey] = new List<string>();
                                    straddleNotes[dayKey]
                                        .Add(
                                            $"会话 {start:MM-dd HH:mm}→{currTime:HH:mm} 跨午夜，{FormatSecondsShort(seconds)} 实属本日"
                                        );
                                }
                            }
                            segmentStart = null;
                        }
                        else if (eventType == "Suspend" || eventType == "ServiceStopped")
                        {
                            // 结束事件没有匹配的开始事件（服务记录缺失）
                            var dayKey = currTime.ToString("yyyy-MM-dd");
                            if (!unmatchedDates.Contains(dayKey))
                                unmatchedDates.Add(dayKey);
                        }
                    }

                    // 仍有未关闭的开始事件且不在今日，说明缺少结束事件
                    if (
                        segmentStart.HasValue
                        && segmentStart.Value.Date != DateTime.Today
                        && !unmatchedDates.Contains(segmentStart.Value.ToString("yyyy-MM-dd"))
                    )
                    {
                        unmatchedDates.Add(segmentStart.Value.ToString("yyyy-MM-dd"));
                    }
                }

                // 3. 对比并填充修正值与原因
                foreach (var record in dbRecords)
                {
                    if (eventDailySeconds.TryGetValue(record.Date, out var corrected))
                    {
                        record.CorrectedSeconds = corrected;

                        var notes = straddleNotes.GetValueOrDefault(
                            record.Date,
                            new List<string>()
                        );
                        // 未被跨午夜拆分解释的差值：秒级计时误差或事件缺失
                        var explainedDelta = straddleDeltas.GetValueOrDefault(record.Date);
                        var residual = (corrected - record.Seconds) - explainedDelta;
                        if (Math.Abs(residual) > 5)
                        {
                            if (unmatchedDates.Contains(record.Date))
                                notes.Add("存在未配对的会话事件（开始/结束事件缺失）");
                            else
                                notes.Add(
                                    $"另有 {FormatSecondsShort(Math.Abs(residual))} 的计时差异"
                                );
                        }
                        if (notes.Count > 0)
                            record.CorrectionReason = string.Join("；", notes);
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

        if (!IsServiceInstalled())
        {
            SessionEvents = events;
            _activeSessionStartEvent = null;
            _activeSessionStartTime = null;
            _todayCorrectedSeconds = 0;
            return;
        }

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

                            // 跨午夜的会话段按自然日拆分，避免整段被记到结束当天
                            foreach (
                                var (dayKey, seconds) in SplitSegmentByDay(
                                    segmentStartTime.Value,
                                    currTime
                                )
                            )
                            {
                                if (!dailyCorrectedSeconds.ContainsKey(dayKey))
                                    dailyCorrectedSeconds[dayKey] = 0;
                                dailyCorrectedSeconds[dayKey] += seconds;
                            }

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

    /// <summary>
    /// 中文短格式时长，如「48分22秒」「15分钟14秒」「5秒」。
    /// </summary>
    private static string FormatSecondsShort(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}天 {ts.Hours}小时 {ts.Minutes}分";
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}小时 {ts.Minutes}分";
        if (ts.Minutes > 0)
            return $"{ts.Minutes}分{ts.Seconds:D2}秒";
        return $"{ts.Seconds}秒";
    }

    /// <summary>
    /// 将会话段按自然日拆分，返回每天各自占用的秒数。
    /// 跨午夜的段（如 23:00 → 次日 01:00）会被拆到两天，避免整段记在开始或结束当天。
    /// </summary>
    private static Dictionary<string, long> SplitSegmentByDay(DateTime start, DateTime end)
    {
        var byDay = new Dictionary<string, long>();
        var cursor = start;
        while (cursor < end)
        {
            var nextMidnight = cursor.Date.AddDays(1);
            var segmentEnd = end < nextMidnight ? end : nextMidnight;
            var seconds = (long)(segmentEnd - cursor).TotalSeconds;
            if (seconds > 0)
            {
                var dayKey = cursor.ToString("yyyy-MM-dd");
                byDay[dayKey] = byDay.GetValueOrDefault(dayKey) + seconds;
            }
            cursor = segmentEnd;
        }
        return byDay;
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

    /// <summary>
    /// 读取服务端缓存的今日秒数；不可用时返回 null。
    /// 先查 \\.\pipe\ 命名空间再连：服务未启动时根本不进入 Connect，避开每 5 秒一次的
    /// TimeoutException（.NET 8 没有移植 PipeClientStream.TryWaitForConnection）。
    /// 本方法会阻塞，因此只在 Task.Run 里调用。
    /// </summary>
    private static long? TryReadPipeSeconds()
    {
        // 服务端存在时，命名管道会以文件形式出现在 \\.\pipe\ 下；File.Exists 本身不抛异常
        if (!File.Exists(@"\\.\pipe\" + PipeName))
            return null;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In);
            client.Connect(800);

            var buffer = new byte[256];
            int bytesRead = client.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
                return null;

            var text = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            return long.TryParse(text, out var seconds) ? seconds : null;
        }
        catch (Exception ex)
            when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // 探测与连接之间服务退出的竞态：视为不可用，不记异常
            return null;
        }
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
