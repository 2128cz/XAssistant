using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace XAssistant.Services.Modules;

/// <summary>
/// Qoder 对话报错监视：尾随 IDE 自己的 agent.log，把「整轮对话被 API 错误打断」报成红档。
///
/// 为什么盯日志而不是 hook：quota/限流这类错误发生在**模型调用层**，不在任何 hook 事件流里
/// （Qoder 的 12 事件表没有 StopFailure），transcript 也不落；但 IDE 的日志会写状态机迁移
/// `State transition: prompting -> error, trigger: chat_finish:{"code":100400,...}`。
///
/// 从 <c>AgentErrorWatch</c> 迁移而来（服务 → 模块协议）：行为不变——增量的日志尾随
/// （<see cref="LogTailer"/>）、同平台同码冷却、红档 + 消息卡同一条上屏链路；多出来的是
/// 面板上的参数（扫描间隔/冷却窗）与「试弹」按钮，以及激活态的自动持久化。
/// 继承 <see cref="IconBackdropModule"/>：这张卡顺带管 Qoder 消息的背景立绘（来源词 <c>qoder</c>）。
/// </summary>
public sealed class QoderWatchModule : IconBackdropModule
{
    /// <summary>来源表：%APPDATA% 目录名 → 平台名（冷却键用）。国际版没装的机器上目录不存在，静默跳过。</summary>
    private static readonly (string AppDir, string Platform)[] Sources =
    [
        ("QoderCN", "qoder"),
        ("Qoder", "qoder-intl"),
    ];

    /// <summary>状态机迁移进 error 态的行；code 从同行 JSON 里取（Qoder CN 实盘形态）。</summary>
    private static readonly Regex ErrorLine = new(
        @"State transition:\s*\w+\s*->\s*error.*?""code""\s*:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>演练专用 tag：独立于真实告警，能被 <c>xa -k -tag</c> 单独收掉。</summary>
    public const string DrillTag = "qoder-watch-drill";

    private readonly string _appData;
    private readonly IWatchAlertSink _sink;
    private readonly LogTailer _tailer = new();
    private readonly HashSet<string> _primed = new();   // 首次见到的日志文件：只记游标不回放旧行
    private readonly Dictionary<string, DateTime> _lastAlert = new();
    private readonly List<ModuleField> _fields =
    [
        new("scanSeconds", "扫描间隔", 3.0),
        new("cooldownMinutes", "同码冷却", 2.0),
        new("testAlert", "试弹一次红档", false),
    ];
    private DateTime _lastScan = DateTime.MinValue;

    public QoderWatchModule() : this(null, new EffectQueueWatchSink()) { }

    /// <summary>可注入构造：appDataOverride 换日志根（测试指临时目录），sink 换告警出口（指假实现断言发了什么）。</summary>
    public QoderWatchModule(string? appDataOverride, IWatchAlertSink sink) : base(sink)
    {
        _appData = appDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _sink = sink;
    }

    public override string Id => "qoder-watch";
    public override string Title => "Qoder 对话报错监视";

    /// <summary>这张卡认领的来源词：hook 与真告警都带 <c>-from qoder</c>，立绘与徽章都认它。</summary>
    protected override string IdeSource => "qoder";

    public override ModuleSpace Space => new(520, 320);

    protected override IReadOnlyList<ModuleField> OwnFields() => _fields;

    protected override IReadOnlyList<ModuleMeta> OwnMetas() =>
    [
        new("scanSeconds") { NumericOnly = true, Min = 1, Max = 60, Unit = "秒", Hint = "尾随 agent.log 的节奏；报错到提醒最多晚这一档，日志空转时不读内容" },
        new("cooldownMinutes") { NumericOnly = true, Min = 0, Max = 60, Unit = "分", Hint = "同一平台同一错误码的冷却窗，压掉自动重试期间的连刷" },
        new("testAlert") { Hint = "按下即复位；演练用独立 tag、不带重播，不影响真实告警" },
    ];

    /// <summary>启动检查：激活时立刻扫一轮，不等第一个 tick。</summary>
    protected override void OnIdeActivate(ModuleContext context)
    {
        _lastAlert.Clear();
        Scan(context);
    }

    protected override void OnIdeUpdate(ModuleContext context)
    {
        if (!context.IsEnabled) return;
        if (context.Now - _lastScan < TimeSpan.FromSeconds(Number("scanSeconds", 3))) return;
        Scan(context);
    }

    /// <summary>关掉就复位：一次性红档没有 tag 要收，但冷却与扫描钟要清，重新激活时立刻能报。</summary>
    protected override void OnIdeDeactivate()
    {
        _lastAlert.Clear();
        _lastScan = DateTime.MinValue;
    }

    protected override void OnIdeValuesPushed(IReadOnlyDictionary<string, object?> values)
    {
        // 参数行按规则钳制——面板拦了输入，模块仍要再校验一次（协议约定）
        if (Context is { } ctx)
        {
            double scan = Math.Clamp(Number("scanSeconds", 3), 1, 60);
            if (Math.Abs(scan - Number("scanSeconds", 3)) > 1e-9) ctx.Submit("scanSeconds", scan);
            double cool = Math.Clamp(Number("cooldownMinutes", 2), 0, 60);
            if (Math.Abs(cool - Number("cooldownMinutes", 2)) > 1e-9) ctx.Submit("cooldownMinutes", cool);
        }
        // 「试弹」按钮：bool 行按下去就是 true，弹一条演练红档再复位
        if (Flag("testAlert"))
        {
            _sink.Raise($"-s error 5 1 1 -border on 60 30 1 -lable on 26 中断 · 请求人类介入：演练 · Qoder 报错监视 -tag {DrillTag} -from {IdeSource}");
            Context?.Submit("testAlert", false);
        }
    }

    private void Scan(ModuleContext ctx)
    {
        _lastScan = ctx.Now;
        foreach (var (appDir, platform) in Sources)
        {
            string? log = LogTailer.NewestUnder(Path.Combine(_appData, appDir, "logs"), Path.Combine("questWindow", "agent.log"));
            if (log is null) continue;
            // 首见（启动时/日志轮换后）：把既有内容消费掉只记住游标——历史报错在发生的那次运行里已经报过，
            // 监视器只报「从现在开始」的新错误，否则每次启动都会把昨天的 quota 错误再弹一遍
            if (_primed.Add(log)) { _tailer.ReadNewLines(log); continue; }
            foreach (string line in _tailer.ReadNewLines(log))
            {
                Match m = ErrorLine.Match(line);
                if (m.Success) Alert(ctx, platform, m.Groups[1].Value);
            }
        }
    }

    private void Alert(ModuleContext ctx, string platform, string code)
    {
        string key = platform + "/" + code;
        if (_lastAlert.TryGetValue(key, out DateTime last) && ctx.Now - last < TimeSpan.FromMinutes(Number("cooldownMinutes", 2))) return;
        _lastAlert[key] = ctx.Now;
        // 与 hook 提醒同一链路：红档全屏带 + 顶部消息卡 + 平台徽章
        _sink.Raise($"-s error 8 1 1 -border on 60 30 1 -lable on 26 中断 · 请求人类介入：对话被 API 错误打断 · code {code} -from {IdeSource}");
    }
}