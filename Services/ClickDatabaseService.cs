using System.IO;
using Microsoft.Data.Sqlite;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class ClickDatabaseService : IClickDatabaseService
{
    private static readonly string ConnectionString = InitializeConnectionString();

    private static string InitializeConnectionString()
    {
        // 获取 AppData 目录，不存在则创建
        string appDataFolder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(appDataFolder);

        string dbPath = Path.Combine(appDataFolder, "click_data.db");
        return $"Data Source={dbPath}";
    }

    public ClickDatabaseService()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText =
            @"
            CREATE TABLE IF NOT EXISTS ClickRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Button TEXT NOT NULL,
                ClickTime TEXT NOT NULL
            );
        ";
        command.ExecuteNonQuery();
    }

    public void SaveClick(MouseClickRecord record)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ClickRecords (Button, ClickTime) VALUES (@b, @t)";
        command.Parameters.AddWithValue("@b", record.Button);
        command.Parameters.AddWithValue("@t", record.ClickTime.ToString("o")); // ISO 8601
        command.ExecuteNonQuery();
    }

    public Dictionary<string, int> GetClickCountsByDate(DateTime date)
    {
        var counts = new Dictionary<string, int>
        {
            { "Left", 0 },
            { "Middle", 0 },
            { "Right", 0 },
        };

        string dateStr = date.ToString("yyyy-MM-dd");
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        // SQLite 的 date() 函数可以将 ISO 8601 字符串提取日期部分
        cmd.CommandText =
            @"
        SELECT Button, COUNT(*)
        FROM ClickRecords
        WHERE date(ClickTime) = @date
        GROUP BY Button";
        cmd.Parameters.AddWithValue("@date", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    // 后续分析用：获取所有记录或聚合数据
    public List<MouseClickRecord> GetAllRecords()
    {
        var records = new List<MouseClickRecord>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Button, ClickTime FROM ClickRecords ORDER BY ClickTime";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(
                new MouseClickRecord
                {
                    Id = reader.GetInt64(0),
                    Button = reader.GetString(1),
                    ClickTime = DateTime.Parse(reader.GetString(2)),
                }
            );
        }
        return records;
    }

    public Dictionary<string, int> GetClickCounts()
    {
        var counts = new Dictionary<string, int>
        {
            { "Left", 0 },
            { "Middle", 0 },
            { "Right", 0 },
        };
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Button, COUNT(*) FROM ClickRecords GROUP BY Button";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }
}
