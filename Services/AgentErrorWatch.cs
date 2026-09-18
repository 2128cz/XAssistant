using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace XAssistant.Services;

/// <summary>
/// 对话报错哨兵：盯各家 IDE 的 agent.log，把「整轮对话被 API 错误打断、卡住等人恢复」报成红档。
///
/// 为什么需要它：quota/限流这类错误发生在**模型调用层**，不在任何 hook 事件流里（Qoder 的
/// 12 事件表没有 StopFailure，Trae 连表里这些都还没发过），transcript 也不落——hook 收不到。
/// 但 IDE 自己的日志会写状态机迁移：`State transition: prompting -> error, trigger:
/// chat_finish:{"code":100400,...}`——这是「对话卡死等人」的精确信号，实测 Qoder CN 在写。
///
/// 工作方式：每 3 秒扫各平台最近一次启动会话的 agent.log，从上次读到的偏移增量读新行
/// （日志是追加写的，读过的不回头）；命中错误模式且不在冷却窗里，就走 <see cref="EffectDispatch"/>
/// 发一条红档（全屏带 + 消息卡 + 平台徽章，与 hook 提醒同一条链路）。
/// 同一平台同一错误码两分钟只报一次：Qoder CN 会自动重试，重试期间状态机可能连写多行 error。
/// </summary>
public sealed class AgentErrorWatch : IDisposable
{
    /// <summary>扫描周期：报错到提醒晚几秒完全可以接受，不必更勤。</summary>
    private const int PollSeconds = 3;

    /// <summary>同一「平台 · 错误码」的冷却窗：压掉自动重试期间的连刷。</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    /// <summary>平台名（也是 -from 徽章名）→ %APPDATA% 下的目录名。国际版没装的机器上目录不存在，静默跳过。</summary>
    private static readonly (string Platform, string AppDir)[] Targets =
    [
        ("qoder", "QoderCN"),
        ("qoder-intl", "Qoder"),
        // Trae 的对话错误日志形态还没实锤过，拿到样本后往这张表加一行即可
    ];

    private readonly DispatcherTimer _timer;

    /// <summary>%APPDATA% 根（夹具可换成临时目录，不碰真 IDE 的日志）。</summary>
    private readonly string _appData;

    /// <summary>每个平台盯的日志：文件路径 + 读到的字节偏移（换了新日志文件就从 0 重读）。</summary>
    private readonly Dictionary<string, (string File, long Offset)> _cursors = new();

    /// <summary>上次报警时刻：key = 平台+错误码。</summary>
    private readonly Dictionary<string, DateTime> _lastAlert = new();

    /// <summary>状态机迁移进 error 态的行；code 从同行 JSON 里取。</summary>
    private static readonly Regex ErrorLine = new(
        @"State transition:\s*\w+\s*->\s*error.*?""code""\s*:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AgentErrorWatch(string? appDataOverride = null)
    {
        _appData = appDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PollSeconds) };
        _timer.Tick += (_, _) => Scan();
        _timer.Start();
    }

    /// <summary>测试夹具用：立刻扫一轮，不等定时器。</summary>
    public void ScanNow() => Scan();

    private void Scan()
    {
        foreach (var (platform, appDir) in Targets)
        {
            string? log = NewestAgentLog(Path.Combine(_appData, appDir, "logs"));
            if (log is null) continue;
            if (!_cursors.TryGetValue(platform, out var cursor) || cursor.File != log)
                cursor = (log, 0);
            List<string> lines = ReadNewLines(log, ref cursor);
            _cursors[platform] = cursor;
            foreach (string line in lines)
            {
                Match m = ErrorLine.Match(line);
                if (!m.Success) continue;
                Alert(platform, m.Groups[1].Value);
            }
        }
    }

    /// <summary>logs\&lt;启动时间戳&gt;\questWindow\agent.log——取最近修改的那份（IDE 每次启动开一个新目录）。</summary>
    private static string? NewestAgentLog(string logsRoot)
    {
        if (!Directory.Exists(logsRoot)) return null;
        try
        {
            return Directory.EnumerateDirectories(logsRoot)
                .Select(dir => Path.Combine(dir, "questWindow", "agent.log"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }   // 目录正好在轮换：下一轮再看
    }

    /// <summary>从游标偏移起读新行。IDE 独占写这些日志，但都是共享读开的，Read 不冲突。</summary>
    private static List<string> ReadNewLines(string log, ref (string File, long Offset) cursor)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < cursor.Offset) cursor.Offset = 0;   // 日志被截断重来
            fs.Seek(cursor.Offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
            cursor.Offset = fs.Position;
        }
        catch (IOException) { /* 正在被写锁住：下一轮补上，不丢偏移 */ }
        return lines;
    }

    private void Alert(string platform, string code)
    {
        string key = platform + "/" + code;
        DateTime now = DateTime.UtcNow;
        if (_lastAlert.TryGetValue(key, out DateTime last) && now - last < Cooldown) return;
        _lastAlert[key] = now;
        // 与 hook 提醒同一链路：红档全屏带 + 顶部消息卡 + 平台徽章；-from 用徽章表里认得的名字。
        // 这里进程内直调 OnLine，文本不加引号——引号是命令行的语法，真起 xa 进程时由 OS 剥掉，
        // 直调时留着它们就会原样钉进卡片
        string from = platform == "qoder-intl" ? "qoder" : platform;
        EffectDispatch.OnLine($"-s error 8 1 1 -border on 60 30 1 -lable on 26 中断 · 请求人类介入：对话被 API 错误打断 · code {code} -from {from}");
    }

    public void Dispose() => _timer.Stop();
}
