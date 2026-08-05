using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using XAssistant.Services;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services.QuickNote;

// 速记库写入：Npgsql 直插 xapp 主库 quick_notes 表
// 只写 content/source，id 与 created_at/updated_at 靠 DB 默认，archived_at/document_id 由 xapp 归档时写（契约见 xapp ADR 0010）
public sealed class QuickNoteDatabaseService : IQuickNoteDatabaseService
{
    private const string InsertSql =
        "INSERT INTO quick_notes (content, source) VALUES (@content, @source)";

    private const string DefaultEnvPath = @"C:\xapp-2026-06-30\.env";

    private readonly IConfigurationService _configurationService;
    private readonly ILogger<QuickNoteDatabaseService> _logger;

    public QuickNoteDatabaseService(
        IConfigurationService configurationService,
        ILogger<QuickNoteDatabaseService> logger
    )
    {
        _configurationService = configurationService;
        _logger = logger;
    }

    public async Task<bool> SaveAsync(string content, string? source)
    {
        string? raw = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogError(
                "未配置速记数据库连接串：请在 {ConfigPath} 的 QuickNote.ConnectionString 填入 xapp .env 的 DATABASE_URL，或放置 xapp .env 供自动读取",
                Path.Combine(AppDataPathHelper.GetAppDataFolder(), "appsettings.json")
            );
            return false;
        }

        try
        {
            string connectionString = BuildNpgsqlConnectionString(raw);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(InsertSql, connection);
            command.Parameters.AddWithValue("@content", content);
            command.Parameters.AddWithValue("@source", source is null ? DBNull.Value : source);
            await command.ExecuteNonQueryAsync();

            _logger.LogInformation("速记已保存（长度 {Length}，来源 {Source}）", content.Length, source);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "速记保存失败");
            return false;
        }
    }

    // 连接串解析：优先显式配置；未配置时回退读取 xapp .env 的 DATABASE_URL，让功能开箱即用
    private string? ResolveConnectionString()
    {
        string? configured = _configurationService.Settings.QuickNote.ConnectionString;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string envPath =
            _configurationService.Settings.QuickNote.DatabaseUrlEnvPath ?? DefaultEnvPath;
        return TryReadDatabaseUrlFromEnv(envPath);
    }

    private string? TryReadDatabaseUrlFromEnv(string envPath)
    {
        try
        {
            if (!File.Exists(envPath))
                return null;
            foreach (string rawLine in File.ReadAllLines(envPath))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("DATABASE_URL=", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = line["DATABASE_URL=".Length..].Trim();
                // 去掉可能的外层引号（.env 中常见 "postgresql://..."）
                if (
                    value.Length >= 2
                    && (value[0] == '"' || value[0] == '\'')
                    && value[^1] == value[0]
                )
                    value = value[1..^1];
                _logger.LogInformation("已从 {EnvPath} 读取速记库 DATABASE_URL", envPath);
                return value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 xapp .env 的 DATABASE_URL 失败：{EnvPath}", envPath);
        }
        return null;
    }

    // DATABASE_URL（形如 postgresql://user:pass@host:port/db）可直接粘贴，这里转成 Npgsql 连接串；已是 Npgsql 格式则原样使用
    private static string BuildNpgsqlConnectionString(string raw)
    {
        if (
            !raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        )
            return raw;

        var uri = new Uri(raw);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Database = uri.AbsolutePath.TrimStart('/'),
        };
        // 未显式指定端口时（未知 scheme 的 Uri.Port 为 -1），默认 5432
        builder.Port = uri.IsDefaultPort ? 5432 : uri.Port;

        // userinfo 中用户名与密码可能被 URL 编码
        string userInfo = Uri.UnescapeDataString(uri.UserInfo);
        int colon = userInfo.IndexOf(':');
        if (colon >= 0)
        {
            builder.Username = userInfo[..colon];
            builder.Password = userInfo[(colon + 1)..];
        }
        else
        {
            builder.Username = userInfo;
        }

        return builder.ConnectionString;
    }
}
