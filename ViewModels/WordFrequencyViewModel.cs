using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services;

namespace XAssistant.ViewModels;

public partial class WordFrequencyViewModel : ObservableObject, IDisposable
{
    private readonly WordFrequencyStore _store;
    private readonly DispatcherTimer? _timer;
    private bool _disposed;

    /// <summary>缓存为空时自动统计只回放到这个窗口起点，更早的历史交给右侧按钮。</summary>
    private static readonly TimeSpan AutoHistoryLimit = TimeSpan.FromDays(3);
    /// <summary>每页让出的时间倍数。实测回放 2000 条约 45 ms（4.4 万条/秒），让出 2 倍后有效速度压到约 1.5 万条/秒，换主线程不与热力图、曲线那些定时器争抢。</summary>
    private const int PauseFactor = 2;
    /// <summary>回灌途中的界面刷新节拍；每页都刷一次的话，178 页就是 178 次百行列表重建。</summary>
    private const int UiRefreshIntervalMs = 1000;
    /// <summary>第一次量到速度之前的兜底估算，取实测值算上让出之后的结果。</summary>
    private const double FallbackRowsPerSecond = 15_000;

    private double _rowsPerSecond;
    private DateTime _rateAt = DateTime.MinValue;
    private long _rateCursor;
    // 进度文案的口径：本轮从哪条开始算、一共多少条、还在不在跑
    private long _totalRows;
    private long _baseCursor;
    /// <summary>游标还在往前推，状态栏才写「正在统计」；否则最后一刷会永远停在统计中。</summary>
    private bool _counting;

    public ObservableCollection<WordFrequencyRow> Words { get; } = [];
    public ObservableCollection<WordOccurrence> Recent { get; } = [];
    public string[] Filters { get; } = ["全部", "易错", "已标记"];
    [ObservableProperty] private string _selectedFilter = "全部";
    [ObservableProperty] private string _status = "正在统计…";
    [ObservableProperty] private string _pending = "";
    [ObservableProperty] private bool _isBusy;
    /// <summary>右侧按钮上的文案：有待纳入的数据时写成「统计 量级 · 约 预估」。</summary>
    [ObservableProperty] private string _refreshLabel = "↻";
    [ObservableProperty] private string _refreshTip = "从按键记录重建";

