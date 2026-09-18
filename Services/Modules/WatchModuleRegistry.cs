using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace XAssistant.Services.Modules;

/// <summary>
/// 监视模块的宿主：反射发现 → 生命周期编排 → 参数双向通道 → 脏标记与防抖。
///
/// 三条防死循环的硬规矩（协议的核心，改之前先读这三条）：
///
/// 1. <b>回灌只由用户交互触发</b>。模块在 <c>OnValuesPushed</c> 里 <c>Submit</c> 钳制值，
///    只会刷新面板，不会再回调模块——「前端 → 模块 → 前端 → 模块」这条路在第二跳就断了。
/// 2. <b>值没变就不算脏</b>。<see cref="ModuleContext.Submit"/> 与旧值等价时直接返回，
///    所以模块每轮无脑 Submit 也不会刷屏。
/// 3. <b>用户改动合并成一次投递</b>。连点几下勾选框、连着改几个输入框，防抖窗口内的改动
///    攒成一份完整结构一次性回灌，模块看到的永远是自洽的整份参数而不是半截编辑。
///
/// 生命周期每一步都包 try：模块抛了只记日志并把这张卡标成「出错」，宿主照常跑。
/// 配置与 cookie 落 <c>%LOCALAPPDATA%/XAssistant/watch-modules.json</c>，不进仓库。
/// </summary>
public sealed class WatchModuleRegistry : IDisposable
{
    /// <summary>宿主 tick 间隔：模块自己按这个节奏判断要不要真去取数。</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>用户改动 → 回灌模块的防抖窗口。</summary>
    public const int PushDebounceMs = 300;

    public static WatchModuleRegistry Shared { get; } = new();

    private readonly List<Entry> _entries = new();
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private Action<string>? _log;
    private bool _started;
    private readonly string _configPath;

    /// <summary>
    /// 常驻用 <see cref="Shared"/>；测试自己 new 一个并指定临时配置路径，互不串台
    /// （不隔离的话测试会把假模块写进用户真实的 watch-modules.json）。
    /// </summary>
    public WatchModuleRegistry(string? configPath = null)
    {
        _configPath = configPath ?? DefaultConfigPath;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>接上宿主日志（可选）。没接时模块日志与报错只留在卡片的错误行上。</summary>
    public void AttachLog(Action<string>? log) => _log = log;

    /// <summary>面板订阅：值、元数据、激活态、错误信息任何一项变了都会来一下（已合并防抖）。</summary>
    public event Action? Changed;

    public IReadOnlyList<WatchModuleInfo> Modules => _entries;

    private static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XAssistant", "watch-modules.json");

    /// <summary>
    /// 反射扫描本程序集里所有可实例化的 <see cref="IWatchModule"/>，读回上次的激活态与参数，
    /// 对已激活的走一次 <c>OnActivate</c>（协议要求「程序启动时也会检查是否激活并直接执行一次」）。
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        var config = ReadConfig();
        foreach (var type in typeof(IWatchModule).Assembly.GetTypes()
                     .Where(t => typeof(IWatchModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface
                                 && t.GetConstructor(Type.EmptyTypes) is not null)
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            try
            {
                var module = (IWatchModule)Activator.CreateInstance(type)!;
                config.Modules.TryGetValue(module.Id, out var saved);
                Register(module, saved?.Enabled, saved?.Values.ToDictionary(v => v.Key, v => (object?)v.Value));
            }
            catch (Exception error) { Warn(type.Name, "构造失败", error); }
        }
        _timer.Start();
        Changed?.Invoke();
    }

    /// <summary>
    /// 挂一个模块进来：反射发现与测试假件共用这一个入口。同 Id 重复注册会被忽略（幂等）。
    /// </summary>
    public WatchModuleInfo? Register(IWatchModule module, bool? enabledOverride = null, IReadOnlyDictionary<string, object?>? values = null)
    {
        if (_entries.Any(e => e.Module.Id == module.Id)) return Find(module.Id);
        var entry = new Entry(this, module);
        if (enabledOverride is { } forced) entry.Enabled = forced;
        if (values is not null)
            foreach (var field in module.Fields())
                if (values.TryGetValue(field.Key, out var value))
                    // 配置里存的是字符串：按字段当前值的类型转回去，否则 bool 行会变成输入框
                    field.Value = value is string raw ? Coerce(field.Value, raw) : value;
        _entries.Add(entry);
        if (entry.Enabled) entry.Activate();
        return entry;
    }

    /// <summary>面板拿到的只读视图。</summary>
    public WatchModuleInfo? Find(string id) => FindEntry(id);

    /// <summary>宿主侧的可写项（生命周期与参数都挂在这上面）。</summary>
    private Entry? FindEntry(string id) => _entries.FirstOrDefault(e => e.Module.Id == id);

    /// <summary>面板的总开关：true 走 OnActivate，false 走 OnDeactivate（都包 try）。</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var entry = FindEntry(id);
        if (entry is null || entry.Enabled == enabled) return;
        if (enabled) { entry.Enabled = true; entry.Activate(); }
        else { entry.Deactivate(); entry.Enabled = false; }
        WriteConfig();
        Changed?.Invoke();
    }

