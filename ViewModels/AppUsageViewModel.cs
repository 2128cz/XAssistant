using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services;

namespace XAssistant.ViewModels;

/// <summary>
/// 进程使用记录。数据由 <see cref="ProcessUsageTracker"/> 写进 app_usage.db：
/// 每个应用每天可以有多条会话段，未关闭的那条只每 5 秒落盘一次累计值，
/// 所以这里读出来还要做一次「把没落盘的这段补上」的校正，再按应用合并成行。
/// </summary>
public partial class AppUsageViewModel : ViewModelBase
{
    private static string DbPath => Path.Combine(
        AppDataPathHelper.GetAppDataFolder(),
        "app_usage.db"
    );
    private readonly DispatcherTimer _timer;
    private readonly bool _monitoringEnabled;
    private readonly ILogger<AppUsageViewModel> _logger;

    /// <summary>
    /// 未关闭的会话段最多往前补算多少秒。追踪器每 5 秒落盘一次，90 秒已经是十八个节拍的余量；
    /// 比这更久的空 EndTime 只可能是崩溃或强杀留下的孤儿（要等下次启动才会被回收），
    /// 再补就会把「上次崩溃到现在」整段算成使用时长。
    /// </summary>
    private const long MaxLiveGapSeconds = 90;

    /// <summary>会话表默认铺开的行数，其余应用并成末行「其他」，避免二三十行把这块撑成长表。</summary>
    private const int VisibleSessionRows = 8;

    /// <summary>按应用名留存的行实例，让每次刷新只改值不换对象。</summary>
    private readonly Dictionary<string, AppUsageItem> _rowCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「其他」这一行是合成的，跨刷新只有它自己更新，不进缓存。</summary>
    private readonly AppUsageItem _overflowRow = new();

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    /// <summary>选中日期当天的全部应用行，按时长倒序。</summary>
    public ObservableCollection<AppUsageItem> AppUsageList { get; } = new();

    /// <summary>工作台会话表用的前若干行，尾部可能挂一行合成的「其他」。</summary>
    public ObservableCollection<AppUsageItem> SessionRows { get; } = new();

    /// <summary>进程库里出现过的日期，倒序。会话表的日期下拉要能选到它们，哪怕当天没有整体使用记录。</summary>
    public IReadOnlyList<string> AvailableDates { get; private set; } = Array.Empty<string>();

    /// <summary>当天全部应用的落盘累计秒数，不含未落盘的增量。</summary>
    [ObservableProperty]
    private long _sessionRawSeconds;

    /// <summary>当天全部应用校正后的秒数。注意它按应用分别计时，并行打开的应用会各算一份。</summary>
    [ObservableProperty]
    private long _sessionTotalSeconds;

    /// <summary>当天仍在运行、且仍在被补算的应用数（不是会话段数）。</summary>
    [ObservableProperty]
    private int _runningProcessCount;

    /// <summary>当天记录到的不同应用数。</summary>
    [ObservableProperty]
    private int _trackedProcessCount;

    private volatile bool _isRefreshing;

    private const int RefreshIntervalSeconds = 2;

    /// <summary>一段未合并的会话记录，只在读库线程上活着。</summary>
    private readonly record struct SessionSegment(
        string ProcessName,
        double AccumulatedSeconds,
        DateTime? StartTime,
        DateTime? EndTime,
        DateTime? LastUpdateTime);

    /// <summary>某个应用当天合并后的结果。</summary>
    private sealed record ProcessAggregate(
        string ProcessName,
        long RawSeconds,
        long TotalSeconds,
        bool IsRunning,
        int SessionCount,
        DateTime? StartTime,
        DateTime? LastActiveTime);

    /// <summary>一次读库的完整产出，供 UI 线程就地套用。</summary>
    private sealed record Snapshot(
        List<ProcessAggregate> Aggregates,
        long RawSeconds,
        long TotalSeconds,
        int RunningCount,
        List<string> Dates);

    public AppUsageViewModel(ILogger<AppUsageViewModel> logger, bool startMonitoring = true)
    {
        _logger = logger;
        _monitoringEnabled = startMonitoring;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        if (startMonitoring)
        {
            _timer.Start();
            _ = RefreshAsync();
        }
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!_monitoringEnabled || _isRefreshing)
            return;
        _isRefreshing = true;
        try
        {
            var snapshot = await Task.Run(() => LoadSnapshot(SelectedDate));
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新软件使用数据失败");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private Snapshot LoadSnapshot(DateTime date)
    {
        var segments = new List<SessionSegment>();
        var dates = new List<string>();
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Date FROM ProcessSession ORDER BY Date DESC LIMIT 60";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                dates.Add(reader.GetString(0));
        }

        using (var cmd = conn.CreateCommand())
        {
            // 会话段原样取出、在内存里按应用合并：校正规则只在 C# 一处，
            // 摊进 SQL 的 CASE 里就会被复制三遍（累计、判活、最近活动各一份）然后各自漂移。
            cmd.CommandText = """
                SELECT ProcessName, AccumulatedSeconds, StartTime, EndTime, LastUpdateTime
                FROM ProcessSession
                WHERE Date = $date
                """;
            cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                segments.Add(
                    new SessionSegment(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                        ReadTime(reader, 2),
                        ReadTime(reader, 3),
                        ReadTime(reader, 4)
                    )
                );
            }
        }

