---
name: xassistant-watch-module
description: 为 XAssistant 编写监视模块（IWatchModule）——UPS 式外部状态轮询 + 全屏告警。实现 IWatchModule 即自动注册到工作台，不改宿主、不写清单。Use when adding a new watch/monitoring module (like UpsModule), wiring external device or API status polling with full-screen alerts, or extending Services/Modules in this repository.
---

# XAssistant 监视模块写法

工作台「PART 4 / MODULES」的卡片由 `Services/Modules/` 的协议驱动：**实现 `IWatchModule` 即完成注册**——宿主反射扫描本程序集（`WatchModuleRegistry.Start`），要求**无参构造函数**。参考实现：`Services/Modules/UpsModule.cs`。

## 三条协议约定（先记住这三条）

1. **值与元数据分开传送**。`Fields()` 是用户能改的键值对；`Metas()` 是前端只读的附加约束（按 `Key` 对齐）。元数据不参与提交回路，不会被回灌覆盖。
2. **只有两种控件**。`bool` 值 → 勾选行；其余任意值 → 输入行。「按钮」也是 bool 行：按下时前端本地产生 `true`，模块处理完用 `context.Submit(key, false)` 复位。
3. **生命周期每步都被宿主包 try**。模块抛了只把这张卡标「出错」，宿主照常跑——但别依赖这点，模块内部异步（HTTP/定时器）要自己 catch。

## 骨架模板

```csharp
namespace XAssistant.Services.Modules;

public sealed class FooModule : IWatchModule
{
    // 告警 tag 从 Id 派生命名；恢复/关闭时按 tag 精确收掉
    public const string AlertTag = "foo-alert";

    private readonly IFooApi _api;          // 接口隔离：测试注假实现
    private readonly IAlertSink _sink;
    private readonly List<ModuleField> _fields = [];
    private DateTime _lastPull = DateTime.MinValue;

    public FooModule() : this(new RealFooApi(), new EffectQueueAlertSink()) { }   // 反射注册走这条
    internal FooModule(IFooApi api, IAlertSink sink) { _api = api; _sink = sink; _fields = [...]; }

    public string Id => "foo";              // 稳定标识：配置键/tag 前缀都从它派生，改名=换模块
    public string Title => "Foo 监视";
    public ModuleSpace Space => new(400, 0);   // 只是排版参考，宿主回传实际尺寸

    public IReadOnlyList<ModuleField> Fields() => _fields;
    public IReadOnlyList<ModuleMeta> Metas() => [
        new("threshold") { NumericOnly = true, Min = 0, Max = 100, Unit = "%" },
        new("testAlert") { Hint = "按下即复位，演练用独立 tag、不带 -replay" },
    ];

    public void OnActivate(ModuleContext ctx) { /* 启动检查：激活时立刻跑一轮，别等第一个 tick */ }
    public void OnUpdate(ModuleContext ctx)   // 宿主 1 秒 tick，拉取节奏自己定
    {
        if (!ctx.IsEnabled || ctx.Now - _lastPull < TimeSpan.FromSeconds(10)) return;
        _lastPull = ctx.Now;
        // 拉取（异步走 fire-and-forget + 自己 catch）→ 判据 → 告警/收掉
    }
    public void OnDeactivate() => _sink.Clear(AlertTag);   // 必须收掉自己的告警，别留孤儿重播

    public void OnValuesPushed(IReadOnlyDictionary<string, object?> values)
    { /* 整份回灌对账：按规则钳制，context.Submit 推回；值相同 Submit 不动作，防打转 */ }
}
```

## 告警出口：一条效果命令行

告警走 sink 抽象（参考 `IUpsAlertSink`）：`Raise(命令行)` 进 `EffectQueue.Shared.Submit`，`Clear(tag)` 走 `EffectQueue.Kill`。**命令行与 `xa` 后面那段一字不差**：

```csharp
// 紧急档：无限重播直到恢复（-replay 间隔 [次数]，不写次数=无限；宿主不在时会被记成「被挤掉」）
_sink.Raise($"-s emergency 9 1 1 -border on 60 30 1 -lable on 30 故障 · 请求人类介入：{细节} -tag {AlertTag} -replay 20");
// 恢复/关闭：按 tag 精确收掉，不误杀别的紧急档
_sink.Clear(AlertTag);
```

语法段速查（解析在 `Services/EffectCommand.cs`）：`-s <色|emergency/urgent> 持续 淡入 淡出`、`-border on 渐宽 延伸 呼吸周期`、`-lable on 字号 文本`、`-from <平台>`（消息卡徽章）、`-tag <名>`、`-replay <秒> [次]`、`-k [-tag X | -any 子串]`（杀除，不给条件=全停）。

**演练（「试弹」bool 行）绝不复用真实告警参数**：独立 tag、不带 `-replay`——否则一按就无限重播，还会把真告警一起杀掉。

## 判据状态机（照 UpsModule 的三件套）

- **确认时长**：条件成立先记 `_since`，持续 N 秒才报警（`outageHoldSeconds`）——瞬抖不打扰。
- **闩锁**：`_alerted` 标志保证一次事故只放一条，条件解除后复位。
- **看门狗**：监视自身中断（接口连续失败）也要出声——黄档 + 独立 tag + 有限重播。

## 宿主事实（不用写代码，但要知道）

- 参数与激活态自动持久化到 `%LOCALAPPDATA%/XAssistant/watch-modules.json`（不进仓库）；模块不碰文件。
- tick 间隔 `WatchModuleRegistry.TickInterval`（1 秒）；面板回灌有 `PushDebounceMs`（300 ms）防抖。
- `ModuleContext`：`IsEnabled` / `Now` / `AvailableWidth` / `Submit(key, value)` / `MutateMeta` / `ReplaceMetas`（按状态收放行，如没配凭据就 `Visible=false` 藏掉行）/ `Log`。
- 常驻主程序在跑才有重播续期（`EffectQueue.Hosted`）；无头实例只播首轮。

## 落地检查单

- [ ] `Services/Modules/XxxModule.cs`：实现 `IWatchModule`，public 无参构造（真依赖）+ internal 构造（注假，供测试）
- [ ] 外部 IO 抽接口（`IXxxApi`/`IXxxTransport`），异步自己 catch，绝不让异常逃出回调
- [ ] `OnActivate` 做启动检查；`OnDeactivate` 按自己的 tag `Clear`
- [ ] 告警命令：真实告警带 `-tag`（+ 紧急档 `-replay`）；演练独立 tag 不带 `-replay`
- [ ] `dotnet build` 零警告零错误；跑起来看卡片出现在 PART 4（注册是反射自动的，没有清单要改）
- [ ] 测试写假 api + 假 sink，断言「什么条件下 Raise 了带什么 tag 的命令行、恢复时 Clear 了它」
