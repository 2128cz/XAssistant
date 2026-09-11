using System.Collections.ObjectModel;
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
    public ObservableCollection<WordFrequencyRow> Words { get; } = [];
    public ObservableCollection<WordOccurrence> Recent { get; } = [];
    public string[] Filters { get; } = ["全部", "易错", "已标记"];
    [ObservableProperty] private string _selectedFilter = "全部";
    [ObservableProperty] private string _status = "正在统计…";
    [ObservableProperty] private string _pending = "";
    [ObservableProperty] private bool _isBusy;
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
    partial void OnSelectedFilterChanged(string value) { _ = RefreshAsync(); }
    public async Task RefreshAsync(bool rebuild = false)
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        try
        {
            bool more;
            do
            {
                more = await Task.Run(() => _store.Update(rebuild)); rebuild = false;
                if (_disposed) return;
                var snapshot = await Task.Run(() => _store.Read(SelectedFilter));
                if (_disposed) return;
                // Do not disturb scrolling/hovered buttons on unchanged polling ticks.
                if (!Words.SequenceEqual(snapshot.Words)) { Words.Clear(); foreach (var row in snapshot.Words) Words.Add(row); }
                if (!Recent.SequenceEqual(snapshot.Recent)) { Recent.Clear(); foreach (var row in snapshot.Recent) Recent.Add(row); }
                Pending = snapshot.Pending;
                Status = snapshot.Cursor == 0 ? "暂无记录" : (more ? "正在统计 · " : "更新至 ") + (DateTime.TryParse(snapshot.Through, out var date) ? date.ToString("MM-dd HH:mm:ss") : snapshot.Through);
                if (more) await Task.Delay(1);
            } while (more && !_disposed);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { Status = "统计失败：" + e.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private Task Rebuild() => RefreshAsync(true);
    [RelayCommand] private Task Good(WordFrequencyRow row) => Mark(row, 1);
    [RelayCommand] private Task Bad(WordFrequencyRow row) => Mark(row, -1);
    private async Task Mark(WordFrequencyRow row, int value)
    {
        if (_disposed) return;
        try
        {
            await Task.Run(() => _store.SetMark(row.Word, row.Mark == value ? 0 : value));
            await RefreshAsync();
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { Status = "标记未保存：" + e.Message; }
    }
    public void Dispose() { _disposed = true; if (_timer != null) { _timer.Stop(); _timer.Tick -= Tick; } }
}
