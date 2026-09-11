using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyDatabaseService : IKeyDatabaseService
{
    private static readonly string ConnectionString = InitializeConnectionString();

    private static string InitializeConnectionString()
    {
        string folder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(folder);
        string dbPath = Path.Combine(folder, "key_data.db");
        return $"Data Source={dbPath}";
    }

    public KeyDatabaseService()
    {
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
    }

    public void SaveKeyPress(KeyPressRecord record)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t)";
        cmd.Parameters.AddWithValue("@k", record.Key);
        cmd.Parameters.AddWithValue("@t", record.PressTime.ToString("o"));
        cmd.ExecuteNonQuery();
    }

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
