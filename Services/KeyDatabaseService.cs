using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyDatabaseService : IKeyDatabaseService, IDisposable
{
    // 按键记录不再在钩子回调里同步写库，改由后台线程攒批提交
    private readonly BackgroundBatchWriter<KeyPressRecord> _writer;
    private readonly string ConnectionString;

    private static string InitializeConnectionString()
    {
        string folder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(folder);
        string dbPath = Path.Combine(folder, "key_data.db");
        return $"Data Source={dbPath}";
    }

    public KeyDatabaseService(string? databasePath = null)
    {
        ConnectionString = databasePath == null ? InitializeConnectionString() : new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText =
            @"
            CREATE TABLE IF NOT EXISTS KeyPressRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Key TEXT NOT NULL,
                PressTime TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();

        // 时段筛选要按 PressTime 取区间，而记录是逐条插入、只增不减的，没有索引就等于每次全表扫描。
        // 把 Key 一起放进索引让查询走 index-only scan：GROUP BY 既不用回表，也不必再建临时 B 树。
        cmd.CommandText =
            @"CREATE INDEX IF NOT EXISTS IX_KeyPressRecords_PressTime_Key
              ON KeyPressRecords(PressTime, Key);";
        cmd.ExecuteNonQuery();

        // 落库改到后台线程后，写与读会真正并发；词频功能还会另开一个只读连接扫这张表，
        // 不开 WAL 的话两边抢锁会撞出 database is locked
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        _writer = new BackgroundBatchWriter<KeyPressRecord>(PersistBatch, name: "按键");
    }

    public void SaveKeyPress(KeyPressRecord record) => _writer.Enqueue(new KeyPressRecord { Key = record.Key, PressTime = record.PressTime });

    /// <summary>一批记录共用一个连接与一个事务，在后台线程执行</summary>
    private void PersistBatch(IReadOnlyList<KeyPressRecord> records)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000;";
        busy.ExecuteNonQuery();

        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t)";
        // 参数只加一次，循环里改值；AddWithValue 每次都追加一个同名参数
        var key = cmd.Parameters.Add("@k", SqliteType.Text);
        var time = cmd.Parameters.Add("@t", SqliteType.Text);
        foreach (var record in records)
        {
            key.Value = record.Key;
            time.Value = record.PressTime.ToString("o");
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>把队列里的记录写完并等在途事务提交，重新做全量统计前与退出前调用</summary>
    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();

    public Dictionary<string, int> GetKeyCounts()
    {
        return GetKeyCounts(null, null);
    }

    public Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to)
    {
        var counts = new Dictionary<string, int>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var cmd = connection.CreateCommand();

        if (from.HasValue && to.HasValue)
        {
            cmd.CommandText =
                @"
                SELECT Key, COUNT(*) 
                FROM KeyPressRecords 
                WHERE PressTime >= @from AND PressTime < @to 
                GROUP BY Key";
            cmd.Parameters.AddWithValue("@from", from.Value.ToString("o"));
            cmd.Parameters.AddWithValue("@to", to.Value.ToString("o"));
        }
        else
        {
            cmd.CommandText = "SELECT Key, COUNT(*) FROM KeyPressRecords GROUP BY Key";
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }
}
