using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XAssistant.Services.Modules;

/// <summary>
/// 「会替某个 IDE 说话」的监视模块的<b>抽象基类</b>：在子类自己的参数行之后固定挂上一段背景立绘配置
/// （启用 / 图标 / 尺寸 / 位置 / 不透明度 + 试弹），并按 <see cref="IdeSource"/> 把这份参数发布给
/// <see cref="IconBackdrop"/>。于是 Qoder 的消息垫 Qoder 的图、DSH 的消息垫 DSH 的图——
/// 每张卡只管自己那个来源，谁在什么 IDE 里发消息，就照那张卡的配置立绘。
///
/// 三条不可省的性质（这一层存在的理由）：
///
/// 1. <b>默认什么都不画</b>。只有「模块处于激活态」且「勾了启用」才发布；关掉这张卡或取消勾选，
///    这个来源立刻从发布表里撤走，效果窗那一层就空着——不放任何来路不明的默认形状。
///    （上一版是一张独立设置卡 + 一个静态全局开关，静态默认值是「开」，卡没激活过也照样铺，
///    在面板上关掉都没有生效——那次事故是这一条的由来。）
/// 2. <b>子类没有机会忘记接</b>。<see cref="Fields"/>、<see cref="Metas"/> 与四个生命周期入口都由
///    基类占住，子类实现的是 <see cref="OwnFields"/> 与 <c>OnIdeXxx</c> 这一层钩子。
/// 3. <b>抽象类不进面板</b>。<see cref="WatchModuleRegistry"/> 的反射扫描跳过 abstract 类型，
///    所以这张基类不会被当成一个「无法创建实例的模块」列出来。
/// </summary>
public abstract class IconBackdropModule : IWatchModule
{
    // 立绘那几行的键统一带 bd 前缀：与子类的行同处一张表，不能撞名
    public const string OnKey = "bdOn", IconKey = "bdIcon", SizeKey = "bdSize",
                        XKey = "bdX", YKey = "bdY", OpacityKey = "bdOpacity", PreviewKey = "bdPreview";

    // 钩子与播放闸门那几行带 hk 前缀：装不装钩子、哪几类消息允许上屏
    public const string HooksKey = "hkOn", HooksStateKey = "hkState",
                        PlayAskKey = "hkAsk", PlayDoneKey = "hkDone", PlayToolFailKey = "hkToolFail",
                        PlayInterruptKey = "hkInterrupt", PlayErrorKey = "hkError";

    /// <summary>类型开关 → 词表里的词。**只列有开关的五个**：`notice` 是兜底那类，没有开关
    /// （卡开着就放行），否则"说不清的消息"会被整片吞掉。</summary>
    private static readonly (string Key, string Kind)[] PlaySwitches =
    [
        (PlayAskKey, "ask"), (PlayDoneKey, "done"), (PlayToolFailKey, "tool-fail"),
        (PlayInterruptKey, "interrupt"), (PlayErrorKey, "error"),
    ];

    /// <summary>
    /// 钩子与闸门这几行：<b>卡开＝登记闸门（立刻生效）＋按开关装钩子；卡关＝注销＋卸钩子＋收掉在屏消息</b>。
    /// 装卸钩子只改 IDE 的配置文件，而 Qoder/Claude 的 hooks 不热重载——所以"关掉立刻不播"靠的是闸门那一半。
    /// </summary>
    private readonly List<ModuleField> _hookFields =
    [
        new(HooksKey, "安装 hooks", true),
        new(PlayAskKey, "播放 · 请求人类介入", true),
        new(PlayDoneKey, "播放 · 回合结束", true),
        new(PlayToolFailKey, "播放 · 工具失败", true),
        new(PlayInterruptKey, "播放 · 对话中断", true),
        new(PlayErrorKey, "播放 · 运行告警", true),
        new(HooksStateKey, "hooks 状态", "…"),
    ];

    /// <summary>面板与发布表共用的默认摆放：居中、屏高 62%、16% 不透明度——压得住屏又不抢正文的读位。</summary>
    public const double DefaultSize = IconBackdrop.NeutralSizePercent,
                      DefaultCenter = IconBackdrop.NeutralXPercent,
                      DefaultOpacity = IconBackdrop.NeutralOpacityPercent;

    /// <summary>立绘行的当前值（按 key 取）。子类只读，不重建这张表。</summary>
    private readonly List<ModuleField> _backdropFields =
    [
        new(OnKey, "背景立绘", false),
        new(IconKey, "立绘图标", "", "留空 = 按来源自动取；也可写图名或完整路径"),
        new(SizeKey, "立绘尺寸", DefaultSize),
        new(XKey, "立绘横向位置", DefaultCenter),
        new(YKey, "立绘纵向位置", DefaultCenter),
        new(OpacityKey, "立绘不透明度", DefaultOpacity),
        new(PreviewKey, "试弹立绘", false),
    ];

