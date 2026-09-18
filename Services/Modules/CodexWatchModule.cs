using System;
using System.IO;

namespace XAssistant.Services.Modules;

/// <summary>
/// Codex 接入状态（占位）：CLI 形态，社区有 hooks（~/.codex/hooks）可接入，
/// 本机未安装——装好后照 skill 落地一个真正的尾随/事件模块。
/// </summary>
public sealed class CodexWatchModule : IdeStatusModule
{
    private readonly string _userProfile;
    private readonly string _localAppData;

    public CodexWatchModule() : this(null, null) { }

    /// <summary>可注入根目录（测试指临时目录）。</summary>
    public CodexWatchModule(string? userProfile, string? localAppData)
    {
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public override string Id => "codex-watch";
    public override string Title => "Codex 接入状态（占位）";

    protected override string Note =>
        "CLI 形态：社区 hooks（~/.codex/hooks）可接入，本机未检测到安装——装好后照 skill 落地";

    protected override (string Program, string Data, string Hooks, string Events) Read() => (
        IdeProbe.Program("codex", Path.Combine(_userProfile, ".codex")),
        IdeProbe.Data(Path.Combine(_userProfile, ".codex"), Path.Combine(_userProfile, ".codex", "sessions")),
        IdeProbe.Hooks(Path.Combine(_userProfile, ".codex", "hooks.json")),
        IdeProbe.LastRealEvent(Path.Combine(_localAppData, "XAssistant", "agent-hooks", "agent-status.log"), "codex"));
}