        return Merge(date, segments, dates);
    }

    private static Snapshot Merge(DateTime date, List<SessionSegment> segments, List<string> dates)
    {
        // 只有今天需要往前补算：历史那天的“现在”取当日末尾，孤儿会话不会被算成一直在用
        var now = date.Date == DateTime.Today ? DateTime.Now : date.AddDays(1).AddMilliseconds(-1);
        var aggregates = new List<ProcessAggregate>();
        foreach (var group in segments.GroupBy(segment => segment.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            double raw = 0;
            double corrected = 0;
            var running = false;
            var count = 0;
            DateTime? first = null;
            DateTime? last = null;
            foreach (var segment in group)
            {
                count++;
                raw += segment.AccumulatedSeconds;
                var live = 0d;
                if (segment.EndTime is null)
                {
                    // EndTime 为空即追踪器仍认为进程在跑，把最后落盘之后那段补上；
                    // 补算被 MaxLiveGapSeconds 拦住，孤儿的空 EndTime 不会被当成一直在用
                    var gap = (now - (segment.LastUpdateTime ?? segment.StartTime ?? now)).TotalSeconds;
                    if (gap is > 0 and <= MaxLiveGapSeconds)
                    {
                        live = gap;
                        running = true;
                    }
                }
                corrected += segment.AccumulatedSeconds + live;
                if (segment.StartTime is { } start && (first is null || start < first))
                    first = start;
                var activeAt = segment.EndTime ?? segment.LastUpdateTime ?? segment.StartTime;
                if (activeAt is { } activity && (last is null || activity > last))
                    last = activity;
            }
            aggregates.Add(
                new ProcessAggregate(
                    group.Key,
                    (long)Math.Round(raw),
                    (long)Math.Round(corrected),
                    running,
                    count,
                    first,
                    last
                )
            );
        }
        aggregates.Sort((left, right) => right.TotalSeconds.CompareTo(left.TotalSeconds));
        return new Snapshot(
            aggregates,
            aggregates.Sum(item => item.RawSeconds),
            aggregates.Sum(item => item.TotalSeconds),
            aggregates.Count(item => item.IsRunning),
            dates
        );
    }

    /// <summary>把一次读库的结果套到界面上：值改在旧行实例上，顺序用 Move 调。</summary>
    private void ApplySnapshot(Snapshot snapshot)
    {
        var ordered = new List<AppUsageItem>(snapshot.Aggregates.Count);
        foreach (var aggregate in snapshot.Aggregates)
        {
            if (!_rowCache.TryGetValue(aggregate.ProcessName, out var row))
            {
                row = new AppUsageItem { ProcessName = aggregate.ProcessName };
                _rowCache[aggregate.ProcessName] = row;
            }
            row.RawSeconds = aggregate.RawSeconds;
            row.TotalSeconds = aggregate.TotalSeconds;
            row.IsRunning = aggregate.IsRunning;
            row.SessionCount = aggregate.SessionCount;
            row.StartTime = aggregate.StartTime;
            row.EndTime = aggregate.LastActiveTime;
            row.Share = snapshot.TotalSeconds > 0
                ? aggregate.TotalSeconds * 100d / snapshot.TotalSeconds
                : 0;
            ordered.Add(row);
        }

        SyncRows(AppUsageList, ordered);

        var displayed = ordered.Take(VisibleSessionRows).ToList();
        if (ordered.Count > VisibleSessionRows)
            displayed.Add(BuildOverflowRow(ordered.Skip(VisibleSessionRows).ToList()));
        SyncRows(SessionRows, displayed);

        SessionRawSeconds = snapshot.RawSeconds;
        SessionTotalSeconds = snapshot.TotalSeconds;
        RunningProcessCount = snapshot.RunningCount;
        TrackedProcessCount = ordered.Count;
        if (snapshot.Dates.Count != AvailableDates.Count
            || !snapshot.Dates.SequenceEqual(AvailableDates))
        {
            AvailableDates = snapshot.Dates;
            OnPropertyChanged(nameof(AvailableDates));
        }
    }

    /// <summary>被折叠掉的那些应用合成一行。时长与占比照加，起止时间取并集的两端。</summary>
    private AppUsageItem BuildOverflowRow(List<AppUsageItem> hidden)
    {
        _overflowRow.ProcessName = $"其他 {hidden.Count} 个应用";
        _overflowRow.RawSeconds = hidden.Sum(item => item.RawSeconds);
        _overflowRow.TotalSeconds = hidden.Sum(item => item.TotalSeconds);
        _overflowRow.SessionCount = hidden.Sum(item => item.SessionCount);
        _overflowRow.IsRunning = hidden.Any(item => item.IsRunning);
        _overflowRow.StartTime = hidden.Min(item => item.StartTime);
        _overflowRow.EndTime = hidden.Max(item => item.EndTime);
        _overflowRow.Share = hidden.Sum(item => item.Share);
        return _overflowRow;
    }

    /// <summary>
    /// 就地同步集合：目标里已有的行一律复用，只把值改在旧实例上，顺序变化用 Move 表达。
    /// 直接赋新集合会让 DataGrid 重建全部行容器，两秒一次的刷新下滚动位置和悬停状态都留不住。
    /// </summary>
    private static void SyncRows(ObservableCollection<AppUsageItem> target, IReadOnlyList<AppUsageItem> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (ReferenceEquals(target.ElementAtOrDefault(index), desired[index]))
                continue;
            var found = -1;
            for (var candidate = index + 1; candidate < target.Count; candidate++)
            {
                if (ReferenceEquals(target[candidate], desired[index]))
                {
                    found = candidate;
                    break;
                }
            }
            if (found >= 0)
                target.Move(found, index);
            else
                target.Insert(index, desired[index]);
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static DateTime? ReadTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTime.TryParse(reader.GetString(ordinal), out var value)
                ? value
                : null;
}
