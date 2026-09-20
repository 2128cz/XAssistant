using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XAssistant.Services.Modules;

/// <summary>
/// DSH（DeepSeek Harness）接入状态。它不走外部 IDE 那套 hooks.json：DSH 本身就是 Cordis 运行时，
/// 装的是 profile 内的原生事件插件 <c>dsh-status-plugin.mjs</c>，靠 <c>cordis.patch.yml</c> 里带标记的
/// 一小段插进来。
///
/// 方向是**单向推**：插件订阅 DSH 的对话/工具/授权事件，自己 spawn <c>agent-status.ps1 -Platform dsh</c>
/// 把状态 JSON 从 stdin 推进来。XAssistant 不需要为 DSH 监听任何东西，所以这张卡只如实报
/// 「装没装、通没通、最近一条真机事件」，与 Trae / ZCode / Codex / VS Code 那几张同构。
/// </summary>
public sealed class DshStatusModule : IdeStatusModule
{
    private readonly string _dshHome;
    private readonly string _localAppData;

    public DshStatusModule() : this(null, null) { }

    /// <summary>根目录可注入（夹具指临时目录，不碰真机数据）。默认取 <c>$DSH_HOME</c>，没有就 <c>~\.dsh</c>。</summary>
    public DshStatusModule(string? dshHome, string? localAppData)
    {
        _dshHome = dshHome ?? Environment.GetEnvironmentVariable("DSH_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public override string Id => "dsh-watch";
    public override string Title => "DSH 接入状态";

    protected override string Note =>
        "原生 Cordis 插件单向推事件，XAssistant 侧无需监听或轮询：装好 profile 里的插件与状态脚本、" +
        "cordis.patch.yml 有标记块即可。DSH 的用户 patch 支持热重载（不像 Qoder / Claude 必须重启 IDE），" +
        "没加载就重启一次 dsh web。默认只报顶层对话（includeSubagents: false），子代理扇出的完成事件全部静默；" +
        "用户主动停止（aborted: user/disposed）也不报。标题优先用事件自带的 session_title，拿不到才回落到 vscdb 查任务名。" +
        $"\n插件与安装：{PluginRepo}；装/拆/试算走同一套安装器：" +
        @"install-agent-hooks.ps1 -Platform dsh -Profile <profile> [-DryRun|-Remove]（DSH_HOME 默认 ~\.dsh）。" +
        "状态脚本不在插件仓库里复制一份——分档、文案、来源徽章、乱码过滤全在本仓库的 agent-status.ps1。";

    /// <summary>插件的独立仓库地址：登记在这里，别人找不到插件时有一处可查。</summary>
    public const string PluginRepo = "http://192.168.1.111:3000/AI/DSH-XAssistant-AIHook.git";

    protected override (string Program, string Data, string Hooks, string Events) Read()
    {
        var profilesRoot = Path.Combine(_dshHome, "profiles");
        // profiles 下混着 pnpm 的 node_modules 与点开头的暂存目录，它们都不是 profile，列出来只会误导
        var profiles = Directory.Exists(profilesRoot)
            ? Directory.EnumerateDirectories(profilesRoot)
                .Where(p => !Path.GetFileName(p).StartsWith('.')
                            && !Path.GetFileName(p).Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];
        return (
            // 桌面端进程名 dsh-desktop（productName「DeepSeek Harness」）；web 端跑在 node 下，认数据根
            IdeProbe.Program("dsh-desktop", Path.Combine(_dshHome, "settings.yaml")),
            $"{IdeProbe.Data(_dshHome, Path.Combine(_dshHome, "sessions"), Path.Combine(_dshHome, "settings.yaml"))}"
                + $"（profile {profiles.Count} 个）",
            DescribeProfiles(profiles),
            IdeProbe.LastRealEvent(
                Path.Combine(_localAppData, "XAssistant", "agent-hooks", "agent-status.log"), "dsh"));
    }

    /// <summary>逐 profile 如实报：patch 里有没有我们的标记块、插件文件在不在位。</summary>
    private static string DescribeProfiles(List<string> profiles)
    {
        if (profiles.Count == 0) return "未部署（DSH_HOME 下没有 profiles）";
        var parts = new List<string>();
        foreach (string dir in profiles.Take(6))
        {
            string name = Path.GetFileName(dir);
            // cordis.patch.yml 里的 statusScript 就写着 agent-status.ps1，探测件直接复用这一判据
            bool patched = IdeProbe.Hooks(Path.Combine(dir, "cordis.patch.yml")).StartsWith("已部署");
            bool plugin = File.Exists(Path.Combine(dir, "xassistant-agent-hooks", "dsh-status-plugin.mjs"));
            parts.Add((patched, plugin) switch
            {
                (true, true) => $"{name}: 已部署（热重载即可）",
                (false, true) => $"{name}: 插件在位但 patch 没有标记块（不会被加载）",
                (true, false) => $"{name}: patch 有标记块但插件文件缺失（重跑安装器）",
                _ => $"{name}: 未部署",
            });
        }
        if (profiles.Count > 6) parts.Add($"…共 {profiles.Count} 个");
        return string.Join("；", parts);
    }
}
