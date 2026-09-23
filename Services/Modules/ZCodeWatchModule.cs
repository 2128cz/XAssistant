using System;
using System.IO;

namespace XAssistant.Services.Modules;

/// <summary>
/// ZCode 接入状态：Electron + 独立 CLI/agent 形态，会话与产物落在 ~/.zcode/cli
/// （agents/sess_* 与 artifacts）；未发现 hooks 机制，事件接入待探索
/// （会话目录里有 metadata.json/output.txt 可尾随，拿到报错样本后照 QoderWatchModule 补）。
/// </summary>
public sealed class ZCodeWatchModule : IdeStatusModule
{
    private readonly string _userProfile;
    private readonly string _localAppData;

    public ZCodeWatchModule() : this(null, null) { }

    /// <summary>可注入根目录（测试指临时目录）。</summary>
    public ZCodeWatchModule(string? userProfile, string? localAppData)
    {
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public override string Id => "zcode-watch";
    public override string Title => "ZCode 接入状态";

    /// <summary>这张卡认领的来源词（<c>-from zcode</c>）：真实事件过滤与背景立绘共用它。</summary>
    protected override string IdeSource => "zcode";

    protected override string Note =>
        "会话数据可读（~/.zcode/cli：agents 会话目录 + artifacts 工具产物）；未发现 hooks 机制，" +
        "事件接入待探索——拿到真实报错样本后照 QoderWatchModule 写尾随告警";

    protected override (string Program, string Data, string Hooks, string Events) Read()
    {
        string agents = Path.Combine(_userProfile, ".zcode", "cli", "agents");
        int sessions = IdeProbe.CountDirs(agents);
        string data = IdeProbe.Data(
            agents,
            Path.Combine(_userProfile, ".zcode", "cli", "artifacts"),
            Path.Combine(_userProfile, ".zcode", "workspace"));
        if (sessions > 0) data += $"（会话 {sessions} 个）";
        return (
            IdeProbe.Program("ZCode", Path.Combine(_localAppData, "Programs", "ZCode")),
            data,
            "未提供（未见 hooks 机制）",
            IdeProbe.LastRealEvent(Path.Combine(_localAppData, "XAssistant", "agent-hooks", "agent-status.log"), IdeSource));
    }
}