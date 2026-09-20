using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace XAssistant.Services.Modules;

/// <summary>
/// 平台接入状态的探测件：给「接入状态」型监视模块共用（Trae / ZCode / Codex / VS Code …）。
/// 只做四件如实的事：目标程序找没找到、对话数据目录在不在、hooks 部署没有、最近有没有真实事件。
/// 所有路径都从参数进来（模块把注入的根目录传进来），测试可以指向假目录。
/// </summary>
public static class IdeProbe
{
    /// <summary>目标程序：跑着 → 装着（未运行）→ 没找到。</summary>
    public static string Program(string processName, params string[] installHints)
    {
        try
        {
            if (Process.GetProcessesByName(processName).Length > 0) return "运行中";
        }
        catch (InvalidOperationException) { /* 查进程失败当没跑：下面的目录存在性还有话说 */ }
        foreach (string hint in installHints)
            if (Directory.Exists(hint) || File.Exists(hint)) return "已安装（未运行）";
        return "未找到";
    }

    /// <summary>对话数据：候选路径里在位的项数（目录或文件都算）。</summary>
    public static string Data(params string[] candidates)
    {
        int found = candidates.Count(path => Directory.Exists(path) || File.Exists(path));
        return found == 0 ? "未找到" : $"{found}/{candidates.Length} 项在位";
    }

    /// <summary>子目录计数（ZCode 的会话数之类）。目录不存在返回 0。</summary>
    public static int CountDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Count() : 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>hooks 部署：配置文件里有没有指到 agent-status.ps1 的条目。</summary>
    public static string Hooks(string configPath)
    {
        if (!File.Exists(configPath)) return "未部署（配置不存在）";
        try
        {
            return File.ReadAllText(configPath).Contains("agent-status.ps1")
                ? "已部署（重启 IDE 生效）"
                : "未部署（配置里没有我们）";
        }
        catch (IOException) { return "读取失败"; }
    }

    /// <summary>最近一条**真实事件**（带 session_id 的行）：19 字时间戳或「无真实事件」。</summary>
    public static string LastRealEvent(string statusLog, string platformTag)
    {
        if (!File.Exists(statusLog)) return "无事件日志";
        try
        {
            string? last = null;
            foreach (string line in File.ReadLines(statusLog))
                if (line.Contains("[" + platformTag, StringComparison.OrdinalIgnoreCase) && line.Contains("session_id")) last = line;
            return last is null ? "无（从未真实触发）" : last[..Math.Min(19, last.Length)];
        }
        catch (IOException) { return "读取失败"; }
    }
}