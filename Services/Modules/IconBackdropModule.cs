using System;
using System.Collections.Generic;
using System.Linq;

namespace XAssistant.Services.Modules;

/// <summary>
/// 背景立绘的设置卡：把「警告背后那颗谁在说话的剪影」暴露成面板上能改的几行——
/// 开关、图标文件、尺寸、位置、不透明度，外加一个试弹按钮当场看效果。
///
/// 这个模块不监视任何东西，它存在的理由是<b>参数面板与持久化都是现成的</b>：
/// 值走 <see cref="ModuleField"/>，元数据走 <see cref="ModuleMeta"/>，
/// 落盘由 <see cref="WatchModuleRegistry"/> 统一存进 watch-modules.json，模块自己一行文件 IO 都不碰。
/// 改完的值每 tick 同步进 <see cref="IconBackdrop.Current"/>，效果窗读那份快照——
/// 效果窗因此不需要认识面板，两边只共用这一个记录类型。
/// </summary>
public sealed class IconBackdropModule : IWatchModule
{
    /// <summary>试弹用的 tag：与真实告警分开，且不带 -replay（一按就无限重播是事故）。</summary>
    public const string PreviewTag = "icon-backdrop-preview";

    private readonly List<ModuleField> _fields =
    [
        new("on", "启用背景立绘", true),
        new("icon", "图标文件", "", "留空 = 按 -from 自动取；也可写绝对路径"),
        new("size", "尺寸", 62.0),
        new("x", "横向位置", 50.0),
        new("y", "纵向位置", 50.0),
        new("opacity", "不透明度", 16.0),
        new("preview", "试弹一条", false),
    ];

    /// <summary>提交一行命令行的出口。默认走常驻队列；internal 构造给测试注假。</summary>
    private readonly Action<string> _raise;

    private ModuleContext? _ctx;

    public IconBackdropModule() : this(line => EffectQueue.Shared.Submit(Parse(line))) { }

    internal IconBackdropModule(Action<string> raise)
    {
        _raise = raise;
        _fields =
        [
            new("on", "启用背景立绘", true),
            new("icon", "图标文件", "", "留空 = 按 -from 自动取；也可写绝对路径"),
            new("size", "尺寸", 62.0),
            new("x", "横向位置", 50.0),
            new("y", "纵向位置", 50.0),
            new("opacity", "不透明度", 16.0),
            new("preview", "试弹一条", false),
        ];
    }

    public string Id => "icon-backdrop";
    public string Title => "警告背景立绘";
    public ModuleSpace Space => new(420, 0);

    public IReadOnlyList<ModuleField> Fields() => _fields;

    public IReadOnlyList<ModuleMeta> Metas() =>
    [
        new("icon") { Hint = "只认带透明的 png；图标自身的 alpha 当遮罩，颜色跟着告警档位走，不需要准备白色图" },
        new("size") { NumericOnly = true, Min = 5, Max = 200, Unit = "%", Hint = "相对屏幕高度的百分比" },
        new("x") { NumericOnly = true, Min = 0, Max = 100, Unit = "%" },
        new("y") { NumericOnly = true, Min = 0, Max = 100, Unit = "%" },
        new("opacity") { NumericOnly = true, Min = 2, Max = 100, Unit = "%", Hint = "低于 2% 基本看不见，高于 ~30% 会开始抢正文" },
        new("preview") { Hint = "按档位弹一条普通告警看一眼，独立 tag、不重播" },
    ];

    public void OnActivate(ModuleContext context)
    {
        _ctx = context;
        Apply();
    }

    public void OnUpdate(ModuleContext context) => Apply();

    /// <summary>关掉就把立绘整层撤掉：不留一张「面板上已经关了但屏上还垫着」的图。</summary>
    public void OnDeactivate()
    {
        _ctx = null;
        IconBackdrop.Current = IconBackdropSettings.Default with { On = false };
        EffectQueue.Shared.Kill(null, PreviewTag, null);
    }

    public void OnValuesPushed(IReadOnlyDictionary<string, object?> values)
    {
        if (_ctx is not { } context) return;
        if (values.TryGetValue("preview", out var press) && Truthy(press))
        {
            Preview();
            context.Submit("preview", false);
        }
        Clamp(context, values, "size", 5, 200, IconBackdropSettings.Default.SizePercent);
        Clamp(context, values, "x", 0, 100, IconBackdropSettings.Default.XPercent);
        Clamp(context, values, "y", 0, 100, IconBackdropSettings.Default.YPercent);
        Clamp(context, values, "opacity", 2, 100, IconBackdropSettings.Default.OpacityPercent);
        Apply();
    }

    /// <summary>把面板上的六行换算成一份设置快照，交给效果窗读。</summary>
    private void Apply() =>
        IconBackdrop.Current = IconBackdropSettings.Clamp(new IconBackdropSettings(
            Truthy(Value("on")),
            Number(Value("size")),
            Number(Value("x")),
            Number(Value("y")),
            Number(Value("opacity"))));

    /// <summary>试弹：一条普通档告警，带 <c>-from</c> 好让立绘按来源取图。</summary>
    private void Preview() => _raise(
        "-s warn 5 1 1 -border on 50 30 1 -lable on 30 立绘 · 试弹 -tag " + PreviewTag);

    private static EffectCommand Parse(string line)
    {
        if (!EffectCommand.TryParse(line, out EffectCommand command)) throw new InvalidOperationException(line);
        return command;
    }

    private object? Value(string key) => _fields.FirstOrDefault(field => field.Key == key)?.Value;

    private static bool Truthy(object? raw) => raw switch
    {
        bool flag => flag,
        string text => text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1",
        _ => false,
    };

    private static double Number(object? raw) => raw switch
    {
        double number => number,
        int whole => whole,
        string text when double.TryParse(text, out double parsed) => parsed,
        _ => double.NaN,
    };

    private static void Clamp(ModuleContext ctx, IReadOnlyDictionary<string, object?> values,
        string key, double min, double max, double fallback)
    {
        if (!values.TryGetValue(key, out var raw)) return;
        double parsed = raw switch
        {
            double number => number,
            int whole => whole,
            string text when double.TryParse(text, out double typed) => typed,
            _ => double.NaN,
        };
        if (!double.IsFinite(parsed)) { ctx.Submit(key, fallback); return; }
        double fixedValue = Math.Clamp(parsed, min, max);
        if (Math.Abs(fixedValue - parsed) > 1e-9) ctx.Submit(key, fixedValue);
    }
}