    private List<ModuleField>? _fields;
    private IconBackdropTuning? _published;

    /// <summary>试弹那条命令的出口。真实现走常驻队列；受保护构造给子类（和测试）注假件。</summary>
    private readonly IWatchAlertSink _sink;

    protected IconBackdropModule() : this(new EffectQueueWatchSink()) { }

    protected IconBackdropModule(IWatchAlertSink sink) => _sink = sink;

    /// <summary>
    /// 这张卡替哪个来源说话：命令行 <c>-from</c> 的那个词，同时也是默认图名
    /// （<c>Assets/IdeIcons/&lt;来源&gt;.png</c>）。子类实现它，立绘与消息卡徽章就都对上了。
    /// 一个来源只归一张卡认领：发布表按来源覆盖，两张卡认领同一个来源会互相改写对方的摆放。
    /// </summary>
    protected abstract string IdeSource { get; }

    /// <summary>试弹专用 tag：从 Id 派生，与真实告警分开，能被 <c>xa -k -tag</c> 单独收掉。</summary>
    public string PreviewTag => Id + "-backdrop-preview";

    /// <summary>最近一次生命周期回调里宿主给的那份上下文（OnValuesPushed 没有 ctx 参数，靠它 Submit）。</summary>
    protected ModuleContext? Context { get; private set; }

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract ModuleSpace Space { get; }

    /// <summary>子类自己的参数行——立绘这几行由基类追加，子类既不用管也改不掉。</summary>
    protected abstract IReadOnlyList<ModuleField> OwnFields();

    /// <summary>子类自己的元数据行。</summary>
    protected virtual IReadOnlyList<ModuleMeta> OwnMetas() => [];

    public IReadOnlyList<ModuleField> Fields() => _fields ??= [.. OwnFields(), .. _backdropFields, .. _hookFields];

    public IReadOnlyList<ModuleMeta> Metas() => [.. OwnMetas(), .. BackdropMetas(), .. HookMetas()];

    // ===== 生命周期：基类占住接口，子类实现 OnIdeXxx 钩子 =====

    public void OnActivate(ModuleContext context)
    {
        Context = context;
        OnIdeActivate(context);
        SyncBackdrop();
        SyncGate();
        SyncHooksState();
        if (Flag(HooksKey)) InstallHooks();
        SyncRowVisibility();
    }

    public void OnUpdate(ModuleContext context)
    {
        Context = context;
        OnIdeUpdate(context);
        SyncBackdrop();
        SyncHooksState();
    }

    /// <summary>
    /// 关闭即撤回：立绘、试弹、**闸门登记**、钩子与这一路在屏消息全收掉——
    /// 面板上关掉卡之后还在刷屏，就是前几样漏了其中一样的历史事故。
    /// </summary>
    public void OnDeactivate()
    {
        OnIdeDeactivate();
        RetractBackdrop();
        _sink.Clear(PreviewTag);
        MessageGate.Shared.Unregister(IdeSource);
        _sink.Clear(IdeSource);          // 这一路在屏与在栈的消息一并收掉
        if (Flag(HooksKey)) RemoveHooks();
        Context = null;
    }

    public void OnValuesPushed(IReadOnlyDictionary<string, object?> values)
    {
        OnIdeValuesPushed(values);
        if (Context is not { } ctx) return;
        Clamp(ctx, values, SizeKey, IconBackdrop.MinSize, IconBackdrop.MaxSize, DefaultSize);
        Clamp(ctx, values, XKey, 0, 100, DefaultCenter);
        Clamp(ctx, values, YKey, 0, 100, DefaultCenter);
        Clamp(ctx, values, OpacityKey, IconBackdrop.MinOpacity, IconBackdrop.MaxOpacity, DefaultOpacity);
        if (Truthy(values.TryGetValue(PreviewKey, out var press) ? press : null))
        {
            Preview();
            ctx.Submit(PreviewKey, false);
        }
        SyncBackdrop();
        SyncRowVisibility();
        // 闸门每次回灌都重登记一次：类型开关一翻，下一帧就生效（不等 IDE、不等重装钩子）
        SyncGate();
        if (values.ContainsKey(HooksKey))
        {
            if (Flag(HooksKey)) InstallHooks();
            else RemoveHooks();
        }
    }

    /// <summary>子类钩子：与原来的 OnActivate/OnUpdate/OnDeactivate/OnValuesPushed 同一时机，只是不用管立绘。</summary>
    protected virtual void OnIdeActivate(ModuleContext context) { }
    protected virtual void OnIdeUpdate(ModuleContext context) { }
    protected virtual void OnIdeDeactivate() { }
    protected virtual void OnIdeValuesPushed(IReadOnlyDictionary<string, object?> values) { }

