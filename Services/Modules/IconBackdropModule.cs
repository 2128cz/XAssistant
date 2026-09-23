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

    public IReadOnlyList<ModuleField> Fields() => _fields ??= [.. OwnFields(), .. _backdropFields];

    public IReadOnlyList<ModuleMeta> Metas() => [.. OwnMetas(), .. BackdropMetas()];

    // ===== 生命周期：基类占住接口，子类实现 OnIdeXxx 钩子 =====

    public void OnActivate(ModuleContext context)
    {
        Context = context;
        OnIdeActivate(context);
        SyncBackdrop();
        SyncRowVisibility();
    }

    public void OnUpdate(ModuleContext context)
    {
        Context = context;
        OnIdeUpdate(context);
        SyncBackdrop();
    }

    /// <summary>关闭即撤回：不留一份「面板上已经关了但屏上还垫着」的立绘，顺手收掉试弹那条。</summary>
    public void OnDeactivate()
    {
        OnIdeDeactivate();
        RetractBackdrop();
        _sink.Clear(PreviewTag);
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
    }

    /// <summary>子类钩子：与原来的 OnActivate/OnUpdate/OnDeactivate/OnValuesPushed 同一时机，只是不用管立绘。</summary>
    protected virtual void OnIdeActivate(ModuleContext context) { }
    protected virtual void OnIdeUpdate(ModuleContext context) { }
    protected virtual void OnIdeDeactivate() { }
    protected virtual void OnIdeValuesPushed(IReadOnlyDictionary<string, object?> values) { }

    /// <summary>试弹：一条普通档告警，带本卡的来源，于是这条消息就照这份立绘配置摆。</summary>
    protected virtual void Preview() => _sink.Raise(
        $"-s warn 5 1 1 -border on 50 30 1 -lable on 30 立绘 · 试弹 · {Title} -tag {PreviewTag} -from {IdeSource}");

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