    /// <summary>
    /// 前端把某一行的新值交进来：立刻记账（面板显示不延迟），但**攒到防抖窗口结束**才整份回灌模块。
    /// </summary>
    public void PushValue(string id, string key, object? value)
    {
        var entry = FindEntry(id);
        if (entry is null) return;
        entry.ApplyFromUi(key, value);
        // 不另开计时器：记下“最早什么时候可以回灌”，由宿主 tick 到点再冲——面板连改几行会合并成一次投递
        entry.PushDueAt = DateTime.Now.AddMilliseconds(PushDebounceMs);
        Changed?.Invoke();
    }

    /// <summary>面板实测出这一格能用多大后回传（仅参考，模块自己决定内部怎么排）。</summary>
    public void ReportSpace(string id, double width, double height)
    {
        var entry = FindEntry(id);
        if (entry is null || Math.Abs(entry.AvailableWidth - width) < 1 && Math.Abs(entry.AvailableHeight - height) < 1) return;
        entry.AvailableWidth = width;
        entry.AvailableHeight = height;
    }

    /// <summary>
    /// 宿主心跳（也是测试的推进入口）：到点的回灌先冲，再对激活中的模块走一轮 OnUpdate。
    /// </summary>
    public void Tick()
    {
        var now = DateTime.Now;
        foreach (var entry in _entries)
        {
            if (entry.PushDueAt is { } due && now >= due)
            {
                entry.PushDueAt = null;
                entry.FlushPush();
            }
        }
        foreach (var entry in _entries)
            if (entry.Enabled) entry.Update();
    }

    /// <summary>程序退出：对所有激活中的模块各走一次 OnDeactivate。</summary>
    public void Dispose()
    {
        _timer.Stop();
        foreach (var entry in _entries.Where(e => e.Enabled)) entry.Deactivate();
    }

    internal void MarkDirty() => Changed?.Invoke();

    internal void Warn(string id, string where, Exception error)
    {
        _log?.Invoke($"模块 {id} 在 {where} 抛错：{error.GetBaseException().Message}");
        if (_entries.FirstOrDefault(e => e.Module.Id == id) is { } entry)
        {
            entry.LastError = $"{where}：{error.GetBaseException().Message}";
            Changed?.Invoke();
        }
    }

    // ===== 配置：激活态 + 参数值（cookie 也在这里，只落本机）=====

    private sealed class SavedModule
    {
        public bool Enabled { get; set; }
        public Dictionary<string, string> Values { get; set; } = new();
    }

