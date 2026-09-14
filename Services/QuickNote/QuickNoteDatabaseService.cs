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

    /// <summary>
    /// 未配置 DatabaseUrlEnvPath 时找的 .env 文件名，与 appsettings.json 同住数据目录
    /// （<see cref="AppDataPathHelper.GetAppDataFolder"/>）。
    /// 这里刻意不写任何本机绝对路径：仓库要推到公网，硬编码某台机器上的目录既换机即失效，
    /// 也会把本机布局同步出去。要指向别处的 .env，就在数据目录的 appsettings.json 里配
    /// DatabaseUrlEnvPath：相对路径按数据目录解析，只有跨盘时才需写绝对路径；
    /// 那份配置本身在仓库外，不进版本控制。
    /// </summary>
    private const string DefaultEnvFileName = ".env";

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
                "未配置速记数据库连接串：请在 {ConfigPath} 的 QuickNote.ConnectionString 填入 DATABASE_URL，或把 .env 放到 {EnvPath}",
                Path.Combine(AppDataPathHelper.GetAppDataFolder(), "appsettings.json"),
                ResolveEnvPath(_configurationService.Settings.QuickNote.DatabaseUrlEnvPath)
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

    // 连接串解析：优先显式配置；未配置时回退读取 .env 的 DATABASE_URL，让功能开箱即用
    private string? ResolveConnectionString()
    {
        string? configured = _configurationService.Settings.QuickNote.ConnectionString;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string envPath = ResolveEnvPath(
            _configurationService.Settings.QuickNote.DatabaseUrlEnvPath
        );
        return TryReadDatabaseUrlFromEnv(envPath);
    }

    /// <summary>
    /// 把配置里的 .env 位置落成实际路径：留空取数据目录下的 .env；给相对路径则相对数据目录解析
    /// （配置里可以不写盘符，换机器或改数据目录都跟着走）；给绝对路径则原样使用。
    /// </summary>
    private static string ResolveEnvPath(string? configured)
    {
        string dataFolder = AppDataPathHelper.GetAppDataFolder();
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(dataFolder, DefaultEnvFileName);
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(dataFolder, configured));
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