    public WordFrequencyViewModel(WordFrequencyStore store, bool start = true)
    {
        _store = store;
        if (start)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += Tick; _timer.Start();
            // Startup may precede WPF installing its synchronization context.
            _ = _timer.Dispatcher.InvokeAsync(async () => await RefreshAsync());
        }
    }

    private async void Tick(object? sender, EventArgs e) => await RefreshAsync();
    // 换筛选只是换读法，不该再推进一次统计
    partial void OnSelectedFilterChanged(string value) { _ = PublishAsync(); }

    public async Task RefreshAsync(bool rebuild = false)
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        try
        {
            var backlog = await Task.Run(() => _store.InspectBacklog());
            // 冷缓存 + 历史超出窗口：把游标推到窗口起点，老历史不进统计
            if (!rebuild && !backlog.HasCache && backlog.Span > AutoHistoryLimit
                && await Task.Run(() => _store.FastForwardTo(DateTime.Now - AutoHistoryLimit)))
                backlog = await Task.Run(() => _store.InspectBacklog());

            // 重建是从头回放，代价按全部记录算；增量只算游标之后的部分
            _totalRows = rebuild ? backlog.NewestId : backlog.Records;
            _baseCursor = rebuild ? 0 : backlog.Cursor;
            _counting = _totalRows > 0;

            bool more = true;
            var sinceUi = Stopwatch.StartNew();
            while (more && !_disposed)
            {
                var page = Stopwatch.StartNew();
                more = await Task.Run(() => _store.Update(rebuild));
                rebuild = false;
                if (!more) break;
                if (sinceUi.ElapsedMilliseconds >= UiRefreshIntervalMs)
                {
                    await PublishAsync();
                    sinceUi.Restart();
                }
                // 让出比干活更久，整段回灌才只会吃到三成 CPU
                await Task.Delay((int)Math.Clamp(page.ElapsedMilliseconds * PauseFactor, 30, 3000));
            }
            _counting = false;
            await PublishAsync();
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { Status = "统计失败：" + e.Message; }
        finally { IsBusy = false; }
    }

    private async Task PublishAsync()
    {
        if (_disposed) return;
        // 投影和未统计规模一起读：分开调就是把开连接的钱付两遍（实测每次 12~16 ms）
        var (snapshot, backlog) = await Task.Run(() => _store.ReadWithBacklog(SelectedFilter));
        if (_disposed) return;
        // Do not disturb scrolling/hovered buttons on unchanged polling ticks.
        if (!Words.SequenceEqual(snapshot.Words)) { Words.Clear(); foreach (var row in snapshot.Words) Words.Add(row); }
        if (!Recent.SequenceEqual(snapshot.Recent)) { Recent.Clear(); foreach (var row in snapshot.Recent) Recent.Add(row); }
        Pending = snapshot.Pending;
        ShowProgress(snapshot, backlog);
    }

    private void ShowProgress(WordFrequencySnapshot snapshot, Backlog backlog)
    {
        string through = DateTime.TryParse(snapshot.Through, out var date) ? date.ToString("MM-dd HH:mm:ss") : snapshot.Through;
        long done = Math.Max(0, snapshot.Cursor - _baseCursor);
        if (snapshot.Cursor == 0) Status = "暂无记录";
        else if (_totalRows > 0 && _counting && done < _totalRows)
        {
            var now = DateTime.Now;
            if (_rateAt == DateTime.MinValue) { _rateAt = now; _rateCursor = done; }
            else if ((now - _rateAt).TotalSeconds >= 0.5 && done > _rateCursor)
            {
                double instant = (done - _rateCursor) / Math.Max(0.001, (now - _rateAt).TotalSeconds);
                _rowsPerSecond = _rowsPerSecond <= 0 ? instant : _rowsPerSecond * 0.5 + instant * 0.5;
                _rateAt = now; _rateCursor = done;
            }
            Status = $"正在统计 · 已至 {through}（{FormatCount(done)} / {FormatCount(_totalRows)}，约剩 {Estimate(_totalRows - done)}）";
        }
        else Status = "更新至 " + through;

        if (backlog.Skipped > 0)
            Status += $"；仅统计最近 {AutoHistoryLimit.TotalDays:0} 天，另有 {FormatCount(backlog.Skipped)}更早的历史未纳入";

        if (backlog.Records == 0 && backlog.Skipped == 0)
        {
            RefreshLabel = "↻";
            RefreshTip = $"从按键记录重建（{FormatCount(backlog.NewestId)}，约 {Estimate(backlog.NewestId)}）；赞踩标记不受影响";
        }
        else
        {
            RefreshLabel = $"统计 {FormatCount(backlog.NewestId)} · 约 {Estimate(backlog.NewestId)}";
            RefreshTip = $"点击从第一条按键记录重放 {FormatCount(backlog.NewestId)}，约 {Estimate(backlog.NewestId)}；"
                + $"其中有 {FormatCount(backlog.Skipped)}是 {AutoHistoryLimit.TotalDays:0} 天以外的历史，不点就永远不进统计。赞踩标记独立保存，重建不会丢。";
        }
    }

    private double Rate => _rowsPerSecond > 0 ? _rowsPerSecond : FallbackRowsPerSecond;

    private string Estimate(long rows)
    {
        double seconds = rows / Rate;
        if (seconds < 1) return "1 秒";
        if (seconds < 90) return $"{seconds:F0} 秒";
        return $"{seconds / 60:F1} 分钟";
    }

    private static string FormatCount(long rows) => rows switch
    {
        < 10_000 => $"{rows:N0} 条",
        < 100_000_000 => $"{rows / 10_000.0:F1} 万条",
        _ => $"{rows / 100_000_000.0:F1} 亿条",
    };

    [RelayCommand] private Task Rebuild() => RefreshAsync(true);
    [RelayCommand] private Task Good(WordFrequencyRow row) => Mark(row, 1);
    [RelayCommand] private Task Bad(WordFrequencyRow row) => Mark(row, -1);
    private async Task Mark(WordFrequencyRow row, int value)
    {
        if (_disposed) return;
        try
        {
            await Task.Run(() => _store.SetMark(row.Word, row.Mark == value ? 0 : value));
            await PublishAsync();
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { Status = "标记未保存：" + e.Message; }
    }
    public void Dispose() { _disposed = true; if (_timer != null) { _timer.Stop(); _timer.Tick -= Tick; } }
}
