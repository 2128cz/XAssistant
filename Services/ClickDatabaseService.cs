using System.IO;
using Microsoft.Data.Sqlite;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class ClickDatabaseService : IClickDatabaseService, IDisposable
{
    // 点击记录不再在钩子回调里同步写库，改由后台线程攒批提交；
    // 点击与移动量两类写入共用一条队列，它们就不会自己跟自己抢锁
    private readonly BackgroundBatchWriter<Action<SqliteConnection, SqliteTransaction>> _writer;
    private readonly string ConnectionString;

    private void PersistActions(IReadOnlyList<Action<SqliteConnection, SqliteTransaction>> actions)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000;";
        busy.ExecuteNonQuery();

        using var transaction = connection.BeginTransaction();
        foreach (var action in actions) action(connection, transaction);
        transaction.Commit();
    }

    public void Dispose() => _writer.Dispose();

    private static string InitializeConnectionString()
    {
        // 获取 AppData 目录，不存在则创建
        string appDataFolder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(appDataFolder);

        string dbPath = Path.Combine(appDataFolder, "click_data.db");
        return $"Data Source={dbPath}";
    }

    public ClickDatabaseService(string? databasePath = null)
    {
        ConnectionString = databasePath == null ? InitializeConnectionString() : new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
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

        // 移动量按天聚合：一次采样一行会在几天内堆出上千万条记录，而查询只需要“今日 / 累计”
        command.CommandText =
            @"
            CREATE TABLE IF NOT EXISTS MouseMovementDays (
                Date TEXT PRIMARY KEY,
                Pixels REAL NOT NULL
            );
        ";
        command.ExecuteNonQuery();

        // 落库改到后台线程后，写与读会真正并发（统计面板每分钟都在查），
        // 不开 WAL 的话两边抢锁会撞出 database is locked
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();

        _writer = new BackgroundBatchWriter<Action<SqliteConnection, SqliteTransaction>>(PersistActions, name: "点击");
    }

    public void SaveClick(MouseClickRecord record) => _writer.Enqueue((connection, transaction) => PersistClick(record, connection, transaction));
    private static void PersistClick(MouseClickRecord record, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ClickRecords (Button, ClickTime) VALUES (@b, @t)";
        command.Parameters.AddWithValue("@b", record.Button);
        command.Parameters.AddWithValue("@t", record.ClickTime.ToString("o")); // ISO 8601
        command.ExecuteNonQuery();
    }

    public void Flush() => _writer.Flush();

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

    public void AddMovementPixels(DateTime date, double pixels) => _writer.Enqueue((connection, transaction) => PersistMovementPixels(date,pixels,connection,transaction));
    private static void PersistMovementPixels(DateTime date, double pixels, SqliteConnection connection, SqliteTransaction transaction)
    {
        if (pixels <= 0)
            return;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            @"
            INSERT INTO MouseMovementDays (Date, Pixels) VALUES (@d, @p)
            ON CONFLICT(Date) DO UPDATE SET Pixels = Pixels + @p;
        ";
        cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@p", pixels);
        cmd.ExecuteNonQuery();
    }

    public double GetMovementPixels(DateTime date)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Pixels FROM MouseMovementDays WHERE Date = @d";
        cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd"));
        return cmd.ExecuteScalar() is double value ? value : 0d;
    }

    public double GetTotalMovementPixels()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(Pixels), 0) FROM MouseMovementDays";
        // SUM 对 REAL 列返回 Real，但全表为空时 COALESCE 给出整数 0
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0d);
    }
}
