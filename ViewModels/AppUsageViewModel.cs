using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services;

namespace XAssistant.ViewModels;

public partial class AppUsageViewModel : ViewModelBase
{
    private static readonly string DbPath = Path.Combine(
        AppDataPathHelper.GetAppDataFolder(),
        "app_usage.db"
    );
    private readonly DispatcherTimer _timer;
    private readonly ILogger<AppUsageViewModel> _logger;

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private ObservableCollection<AppUsageItem> _appUsageList = new();

    private volatile bool _isRefreshing;

    private const int RefreshIntervalSeconds = 2;

    public AppUsageViewModel(ILogger<AppUsageViewModel> logger)
    {
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
            return;
        _isRefreshing = true;
        try
        {
            var list = await LoadAppUsageAsync(SelectedDate);
            AppUsageList = new ObservableCollection<AppUsageItem>(list);
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

    private async Task<List<AppUsageItem>> LoadAppUsageAsync(DateTime date)
    {
        var result = new List<AppUsageItem>();
        var dateStr = date.ToString("yyyy-MM-dd");

        await Task.Run(() =>
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    @"
                SELECT ProcessName,
                    SUM(
                        CASE WHEN EndTime IS NULL 
                            THEN AccumulatedSeconds + (julianday('now','localtime') - julianday(COALESCE(LastUpdateTime, StartTime))) * 86400
                            ELSE AccumulatedSeconds
                        END
                    ) AS Seconds,
                    MIN(StartTime) AS StartTime,
                    MAX(COALESCE(EndTime, datetime('now','localtime'))) AS EndTime
                FROM ProcessSession
                WHERE Date = $date
                GROUP BY ProcessName
                ORDER BY Seconds DESC";
                cmd.Parameters.AddWithValue("$date", dateStr);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var seconds = (long)Math.Round(reader.GetDouble(1));
                    var startStr = reader.GetString(2);
                    var endStr = reader.GetString(3);

                    DateTime? startTime = DateTime.TryParse(startStr, out var st) ? st : null;
                    DateTime? endTime = DateTime.TryParse(endStr, out var et) ? et : null;

                    result.Add(
                        new AppUsageItem
                        {
                            ProcessName = name,
                            TotalSeconds = seconds,
                            StartTime = startTime,
                            EndTime = endTime,
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询软件使用数据失败");
            }
        });

        return result;
    }
}