    private sealed class SavedConfig
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, SavedModule> Modules { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private SavedConfig ReadConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) return new SavedConfig();
            return JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(_configPath), JsonOptions) ?? new SavedConfig();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Invoke($"读模块配置失败，本轮用默认值：{error.Message}");
            return new SavedConfig();
        }
    }

    private void WriteConfig()
    {
        var config = new SavedConfig();
        foreach (var entry in _entries)
        {
            var saved = new SavedModule { Enabled = entry.Enabled };
            foreach (var field in entry.Module.Fields())
                saved.Values[field.Key] = Convert.ToString(field.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            config.Modules[entry.Module.Id] = saved;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"写模块配置失败：{error.Message}");
        }
    }

    /// <summary>存回来的是字符串：按字段当前值的类型转回去，转不动就保留原文。</summary>
    private static object? Coerce(object? sample, string raw)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return sample switch
            {
                bool => bool.TryParse(raw, out bool b) && b,
                int => int.TryParse(raw, out int i) ? i : raw,
                double => double.TryParse(raw, out double d) ? d : raw,
                _ => raw,
            };
        }
        catch (FormatException) { return raw; }
    }

    /// <summary>
    /// 一个模块的宿主侧状态：值表、元数据表、激活态、脏标记，以及给模块的那份 <see cref="ModuleContext"/>。
    /// </summary>
    private sealed class Entry : WatchModuleInfo, ModuleContext
    {
        private readonly WatchModuleRegistry _owner;
        private readonly Dictionary<string, ModuleMeta> _metas = new();
        private readonly Dictionary<string, object?> _pending = new();

        internal Entry(WatchModuleRegistry owner, IWatchModule module)
        {
            _owner = owner;
            Module = module;
            foreach (var field in module.Fields()) _pending[field.Key] = field.Value;
            foreach (var meta in module.Metas()) _metas[meta.Key] = meta;
        }

        public IWatchModule Module { get; }
        public bool Enabled { get; internal set; }
        public string Id => Module.Id;
        public string Title => Module.Title;
        public ModuleSpace Space => Module.Space;
        public double AvailableWidth { get; internal set; }
        public double AvailableHeight { get; internal set; }
        public string? LastError { get; internal set; }
        public IReadOnlyList<ModuleField> Fields => Module.Fields();
        public IReadOnlyList<ModuleMeta> Metas => _metas.Values.ToList();
        internal DateTime? PushDueAt;

        public void Activate()
        {
            try { Module.OnActivate(this); LastError = null; }
            catch (Exception error) { _owner.Warn(Id, "OnActivate", error); }
        }

        public void Deactivate()
        {
            try { Module.OnDeactivate(); }
            catch (Exception error) { _owner.Warn(Id, "OnDeactivate", error); }
        }

        public void Update()
        {
            try { Module.OnUpdate(this); }
            catch (Exception error) { _owner.Warn(Id, "OnUpdate", error); }
        }

        /// <summary>前端改了一行：先记账，再攒进待回灌表。</summary>
        public void ApplyFromUi(string key, object? value)
        {
            var field = Fields.FirstOrDefault(f => f.Key == key);
            if (field is null) return;
            field.Value = value;
            _pending[key] = value;
        }

        /// <summary>防抖窗口结束：把整份结构交给模块（模块自己会 Submit 纠正）。</summary>
        public void FlushPush()
        {
            if (_pending.Count == 0) return;
            var snapshot = _pending.ToDictionary(p => p.Key, p => p.Value);
            _pending.Clear();
            try { Module.OnValuesPushed(snapshot); }
            catch (Exception error) { _owner.Warn(Id, "OnValuesPushed", error); }
        }

        // ===== ModuleContext：模块能碰到的四件事 =====

        public bool IsEnabled => Enabled;
        public DateTime Now => DateTime.Now;   // 不绕 dispatcher：取个时间不值得为它跨一次线程

        /// <summary>
        /// 提交参数：与当前值等价就什么都不做（脏标记的第一道闸）。
        /// 模块纠正出来的值只刷面板、不会再回灌模块——这是掐死「纠正→刷新→再纠正」回路的关键一跳。
        ///
        /// 用 BeginInvoke 而不是 Invoke：模块可能在后台线程的 await 延续里提交，
        /// 阻塞式派发一旦遇上 UI 线程正在等什东西就是死锁（控台推演里真踩到了）。
        /// </summary>
        public void Submit(string key, object? value) => _owner._dispatcher.BeginInvoke(new Action(() =>
        {
            var field = Fields.FirstOrDefault(f => f.Key == key);
            if (field is null || Equals(field.Value, value)) return;
            field.Value = value;
            _owner.MarkDirty();
        }));

        public void MutateMeta(string key, Action<ModuleMeta> mutate) => _owner._dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_metas.TryGetValue(key, out var meta))
                _metas[key] = meta = new ModuleMeta(key);
            mutate(meta);
            _owner.MarkDirty();
        }));

        public void ReplaceMetas(IEnumerable<ModuleMeta> metas) => _owner._dispatcher.BeginInvoke(new Action(() =>
        {
            _metas.Clear();
            foreach (var meta in metas) _metas[meta.Key] = meta;
            _owner.MarkDirty();
        }));

        public void Log(string message) => _owner._log?.Invoke($"模块 {Id}：{message}");
    }
}

/// <summary>面板只需要的只读视图（不暴露宿主侧可写状态）。</summary>
public interface WatchModuleInfo
{
    string Id { get; }
    string Title { get; }
    ModuleSpace Space { get; }
    bool Enabled { get; }
    double AvailableWidth { get; }
    double AvailableHeight { get; }
    string? LastError { get; }
    IReadOnlyList<ModuleField> Fields { get; }
    IReadOnlyList<ModuleMeta> Metas { get; }
    IWatchModule Module { get; }
}
