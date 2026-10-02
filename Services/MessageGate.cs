using System;
using System.Collections.Generic;
using System.Linq;

namespace XAssistant.Services;

/// <summary>
/// 消息闸门：**哪一路消息现在允许上屏**的登记表。监视模块在自己的卡被激活时登记
/// 「我这个来源、允许哪几类消息」，关掉卡时注销；<see cref="EffectQueue.Submit"/> 在入队前问它一次。
///
/// 为什么要有这一层（而不是只靠装卸钩子）：Qoder / Claude 的 hooks **不支持热重载**，
/// 把钩子卸掉也要等 IDE 重启才真的不 spawn（<c>IdeProbe.Hooks</c> 的措辞就是「重启 IDE 生效」）。
/// 面板上关掉卡却还要看它继续刷屏，就是这么来的。闸门在应用侧，**立刻生效**。
///
/// 三条口径，缺一条都会误伤：
/// 1. <b>没登记的来源一律放行</b>。裸 <c>xa</c> 命令、打字彩蛋、别的脚本都没有登记，
///    闸门只关「有主的消息」，不能变成全局黑名单——否则调试一条命令要先找地方登记。
/// 2. <b>登记了但消息不带类型标签，也放行</b>。分不出类的东西不该被静默丢掉：
///    闸门管的是"这类消息要不要播"，不是"是不是我发的"。
/// 3. <b>类型标签里只要有一个被关，这条就挡下</b>（不是"有一个开着就放行"）——
///    用户按的是"这一类别播"，宽松解释等于开关失灵。词表外的标签不算类型，不参与判断。
/// </summary>
public sealed class MessageGate
{
    /// <summary>
    /// 类型词表。**与 <c>mcp/agent-hooks/agent-status.ps1</c> 里那份必须一致**（那边是 PowerShell，
    /// 读不到这里的常量，所以两处各写一份）：面板按它渲染开关，hook 按它写 <c>-tag</c>。
    /// 改词表要两边一起改，并跑 <c>effect-schedule-check</c> 与 <c>selftest.ps1</c>。
    /// </summary>
    public static readonly IReadOnlyList<string> Kinds =
        ["ask", "done", "tool-fail", "interrupt", "error", "notice"];

    /// <summary>进程里就这一份（常驻主程序与无头实例各有一份，各自管自己的屏）。</summary>
    public static MessageGate Shared { get; } = new();

    private readonly Dictionary<string, HashSet<string>> _allowed =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>被挡下的条数（面板显示用）。</summary>
    public int Blocked { get; private set; }

    /// <summary>登记表或计数变了（面板据此刷新）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 登记一路来源允许的类型。<paramref name="kinds"/> 里**没列**的类型即视为关掉；
    /// 传 null / 空表 = 这一路什么都不播（模块卡开着但把类型全关了，是合法配置）。
    /// </summary>
    public void Register(string producer, IEnumerable<string>? kinds)
    {
        if (string.IsNullOrWhiteSpace(producer)) return;
        _allowed[producer.Trim()] = new HashSet<string>(kinds ?? [], StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    /// <summary>注销一路来源：它的消息从此**一律放行**（不是一律挡下——见口径 1）。</summary>
    public void Unregister(string producer)
    {
        if (string.IsNullOrWhiteSpace(producer)) return;
        if (_allowed.Remove(producer.Trim())) Changed?.Invoke();
    }

    /// <summary>这一路登记过没有。</summary>
    public bool IsRegistered(string? producer) =>
        !string.IsNullOrWhiteSpace(producer) && _allowed.ContainsKey(producer.Trim());

    /// <summary>这一路允许的类型（没登记时给空表，调用方自己看 <see cref="IsRegistered"/>）。</summary>
    public IReadOnlyCollection<string> AllowedKinds(string? producer) =>
        IsRegistered(producer) ? _allowed[producer!.Trim()] : [];

    /// <summary>
    /// 这条命令该不该挡。规则全在上面那三条口径里；**纯函数**（除了计数），夹具直接喂命令进来问。
    /// </summary>
    public bool Blocks(EffectCommand command)
    {
        string? producer = command.Producer;
        if (!IsRegistered(producer)) return false;                       // 口径 1：没主的消息不管

        List<string> kinds = command.Tags.Where(IsKind).ToList();
        if (kinds.Count == 0) return false;                              // 口径 2：分不出类就不拦

        HashSet<string> allowed = _allowed[producer!.Trim()];
        bool blocked = kinds.Any(kind => !allowed.Contains(kind));       // 口径 3：有一个关着就挡
        if (blocked)
        {
            Blocked++;
            Changed?.Invoke();
        }
        return blocked;
    }

    /// <summary>这个词是不是类型词（来源词 / main / subagent 都不是）。</summary>
    public static bool IsKind(string tag) => Kinds.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>测试与「全部恢复」用：清空登记表，计数不归零（那是统计，不是状态）。</summary>
    public void Clear()
    {
        if (_allowed.Count == 0) return;
        _allowed.Clear();
        Changed?.Invoke();
    }
}