    /// <summary>试弹：一条普通档告警，带本卡的来源，于是这条消息就照这份立绘配置摆。</summary>
    protected virtual void Preview() => _sink.Raise(
        $"-s warn 5 1 1 -border on 50 30 1 -lable on 30 立绘 · 试弹 · {Title} -id {PreviewTag} -from {IdeSource}");

    // ===== 发布表：这张卡当前该不该替自己的来源垫立绘 =====

    /// <summary>
    /// 把面板上的六行换算成一份参数发布出去；模块没激活、或没勾「启用」就撤回。
    /// 记录类型有值相等性，所以每 tick 调一次也不重复写表。
    /// </summary>
    private void SyncBackdrop()
    {
        if (Context is not { IsEnabled: true } || !Flag(OnKey)) { RetractBackdrop(); return; }
        var next = new IconBackdropTuning(IdeSource, Text(IconKey),
            Number(SizeKey, DefaultSize), Number(XKey, DefaultCenter),
            Number(YKey, DefaultCenter), Number(OpacityKey, DefaultOpacity));
        if (next == _published) return;
        IconBackdrop.Publish(next);
        _published = next;
    }

    private void RetractBackdrop()
    {
        if (_published is null) return;
        IconBackdrop.Retract(_published.Source);
        _published = null;
    }

    /// <summary>没勾「启用」时把几何与试弹几行收起来：面板上只留开关和图标，看得清这张卡现在画不画。</summary>
    private void SyncRowVisibility()
    {
        if (Context is not { } ctx) return;
        bool on = Flag(OnKey);
        foreach (string key in new[] { SizeKey, XKey, YKey, OpacityKey, PreviewKey })
            ctx.MutateMeta(key, meta => meta.Visible = on);
    }

    // ===== 闸门与钩子：这张卡的状态 ⇄ IDE 里的钩子 ⇄ 哪几类消息允许上屏 =====

    private DateTime? _lastHooksProbe;
    private string? _lastHooksState;

    /// <summary>这一路现在允许的类型：五个开关 + 兜底的 notice（它没有开关，卡开着就放行）。</summary>
    private string[] EnabledKinds() =>
    [
        .. PlaySwitches.Where(s => Flag(s.Key)).Select(s => s.Kind),
        "notice",
    ];

    /// <summary>
    /// 装/卸写的是 IDE 的配置文件，所以在**自测**里把它指到一个临时根（等价安装器的 <c>-Root</c>），
    /// 免得测试去动真机的 <c>.qoder-cn</c> / <c>.trae-cn</c>；生产路径保持 null＝各平台自己的默认根。
    /// </summary>
    public static string? HooksRootOverride { get; set; }

    /// <summary>
    /// 把这张卡的类型开关登记进闸门（每次回灌都重登记，翻一下开关下一帧就生效）。
    /// 卡不在激活态就注销——注销＝这一路**一律放行**（闸门只管有主的消息，见 MessageGate 的口径 1）。
    /// </summary>
    private void SyncGate()
    {
        if (Context is not { IsEnabled: true }) { MessageGate.Shared.Unregister(IdeSource); return; }
        MessageGate.Shared.Register(IdeSource, EnabledKinds());
    }

    private void InstallHooks() => RunInstaller(install: true);
    private void RemoveHooks() => RunInstaller(install: false);

    /// <summary>
    /// 装/卸放后台线程：OnActivate 在 UI 线程上跑，装一次要起一个 PowerShell（几百毫秒），
    /// 卡在 UI 上会让人以为面板死了。结果回来用 Submit 写进状态行——写的是安装器自己的解释
    /// （它连「重启 IDE 才生效」都写在输出里），不是我们另编一句话。
    /// </summary>
    private void RunInstaller(bool install)
    {
        string source = IdeSource;
        System.Threading.Tasks.Task.Run(() =>
        {
            AgentHooksResult result = install
                ? AgentHooksInstaller.Install(source, HooksRootOverride)
                : AgentHooksInstaller.Remove(source, HooksRootOverride);
            string head = result.Success ? (install ? "已安装" : "已卸载") : $"操作失败（退出码 {result.ExitCode}）";
            Context?.Submit(HooksStateKey, $"{head} · {HooksDetail(source)}");
        });
    }

    /// <summary>状态行：文件探测（缓存 5 秒，别每 tick 读一次配置）+ 给人看的形态说明。</summary>
    private void SyncHooksState()
    {
        if (Context is not { } ctx) return;
        if (_lastHooksProbe is { } last && (ctx.Now - last).TotalSeconds < 5) return;
        _lastHooksProbe = ctx.Now;
        string detail = HooksDetail(IdeSource);
        if (detail == _lastHooksState) return;
        _lastHooksState = detail;
        ctx.Submit(HooksStateKey, detail);
    }

