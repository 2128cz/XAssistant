using System;
using System.Collections.Generic;

namespace XAssistant.Services.Modules;

/// <summary>
/// 「接入状态」型监视模块的基类：Trae / ZCode / Codex / VS Code 这类平台各写各的子类，
/// 共用同一套读出骨架——目标程序 / 对话数据 / 事件接入 / 最近真实事件 / 能力与待办 + 立即刷新。
///
/// 行都是**只读读出**（Editable=false）：值由模块探测后用 Submit 推进来，面板灰显不可改；
/// 「立即刷新」是唯一的交互行（bool 按钮，按下即复位，与 skill 的按钮约定一致）。
/// 子类只实现四件事：Id、Title、静态的 Note（能力与待办）、Read()（四行的探测结果）。
/// </summary>
public abstract class IdeStatusModule : IWatchModule
{
    /// <summary>探测节奏：接入状态（进程/目录/hooks）不值得秒级刷。</summary>
    private const double ProbeSeconds = 10;

    private readonly List<ModuleField> _fields =
    [
        new("program", "目标程序", "…"),
        new("data", "对话数据", "…"),
        new("hooks", "事件接入", "…"),
        new("events", "最近真实事件", "…"),
        new("note", "能力与待办", "…"),
        new("refresh", "立即刷新", false),
    ];
    private DateTime _lastProbe = DateTime.MinValue;
    private ModuleContext? _context;   // OnValuesPushed 没有 ctx 参数：记下最近一次回调里那份（Entry 就是 ModuleContext）

    public abstract string Id { get; }
    public abstract string Title { get; }

    /// <summary>这行的静态正文由子类给（探测结果以外的「能力与待办」说明）。</summary>
    protected abstract string Note { get; }

    /// <summary>四行探测：目标程序 / 对话数据 / 事件接入 / 最近真实事件。</summary>
    protected abstract (string Program, string Data, string Hooks, string Events) Read();

    public ModuleSpace Space => new(520, 0);

    public IReadOnlyList<ModuleField> Fields() => _fields;

    public IReadOnlyList<ModuleMeta> Metas() =>
    [
        new("program") { Editable = false, Hint = "跑着 / 装着 / 没找到" },
        new("data") { Editable = false, Hint = "会话、日志、配置等数据目录的在位情况" },
        new("hooks") { Editable = false, Hint = "hooks 配置是否部署（部署后重启 IDE 生效）" },
        new("events") { Editable = false, Hint = "带 session_id 的真实事件最近一条的时间；一直为「无」说明 IDE 还没触发过" },
        new("note") { Editable = false, Hint = "接入现状与下一步" },
        new("refresh") { Hint = "按下即复位；想立刻看到最新探测时点它" },
    ];

    public void OnActivate(ModuleContext context)
    {
        _context = context;
        SetField("note", Note);
        RefreshStatus(context);
    }

    public void OnUpdate(ModuleContext context)
    {
        _context = context;
        if (context.Now - _lastProbe < TimeSpan.FromSeconds(ProbeSeconds)) return;
        RefreshStatus(context);
    }

    public void OnDeactivate() => _lastProbe = DateTime.MinValue;

    public void OnValuesPushed(IReadOnlyDictionary<string, object?> values)
    {
        if (!IsOn("refresh")) return;
        RefreshStatus(_context);
        _context?.Submit("refresh", false);
    }

    private void RefreshStatus(ModuleContext? ctx)
    {
        if (ctx is null) return;
        _lastProbe = ctx.Now;
        var (program, data, hooks, events) = Read();
        ctx.Submit("program", program);
        ctx.Submit("data", data);
        ctx.Submit("hooks", hooks);
        ctx.Submit("events", events);
    }

    private bool IsOn(string key)
    {
        foreach (var field in _fields)
            if (field.Key == key)
                return field.Value switch
                {
                    bool b => b,
                    string s => bool.TryParse(s, out bool parsed) && parsed,
                    _ => false,
                };
        return false;
    }

    private void SetField(string key, object? value)
    {
        foreach (var field in _fields)
            if (field.Key == key)
            {
                field.Value = value;
                return;
            }
    }
}