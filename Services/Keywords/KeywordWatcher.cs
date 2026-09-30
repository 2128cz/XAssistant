using System;
using System.Text;
using Microsoft.Extensions.Logging;
using XAssistant.Services.Interfaces;
using XAssistant.Views;

namespace XAssistant.Services.Keywords;

/// <summary>
/// 关键词引擎：把键盘钩子送来的输入切成词流，命中关键词就换肤 / 警告语 / 掉粒子；
/// 另外认一套斜杠命令（<see cref="SlashParser"/>），让正在打字的人不离开当前应用就能喊一声。
///
/// 切词规则直接复用 <see cref="WordFrequencyStore.DecodeKey"/>——与词频统计同一条物理键口径
/// （字母数字算词的一部分，其它键是分隔符，退格撤销上一个字符）。两处共用一个判据是刻意的：
/// 「词频面板里看到的词」和「能触发彩蛋的词」必须是同一批词，不然用户会以为彩蛋漏判。
///
/// 命中时机是**边打边判**：缓冲区一等于某个关键词就立刻触发，不必等空格。
/// 代价是 "white" 也是 "whiteboard" 的前缀，会提前掉一次——彩蛋要的就是即时反馈，这个取舍写在文档里。
/// 斜杠命令走另一条流（TextInput 才带得出 '/'），且收命令期间词流暂停匹配，
/// 否则敲 "/info white" 会中途把界面切成浅色。
/// </summary>
public sealed class KeywordWatcher : IDisposable
{
    /// <summary>一条斜杠命令最多收这么长：超了就是用户在打正文，不是命令。</summary>
    private const int MaxCommandLine = 160;

    private readonly KeywordCatalog _catalog;
    private readonly ILogger<KeywordWatcher> _logger;
    private readonly StringBuilder _word = new();
    private readonly StringBuilder _line = new();
    private IKeyboardHookService? _hook;
    private bool _firedThisWord;
    private bool _inCommand;
    private bool _commandRejected;

    /// <summary>关掉后按键照常记录，只是不再触发彩蛋（设置里那个开关）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>命中一条规则时抛出。设置区的「试一下」与离屏夹具都靠它，不直接依赖换肤与窗口。</summary>
    public event Action<KeywordRule>? Matched;

    public KeywordWatcher(KeywordCatalog catalog, ILogger<KeywordWatcher> logger)
    {
        _catalog = catalog;
        _logger = logger;
        foreach (string note in catalog.LoadNotes) _logger.LogWarning("关键词表：{Note}", note);
        _logger.LogInformation("关键词表就绪：{Count} 条规则", _catalog.Rules.Count);
    }

    public void ConnectKeyboard(IKeyboardHookService hook)
    {
        if (_hook != null) { _hook.KeyPressed -= OnKeyPressed; _hook.TextInput -= OnTextInput; }
        _hook = hook;
        _hook.KeyPressed += OnKeyPressed;
        _hook.TextInput += OnTextInput;
    }

    public void Dispose()
    {
        if (_hook != null) { _hook.KeyPressed -= OnKeyPressed; _hook.TextInput -= OnTextInput; }
        _hook = null;
    }

    private void OnKeyPressed(string keyName)
    {
        if (!Enabled || _inCommand) return;   // 收斜杠命令期间暂停词流：命令里也会出现 white 这样的词
        char c = WordFrequencyStore.DecodeKey(keyName);
        if (c == '\b')
        {
            // 退格撤销一个字符：打错的词不该再触发彩蛋
            if (_word.Length > 0) _word.Length--;
            _firedThisWord = false;
            return;
        }
        if (char.IsLetterOrDigit(c))
        {
            if (_word.Length >= 32) return;   // 长到这份上不是词，是有人在敲随机字符串
            _word.Append(char.ToLowerInvariant(c));
            if (!_firedThisWord && _catalog.Match(_word.ToString()) is { } rule)
            {
                _firedThisWord = true;
                Trigger(rule);
            }
            return;
        }
        _word.Clear();
        _firedThisWord = false;
    }

    /// <summary>命中一条斜杠命令时抛出（包括「收起」那一条）。</summary>
    public event Action<SlashCommand>? CommandMatched;

    /// <summary>
    /// 斜杠命令的状态机。'/' 开一条（并顺手收起当前那句，所以「再敲一个 / 就关闭显示」），
    /// 回车提交，Esc 或下一条 '/' 放弃当前这条。第一个词一结束就定性：不在词表里整条丢掉，
    /// 后面的字只吃掉不再重试，等下一个 '/' 进入——这就是「不对就直接跳过」。
    /// </summary>
    private void OnTextInput(string text)
    {
        if (!Enabled) return;
        foreach (char c in text)
        {
            if (c == '/')
            {
                EffectsWindow.HideBanner();
                _line.Clear();
                _commandRejected = false;
                _inCommand = true;
                _word.Clear();
                _firedThisWord = false;
                continue;
            }
            if (!_inCommand) continue;
            if (c == '\r' || c == '\n') { Submit(_line.ToString()); _inCommand = false; _line.Clear(); _commandRejected = false; continue; }
            if (c == '\u001b') { _inCommand = false; _line.Clear(); _commandRejected = false; continue; }   // Esc 中途退出
            if (c == '\b') { if (_line.Length > 0) _line.Length--; continue; }
            if (_commandRejected) continue;                       // 已经定性为「不是命令」：只吞不处理
            if (_line.Length >= MaxCommandLine) { _commandRejected = true; continue; }
            _line.Append(c);
            // 第一个词后面已经跟了空格，就当场定性；不匹配就不必等到回车了
            int space = IndexOfFirstSpace(_line);
            if (space > 0 && !SlashParser.KnownTone(_line.ToString(0, space))) _commandRejected = true;
        }
    }

