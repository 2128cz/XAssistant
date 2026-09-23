using System;
using System.IO;

namespace XAssistant.Services.Modules;

/// <summary>
/// VS Code 接入状态（占位）：官方不提供 hooks 协议——可经扩展（写一个 StatusWatcher 扩展）
/// 或 UI Automation 观察对话状态，目前没做；这块卡只如实报告「程序与数据在不在」，
/// 表示我们可能可以接入。
/// </summary>
public sealed class VsCodeWatchModule : IdeStatusModule
{
    private readonly string _appData;
    private readonly string _localAppData;

    public VsCodeWatchModule() : this(null, null) { }

    /// <summary>可注入根目录（测试指临时目录）。</summary>
    public VsCodeWatchModule(string? appData, string? localAppData)
    {
        _appData = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public override string Id => "vscode-watch";
    public override string Title => "VS Code 接入状态（占位）";

    /// <summary>这张卡认领的来源词（<c>-from vscode</c>）：真实事件过滤与背景立绘共用它。</summary>
    protected override string IdeSource => "vscode";

    protected override string Note =>
        "占位：官方无 hooks 协议——可行路径是自写扩展或 UI Automation 观察，目前未实现；" +
        "想接入时先定「观察点」（扩展事件 / 状态栏文本），再照 skill 写模块";

    protected override (string Program, string Data, string Hooks, string Events) Read() => (
        IdeProbe.Program("Code",
            Path.Combine(_localAppData, "Programs", "Microsoft VS Code"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code")),
        IdeProbe.Data(
            Path.Combine(_appData, "Code", "User", "globalStorage", "state.vscdb"),
            Path.Combine(_appData, "Code", "logs")),
        "未提供（官方无 hooks 协议）",
        IdeProbe.LastRealEvent(Path.Combine(_localAppData, "XAssistant", "agent-hooks", "agent-status.log"), IdeSource));
}