    /// <summary>一行话：装没装 + 这个平台的形态（不支持的平台直说，不假装能装）。</summary>
    private static string HooksDetail(string source)
    {
        AgentHooksStatus status = AgentHooksInstaller.Status(source, HooksRootOverride);
        return status.Platform is null
            ? $"这个平台没有校准过的装法 · {status.Detail}"
            : $"{status.Hint}（{status.Platform.Installable switch { true => "可装卸", false => "只读" }}）";
    }

    private static IReadOnlyList<ModuleMeta> HookMetas() =>
    [
        new(HooksKey) { Hint = "打开这张卡就把它写进 IDE 的 hooks 配置；取消勾选＝卸掉（只摘指向 agent-status.ps1 的条目，你自己的条目原样保留）" },
        new(PlayAskKey) { Hint = "请求人类介入 / 等待授权这类消息要不要上屏" },
        new(PlayDoneKey) { Hint = "对话回合结束 / 子代理成功要不要上屏" },
        new(PlayToolFailKey) { Hint = "工具调用失败要不要上屏" },
        new(PlayInterruptKey) { Hint = "整轮被打断（限流、配额、人按停）要不要上屏" },
        new(PlayErrorKey) { Hint = "运行告警（token 上限、策略阻断）要不要上屏" },
        new(HooksStateKey) { Editable = false, Hint = "配置里装没装 + 要不要重启 IDE 才生效；工具类平台（VS Code / ZCode）只读" },
    ];

    private static IReadOnlyList<ModuleMeta> BackdropMetas() =>
    [
        new(OnKey) { Hint = "不勾就什么都不画——不会留一个默认的圆角矩形在屏幕中间。关掉这张卡同样立即撤回" },
        new(IconKey)
        {
            Hint = "留空 = 按来源取 Assets/IdeIcons/<来源>.png；可只写图名，也可写完整路径。" +
                   "只认带透明的 png：图标自己的 alpha 当遮罩、颜色跟着告警档位走，不需要白色图；" +
                   "全透明或几乎实心的图会被判成「看不见」或「纯色块」而拒用",
        },
        new(SizeKey) { NumericOnly = true, Min = IconBackdrop.MinSize, Max = IconBackdrop.MaxSize, Unit = "%", Hint = "相对屏幕高度的百分比" },
        new(XKey) { NumericOnly = true, Min = 0, Max = 100, Unit = "%", Hint = "立绘中心的横向位置（屏幕宽度百分比）" },
        new(YKey) { NumericOnly = true, Min = 0, Max = 100, Unit = "%", Hint = "立绘中心的纵向位置（屏幕高度百分比）" },
        new(OpacityKey) { NumericOnly = true, Min = IconBackdrop.MinOpacity, Max = IconBackdrop.MaxOpacity, Unit = "%", Hint = "低于 2% 基本看不见，高于 ~30% 会开始抢正文" },
        new(PreviewKey) { Hint = "按这份配置弹一条本来源的告警看一眼；独立 tag、不带重播，不影响真实告警" },
    ];

    // ===== 面板取值的三个小读法：值可能是 bool/double，也可能是配置回灌进来的字符串 =====

    private ModuleField? Row(string key) => Fields().FirstOrDefault(field => field.Key == key);

    protected bool Flag(string key) => Row(key)?.Value switch
    {
        bool flag => flag,
        string text => text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1",
        _ => false,
    };

    protected string Text(string key) => Row(key)?.Value as string ?? "";

    protected double Number(string key, double fallback) => Row(key)?.Value switch
    {
        double number => number,
        int whole => whole,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
        _ => fallback,
    };

    /// <summary>数值行按区间钳回：前端拦了输入，模块仍要再校验一次（协议约定）。</summary>
    private static void Clamp(ModuleContext ctx, IReadOnlyDictionary<string, object?> values,
        string key, double min, double max, double fallback)
    {
        if (!values.TryGetValue(key, out var raw)) return;
        double parsed = raw switch
        {
            double number => number,
            int whole => whole,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed) => typed,
            _ => double.NaN,
        };
        if (!double.IsFinite(parsed)) { ctx.Submit(key, fallback); return; }
        double fixedValue = Math.Clamp(parsed, min, max);
        if (Math.Abs(fixedValue - parsed) > 1e-9) ctx.Submit(key, fixedValue);
    }

    private static bool Truthy(object? raw) => raw switch
    {
        bool flag => flag,
        string text => text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1",
        _ => false,
    };
}