    private static int IndexOfFirstSpace(StringBuilder buffer)
    {
        for (int i = 0; i < buffer.Length; i++) if (buffer[i] == ' ') return i;
        return -1;
    }

    /// <summary>提交一条命令：不合法就当没发生过，只等下一个斜杠。</summary>
    private void Submit(string line)
    {
        if (SlashParser.IsHide(line)) { Apply(new SlashCommand(SlashTone.Info, 0, 0, "", true)); return; }
        if (SlashParser.TryParse(line, out SlashCommand command)) Apply(command);
        else _logger.LogDebug("斜杠命令不是已知类型，已跳过：/{Line}", line);
    }

    private void Apply(SlashCommand command)
    {
        try
        {
            if (command.Hide) EffectsWindow.HideBanner();
            else EffectsWindow.ShowBanner(command.Text, SlashParser.BrushOf(command.Tone), command.Seconds, command.Blinks);
            CommandMatched?.Invoke(command);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "斜杠命令执行失败：{Text}", command.Text);
        }
    }

    /// <summary>
    /// 直接跑一条斜杠命令（无头模式与 MCP 入口走这里，不必真去敲键盘）。
    /// 参数不含前导斜杠，如 <c>warn 3 AI Computer Use</c>。
    /// </summary>
    public bool RunCommand(string line)
    {
        if (SlashParser.IsHide(line)) { Apply(new SlashCommand(SlashTone.Info, 0, 0, "", true)); return true; }
        if (!SlashParser.TryParse(line, out SlashCommand command)) return false;
        Apply(command);
        return true;
    }

    /// <summary>手动触发一次（设置区的「试一下」按钮走这里，不必真去敲那个词）。</summary>
    public void Fire(KeywordRule rule) => Trigger(rule);

    /// <summary>试一次：与真触发走同一条路（同一套几何、同一套排队），不动主题。</summary>
    public void Preview()
    {
        EffectQueue.Shared.Submit(KeywordCommand("试一次 · 敲到 white / black / flower 就会这样"));
        for (int i = 0; i < 3; i++) EffectsWindow.Emit(ToastWindow.Anchor, "🎲", null);
    }

    /// <summary>彩蛋消息的固定组键：敲不同的词也排成同一叠，不再像以前那样把整叠换掉。</summary>
    public const string KeywordGroup = "keyword";

    /// <summary>
    /// 彩蛋要走的那条消息：几何与老的 <c>ShowBanner</c> 一模一样（58 号、带子 60/36、共 7 秒），
    /// 但它是**一条普通消息**——进同一条队列、和告警排同一条队。<c>-stack off</c>＝不留顶部卡片
    /// （敲个词玩一下不该在顶栏堆一排卡），不写 tag＝重复敲同一个词各占一行（用户点名的堆叠）。
    /// </summary>
    public static EffectCommand KeywordCommand(string text)
    {
        double span = Math.Max(0.6, SlashParser.DefaultSeconds);
        double fadeIn = Math.Min(EffectsWindow.BannerFadeInSeconds, span / 3);
        double fadeOut = Math.Min(EffectsWindow.BannerFadeOutSeconds, span / 3);
        return new EffectCommand
        {
            Text = text,
            Hold = Math.Max(0.1, span - fadeIn - fadeOut),
            FadeIn = fadeIn,
            FadeOut = fadeOut,
            BorderOn = true,
            BorderWidth = 60,
            BorderFade = 36,
            BorderCycle = 0,
            FontSize = EffectCommand.DefaultFontSize,
            Group = KeywordGroup,
            Stack = false,
        };
    }

    public static EffectCommand KeywordCommand(KeywordRule rule) => KeywordCommand(rule.Banner ?? rule.Word);

    private void Trigger(KeywordRule rule)
    {
        try
        {
            if (rule.ChangesTheme) ApplyTheme(rule);
            // 一条斜线警告带横在屏幕中间，斜线用当前主题色拼，中间写这条规则自带的文案（没写就写词）。
            // 不再另弹 toast：两条公告渠道说同一件事，只会互相盖住（上一版就是叠了五个提示条）。
            // 走消息队列＝关键词不再享有"说到就到"的特权：撞上正在播的告警就排队，不把别人的字幕顶掉。
            EffectQueue.Shared.Submit(KeywordCommand(rule));
            if (rule.HasParticle)
            {
                // 一次只从顶部浮岛下方吐几颗（规则不写就是 1 颗）：几十颗同屏既费渲染又像撒沙子
                var origin = ToastWindow.Anchor;
                for (int i = 0; i < Math.Clamp(rule.Particles, 1, 4); i++) EffectsWindow.Emit(origin, rule.Glyph, rule.Image);
            }
            Matched?.Invoke(rule);
        }
        catch (Exception error)
        {
            // 彩蛋不能把主功能带下水：这里只记日志，绝不让按键回调抛出去
            _logger.LogError(error, "关键词「{Word}」触发失败", rule.Word);
        }
    }

    /// <summary>
    /// 换肤只在本次运行内有效，不回写 appsettings.json：误触一个词就把用户的主题偏好改掉，
    /// 下次启动还是那个主题才讲得通。要固定换回去用设置里那两个按钮（那两条会落盘，并抹掉色板）。
    /// </summary>
    private void ApplyTheme(KeywordRule rule)
    {
        if (rule.Colors is { Count: > 0 } colors)
            ThemeManager.ApplyPalette(rule.Theme ?? ThemeManager.Current, rule.Palette ?? rule.Word, colors);
        else
            ThemeManager.Apply(rule.Theme ?? ThemeManager.Current);
    }
}
