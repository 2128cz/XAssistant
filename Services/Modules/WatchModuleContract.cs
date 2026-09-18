using System;
using System.Collections.Generic;

namespace XAssistant.Services.Modules;

/// <summary>
/// 一个监视型模块：程序启动时反射扫描本程序集里所有实现，卡片自动上架到工作台。
/// 继承它就算完成注册——不需要改宿主、不需要清单文件、不需要外部 DLL。
///
/// 三条约定撑住整套协议：
///
/// 1. <b>值与元数据分开传送</b>。<see cref="Fields"/> 是「用户能改的那一列键值对」，
///    <see cref="Metas"/> 是「前端只读、模块后台可改的那一列附加说明」（显示名、是否可输入、
///    是否保密、只允许数字…）。元数据前端改不了，所以它不参与提交回路，也不会被回灌覆盖。
/// 2. <b>只有两种控件</b>：<see langword="bool"/> 值 → 勾选行；其余任意值 → 值输入行。
///    判定顺序就是 bool 优先，剩下的一律走输入框（要不要只让填数字由元数据说）。
///    「按钮」也不额外造类型：一个 bool 行按下去就是 true，模块处理完再回 false 复位；
///    前端在按下时本地立刻产生 true，所以后端有没有复位都不影响下一次点击。
/// 3. <b>生命周期由宿主统一走，且每一步都包 try</b>：模块写错、接口炸了、解析抛了，
///    最多是这张卡不工作，不能把宿主带崩（<see cref="WatchModuleRegistry"/> 负责包）。
/// </summary>
public interface IWatchModule
{
    /// <summary>稳定标识：配置键、告警 tag 前缀都从它派生，改名等于换了一个模块。</summary>
    string Id { get; }

    /// <summary>卡片标题。</summary>
    string Title { get; }

    /// <summary>
    /// 模块「希望」占多大的空间（DIP）。只是给宿主排版的参考：宿主按逐行分栏的实际结果
    /// 回传真正可用的尺寸（<see cref="ModuleContext.AvailableWidth"/>），模块据此自己决定内部怎么排。
    /// </summary>
    ModuleSpace Space { get; }

    /// <summary>参数行：一行一个键值对，值类型决定控件形态。</summary>
    IReadOnlyList<ModuleField> Fields();

    /// <summary>元数据行：按 <see cref="ModuleField.Key"/> 对齐，前端只读。</summary>
    IReadOnlyList<ModuleMeta> Metas();

    /// <summary>开启：用户在面板上激活它时触发一次；程序启动时若它本来就是激活的，也照样走这一步。</summary>
    void OnActivate(ModuleContext context);

    /// <summary>关闭：用户手动关掉时触发一次；程序退出前对所有激活中的模块各触发一次。</summary>
    void OnDeactivate();

    /// <summary>
    /// 更新：宿主按固定频率 tick（默认 1 秒）调它。模块自己判断这次要不要真去取数
    /// （记下上次拉取时间即可），宿主不管模块内部的轮询节奏。
    /// </summary>
    void OnUpdate(ModuleContext context);

    /// <summary>
    /// 接收参数：前端把整份结构回灌给模块（用户改了某一行、或面板刷新时的一次性对账）。
    /// 模块可以在这里按自己的规则纠正（钳制浮点、拒绝非法值），再用
    /// <see cref="ModuleContext.Submit"/> 把改后的值推回去——脏标记保证这不会来回打转。
    /// </summary>
    void OnValuesPushed(IReadOnlyDictionary<string, object?> values);
}

/// <summary>模块申请的空间（DIP）。0 表示「没偏好，宿主说了算」。</summary>
public readonly record struct ModuleSpace(double Width, double Height);

/// <summary>一行参数：键 + 当前值。bool 走勾选，其余走输入框。</summary>
public sealed class ModuleField
{
    /// <summary>键：面板回灌与 Submit 都靠它对齐，只能由构造函数给。</summary>
    public string Key { get; }
    /// <summary>元数据没给显示名时用它兜底。</summary>
    public string Label { get; }
    public object? Value { get; set; }
    /// <summary>值输入行的占位提示（如「https://… 或留空用登录接口」）。</summary>
    public string? Placeholder { get; }

    public ModuleField(string key, string label, object? value, string? placeholder = null)
    {
        Key = key; Label = label; Value = value; Placeholder = placeholder;
    }
}

/// <summary>
/// 一行的元数据。前端只读；模块后台改了它就触发一次面板刷新。
/// 庞大的那部分（约束、多语言、保密）都塞在这里，值通道保持干净。
/// </summary>
public sealed class ModuleMeta
{
    /// <summary>对齐到哪一行参数。只能由构造函数给（模块后台改元数据时改的是其余字段）。</summary>
    public string Key { get; }
    /// <summary>显示名：多语言适配走它，空则回退 <see cref="ModuleField.Label"/>。</summary>
    public string? DisplayName { get; set; }
    /// <summary>false = 灰色且不可交互（比如模块正在按自己的规则维护这一项）。</summary>
    public bool Editable { get; set; } = true;
    /// <summary>保密：怎么遮由前端决定，模块只声明「这是敏感的」。</summary>
    public bool Secret { get; set; }
    /// <summary>只允许数字：前端拦输入，模块仍会在 OnValuesPushed 里再校验一次。</summary>
    public bool NumericOnly { get; set; }
    /// <summary>数值范围，供前端提示与模块钳制共用一份口径。</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>行尾单位（℃、%、秒），只是显示。</summary>
    public string? Unit { get; set; }
    /// <summary>悬停说明。</summary>
    public string? Hint { get; set; }
    /// <summary>false = 这一行不显示（模块按状态收放行，比如没配 cookie 时藏掉测试按钮）。</summary>
    public bool Visible { get; set; } = true;

    public ModuleMeta(string key) => Key = key;
}

/// <summary>
/// 模块在生命周期回调里能碰到的宿主能力。刻意做小：模块不需要知道面板、DI、窗口的存在。
/// </summary>
public interface ModuleContext
{
    /// <summary>模块当前是否处于激活态（tick 里据此决定要不要干活）。</summary>
    bool IsEnabled { get; }

    /// <summary>宿主这一轮 tick 的时间戳。模块用它自己算「该不该去拉取」。</summary>
    DateTime Now { get; }

    /// <summary>宿主回传的实际可用空间（DIP，仅参考；未测量时为 0）。</summary>
    double AvailableWidth { get; }
    double AvailableHeight { get; }

    /// <summary>
    /// 提交参数：把某一行的值改成模块认定的结果（钳制、复位按钮位…）并回灌前端。
    /// 与当前值相同则什么都不发生——脏标记靠这个把「模块纠正 → 前端刷新 → 再回灌 → 再纠正」
    /// 的回路掐死在第一步。
    /// </summary>
    void Submit(string key, object? value);

    /// <summary>改元数据（显示名、可输入、保密…），改完宿主会合并成一次面板刷新。</summary>
    void MutateMeta(string key, Action<ModuleMeta> mutate);

    /// <summary>整份重写元数据表（模块按状态收放行时用）。</summary>
    void ReplaceMetas(IEnumerable<ModuleMeta> metas);

    /// <summary>写日志：只进宿主日志，不打扰用户。</summary>
    void Log(string message);
}
