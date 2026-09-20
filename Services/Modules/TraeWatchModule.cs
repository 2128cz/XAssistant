using System;
using System.IO;

namespace XAssistant.Services.Modules;

/// <summary>
/// Trae 接入状态：hooks 已按官方 schema（version:1、三事件）部署，等重启后验证首触发；
/// 对话报错的日志形态尚未校准（拿到真实样本后补尾随告警，参照 QoderWatchModule）。
/// </summary>
public sealed class TraeWatchModule : IdeStatusModule
{
    private readonly string _userProfile;
    private readonly string _appData;
    private readonly string _localAppData;

    public TraeWatchModule() : this(null, null, null) { }

    /// <summary>可注入根目录（测试指临时目录，不碰真机数据）。</summary>
    public TraeWatchModule(string? userProfile, string? appData, string? localAppData)
    {
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _appData = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public override string Id => "trae-watch";
    public override string Title => "Trae 接入状态";

    protected override string Note =>
        "hooks 已部署（官方 schema：version:1 + Notification/PostToolUse/Stop）——重启 Trae 后验证首触发；" +
        "对话报错的日志形态未校准，拿到真实样本后照 QoderWatchModule 补尾随告警";

    protected override (string Program, string Data, string Hooks, string Events) Read() => (
        IdeProbe.Program("Trae CN", Path.Combine(_localAppData, "Programs", "Trae CN")),
        IdeProbe.Data(
            Path.Combine(_appData, "Trae CN", "User", "globalStorage", "state.vscdb"),
            Path.Combine(_appData, "Trae CN", "logs"),
            Path.Combine(_userProfile, ".trae-cn")),
        IdeProbe.Hooks(Path.Combine(_userProfile, ".trae-cn", "hooks.json")),
        IdeProbe.LastRealEvent(Path.Combine(_localAppData, "XAssistant", "agent-hooks", "agent-status.log"), "trae"));
}