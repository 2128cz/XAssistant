using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XAssistant.Services.Modules;

/// <summary>
/// 日志尾随器：增量读一个追加写的日志文件，游标按「文件路径 + 字节偏移」记。
///
/// 监视模块们共用这一件（Qoder 现在用，Trae/Codex 之类的日志监视模块以后直接拿）。
/// 两个必须处理的实况：
///   1) IDE 独占写日志——共享读打开（FileShare.ReadWrite），Read 不冲突；
///   2) 日志按会话轮换/被截断重来——文件换了或长度缩了，游标归零重读。
/// </summary>
public sealed class LogTailer
{
    private readonly Dictionary<string, long> _cursors = new();

    /// <summary>当前游标（测试断言用）。</summary>
    public long OffsetOf(string log) => _cursors.TryGetValue(log, out long offset) ? offset : 0;

    /// <summary>从上次位置读新行；没新内容就是空表，不碰文件内容只查长度。</summary>
    public List<string> ReadNewLines(string log)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long offset = _cursors.TryGetValue(log, out long last) && fs.Length >= last ? last : 0;   // 换文件/截断都归零
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
            _cursors[log] = fs.Position;
        }
        catch (IOException) { /* 正在被写锁住：下一轮补上，游标不丢 */ }
        return lines;
    }

    /// <summary>
    /// 在 logs 根目录的每个会话子目录里找 <paramref name="relative"/> 对应的文件，取最近修改的那份。
    /// IDE 每次启动开一个新会话子目录，只有最新的在写。
    /// </summary>
    public static string? NewestUnder(string logsRoot, string relative)
    {
        if (!Directory.Exists(logsRoot)) return null;
        try
        {
            return Directory.EnumerateDirectories(logsRoot)
                .Select(dir => Path.Combine(dir, relative))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }   // 目录正好在轮换：下一轮再看
    }
}

/// <summary>
/// 监视模块的告警出口：发一条效果命令行、按 tag 收掉。实现走 <see cref="EffectQueue"/>，
/// 与 <c>xa</c> / hook 提醒完全同一条上屏链路（队列 → 全屏带 + 消息栈卡片）。
/// 模块只依赖这个接口，测试注假实现即可断言「发了什么、收了什么」。
/// </summary>
public interface IWatchAlertSink
{
    /// <summary>放一条效果命令（与 xa 后面那段一字不差）。</summary>
    void Raise(string commandLine);

    /// <summary>按 tag 收掉（带 -replay 的告警恢复时靠这个停）。</summary>
    void Clear(string tag);
}

/// <summary>真实现：进播放队列 / 按 tag 杀除。与 UpsModule 的同名结构是同一口径，后续可合并归一。</summary>
public sealed class EffectQueueWatchSink : IWatchAlertSink
{
    public void Raise(string commandLine)
    {
        if (!EffectCommand.TryParse(commandLine, out var command)) return;
        EffectQueue.Shared.Submit(command);
    }

    public void Clear(string tag) => EffectQueue.Shared.Kill(null, tag, null);
}