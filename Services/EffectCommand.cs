using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Brush / Brushes / Color 会跟 WPF 的撞名
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using XAssistant.Services.Keywords;

namespace XAssistant.Services;

/// <summary>
/// 一条命令行效果指令（<c>xa -s ...</c> / <c>XAssistant.exe --fx ...</c> 的解析产物）。
/// 纯数据 + 纯解析：整个类不碰窗口、不碰 Application 资源，夹具可以直接断言解析结果。
///
/// 语法（新）：
///   <c>-s &lt;色|类型&gt; [持续 [淡入 [淡出]]]</c>            默认 info / 5 / 1 / 1
///   <c>-border on|off [渐宽 [延伸 [周期]]]</c>               默认 on / 50 / 30 / 1（周期 0 = 不循环）
///   <c>-lable on|off [字号] [文本…]</c>                      文本写在段内；不给文本就不显示文字条带
///   <c>-from &lt;词&gt;</c>                                  来源平台标记（hook 传 -Platform 名），消息栈卡片拿它配 IDE 图标徽章
///   <c>confetti</c> / <c>off</c>                            撒花 / 收起
///
/// 语法（旧，保留兼容 <c>--fx</c> 与 MCP 垫片）：<c>warn 3 "AI Computer Use"</c>，走 <see cref="SlashParser"/>，
/// 总时长换算成三段：首尾各 <c>min(1, 总长/3)</c>，中间是持续段。
/// </summary>
public sealed record EffectCommand
{
    /// <summary>收起当前效果（<c>off</c> / <c>hide</c> / <c>stop</c> …）。</summary>
    public bool Hide { get; set; }

    /// <summary>撒花（<c>confetti</c> / <c>celebrate</c> / <c>花</c>）。</summary>
    public bool Confetti { get; set; }

    /// <summary>条带文字；null = 不显示文字（两边斜线一并隐去，只剩边框）。</summary>
    public string? Text { get; set; }

    /// <summary>颜色词：类型名（info/warn/error 及其缩写）、颜色名（green / red / …）或 #RRGGBB。</summary>
    public string Color { get; set; } = "info";

    /// <summary>持续秒数（闪烁铺在这一段里）。与 <see cref="Services.EffectCli"/>、EffectsWindow 的默认节奏互指。</summary>
    public double Hold { get; set; } = 5;

    /// <summary>淡入秒数。</summary>
    public double FadeIn { get; set; } = 1;

    /// <summary>淡出秒数。</summary>
    public double FadeOut { get; set; } = 1;

    /// <summary>闪烁次数（只有旧语法会配；新语法固定 1）。</summary>
    public int Blinks { get; set; } = 1;

    /// <summary>要不要外围边缘高亮。</summary>
    public bool BorderOn { get; set; } = true;

    /// <summary>边缘渐变的淡化带宽度（DIP）：从屏幕边缘向内由亮渐隐的主带。</summary>
    public double BorderWidth { get; set; } = 50;

    /// <summary>主带之后的淡出延伸带宽度（DIP）：亮度继续收到 0 的收尾段。</summary>
    public double BorderFade { get; set; } = 30;

    /// <summary>边框亮度循环周期（秒）：总持续时间内自动在明暗之间循环呼吸；0 = 不循环。</summary>
    public double BorderCycle { get; set; } = 1;

    /// <summary>条带文字字号（DIP）；斜线高度按字号推，二者始终等高。</summary>
    public double FontSize { get; set; } = 46;

    /// <summary>来源平台标记（<c>-from qoder</c>）：消息栈的小圆牌用它配 IDE 图标；自由词不校验，没写就不贴牌。</summary>
    public string? Source { get; set; }
    
    /// <summary>
    /// 识别符（<c>-tag ups-loss</c>）：重播警告得能被找回来。<see cref="Kill"/> 就靠它精确杀，
    /// 没写 tag 时退到用 <c>-any</c> 在正文里做子串匹配。
    /// </summary>
    public string? Tag { get; set; }
    
    /// <summary>紧急档（<c>-s emergency</c> 或单独的 <c>-emergency</c>）：走紧急通道、画自绘三角感叹号。</summary>
    public bool Urgent { get; set; }
    
    /// <summary>重播间隔秒（<c>-replay 3 3</c> 的第一个数）；0 = 不重播。</summary>
    public double ReplayInterval { get; set; }
    
    /// <summary>
    /// 重播次数（<c>-replay 3 3</c> 的第二个数）。没写就是无限（<see cref="InfiniteReplay"/>），
    /// 直到被 <c>xa -k</c> 杀下来——无限重播只该出现在“人不来处理就不会停”的告警上。
    /// </summary>
    public int ReplayTimes { get; set; }
    
    /// <summary>无限重播的哨兵值。</summary>
    public const int InfiniteReplay = -1;
    
    /// <summary>杀除动词（<c>-k</c> / <c>-kill</c>）：按 tag、通道、正文子串挑目标停止。</summary>
    public bool Kill { get; set; }
    
    /// <summary><c>-any</c> 后面那段：kill 时在正文里找的子串（没给选择条件就是“全停”）。</summary>
    public string? MatchAny { get; set; }
    
    /// <summary>本条效果占屏多久（秒）：队列拿它算“什么时候可以播下一条”。</summary>
    public double ScreenSeconds => FadeIn + Hold + FadeOut;

    /// <summary>数字参数缺省值（与上面的属性默认值一一对应，改一处要同步另一处）。</summary>
    private static readonly double[] ShowDefaults = [5, 1, 1];
    private static readonly double[] BorderDefaults = [50, 30, 1];

    /// <summary>颜色名表：常见色各给一支现代色板色。十六进制直接写 <c>#RRGGBB</c> 也可以。</summary>
    private static readonly IReadOnlyDictionary<string, string> NamedPalette =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = "#EF4444", ["orange"] = "#F97316", ["amber"] = "#F59E0B",
            ["yellow"] = "#EAB308", ["lime"] = "#84CC16", ["green"] = "#22C55E",
            ["teal"] = "#14B8A6", ["cyan"] = "#06B6D4", ["blue"] = "#3B82F6",
            ["indigo"] = "#6366F1", ["purple"] = "#A855F7", ["magenta"] = "#EC4899",
            ["pink"] = "#F472B6", ["white"] = "#FFFFFF", ["gray"] = "#9CA3AF", ["grey"] = "#9CA3AF",
        };

    /// <summary>收起用的词：与 <see cref="SlashParser.IsHide"/> 同一张表。</summary>
    private static readonly string[] HideWords = ["off", "hide", "stop", "clear", "close", "x"];

    /// <summary>紧急档的词：颜色走 danger，通道走紧急，两侧多画一个三角感叹号。</summary>
    private static readonly string[] UrgentWords = ["emergency", "emerg", "urgent", "紧急"];

    /// <summary>撒花用的词。</summary>
    private static readonly string[] ConfettiWords = ["confetti", "celebrate", "花"];

    /// <summary>杀除动词的词。</summary>
    private static readonly string[] KillWords = ["-k", "-kill", "kill"];

    /// <summary>新语法的段开关。任一个出现就走新解析，否则整条按旧语法读。</summary>
    private static readonly string[] SectionSwitches =
        ["-s", "-border", "-lable", "-label", "-from", "-tag", "-replay", "-any", "-emergency", "-urgent", "-k", "-kill"];

    /// <summary>
    /// 解析一整行命令。返回 false = 不是已知指令（整条跳过，不猜、不弹、不报错），
    /// 调用方（无头入口）就当这轮不是来放效果的。
    /// </summary>
    public static bool TryParse(string line, out EffectCommand command)
    {
        command = new EffectCommand();
        string[] tokens = (line ?? "").Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(token => token.Equals("--fx", StringComparison.OrdinalIgnoreCase)
                             || token.Equals("xa", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (tokens.Length == 0) return false;

        if (HideWords.Contains(tokens[0], StringComparer.OrdinalIgnoreCase)) { command.Hide = true; return true; }
        if (ConfettiWords.Contains(tokens[0], StringComparer.OrdinalIgnoreCase)) { command.Confetti = true; return true; }
        // 裸词 kill 也能起头：`xa kill -tag ups-loss` 与 `xa -k -tag ups-loss` 同义
        if (KillWords.Contains(tokens[0], StringComparer.OrdinalIgnoreCase))
        {
            command.Kill = true;
            return tokens.Length == 1 || ParseSections(tokens[1..], command);
        }

        // 有段开关就是新语法；否则按旧语法（<类型> <时间-次数> <正文>）读，旧行为一字不改
        if (tokens.Any(IsSectionSwitch))
            return ParseSections(tokens, command);
        return ParseLegacy(tokens, command);
    }

    /// <summary>
    /// 旧语法：<c>warn 3 AI Computer Use</c>、<c>e 1.5-3 编译失败</c>。用时长的三分之一做首尾淡入淡出，
    /// 与新一轮默认节奏（1/5/1）在 7 秒总长时完全一致。
    /// </summary>
    private static bool ParseLegacy(string[] tokens, EffectCommand command)
    {
        if (!SlashParser.TryParse(string.Join(" ", tokens), out SlashCommand legacy)) return false;
        command.Color = SlashToneName(legacy.Tone);
        command.Text = legacy.Text;
        command.Blinks = legacy.Blinks;
        double fade = Math.Min(1, legacy.Seconds / 3);
        command.FadeIn = fade;
        command.FadeOut = fade;
        command.Hold = Math.Max(0.1, legacy.Seconds - fade * 2);
        return true;
    }

    /// <summary>新语法：按段收集，逐段解析。<c>-s</c> 是必须的入口段之一。</summary>
    private static bool ParseSections(string[] tokens, EffectCommand command)
    {
        var show = new List<string>();
        var border = new List<string>();
        var label = new List<string>();
        var from = new List<string>();
        var tag = new List<string>();
        var replay = new List<string>();
        var any = new List<string>();
        List<string>? current = null;
        foreach (string token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "-s": current = show; break;
                case "-border": current = border; break;
                case "-lable" or "-label": current = label; break;
                case "-from": current = from; break;
                case "-tag": current = tag; break;
                case "-replay": current = replay; break;
                case "-any": current = any; break;
                // 无参开关：紧急档既能在 -s 里用颜色词表达，也能这样单独挂上（kill 的选择器靠它限定通道）
                case "-emergency" or "-urgent": command.Urgent = true; current = null; break;
                case "-k" or "-kill": command.Kill = true; current = null; break;
                default:
                    if (current is null) return false;   // 段开关之外的裸内容：不是已知语法
                    current.Add(token);
                    break;
            }
        }
        if (from.Count > 0) command.Source = from[0].ToLowerInvariant();   // 多写只认第一个：平台名就一个词
        if (tag.Count > 0) command.Tag = tag[0];                           // tag 是个词，多写也只认第一个
        if (any.Count > 0) command.MatchAny = string.Join(" ", any);
        // -k 一条不需要节奏/带宽：给不出正文也不当错（“全停”就是合法命令）
        if (command.Kill) { command.Text = any.Count > 0 ? string.Join(" ", any) : null; return true; }
        return ParseReplay(replay, command)
            && ParseShow(show, command) && ParseBorder(border, command) && ParseLabel(label, command);
    }

    /// <summary>
    /// <c>-replay &lt;间隔秒&gt; [次数]</c>。只给间隔就是无限重播（靠 <c>xa -k</c> 收）；
    /// 间隔限在 0.5–3600 秒，次数限在 1–999——没意思的 0 次和一万次都不如不写。
    /// </summary>
    private static bool ParseReplay(List<string> args, EffectCommand command)
    {
        if (args.Count == 0) return true;
        if (!TryNumber(args[0], out double interval) || interval <= 0) return false;
        command.ReplayInterval = Math.Clamp(interval, 0.5, 3600);
        if (args.Count == 1) { command.ReplayTimes = InfiniteReplay; return true; }
        // 重播次数始为整数；写 0 等于不重播
        if (args.Count > 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int times)) return false;
        command.ReplayTimes = times <= 0 ? 0 : Math.Clamp(times, 1, 999);
        return true;
    }

    /// <summary><c>-s &lt;色&gt; [持续 [淡入 [淡出]]]</c>。首个词是颜色就吃颜色，剩下的按位置读时间。</summary>
    private static bool ParseShow(List<string> args, EffectCommand command)
    {
        int at = 0;
        if (at < args.Count && !IsNumber(args[at]))
        {
            // emergency 不是色名而是档位：接住它，颜色固定走 danger，同时开紧急档
            if (UrgentWords.Contains(args[at], StringComparer.OrdinalIgnoreCase))
            {
                command.Urgent = true;
                command.Color = "error";
                at++;
            }
            else
            {
                if (!IsKnownColor(args[at])) return false;
                command.Color = NormalizeColor(args[at]);
                at++;
            }
        }
        if (command.Urgent && at == 0) command.Color = "error";   // 只写了 -emergency 没写 -s 时也是红
        double[] slots = (double[])ShowDefaults.Clone();
        for (int slot = 0; at < args.Count; slot++, at++)
        {
            if (slot > 2 || !TryNumber(args[at], out slots[slot])) return false;
        }
        command.Hold = Math.Clamp(slots[0], 0.1, 300);
        command.FadeIn = Math.Clamp(slots[1], 0, 120);
        command.FadeOut = Math.Clamp(slots[2], 0, 120);
        return true;
    }

    /// <summary><c>-border on|off [渐宽 [延伸 [周期]]]</c>。首个词是 on/off 就当开关，否则默认开。</summary>
    private static bool ParseBorder(List<string> args, EffectCommand command)
    {
        int at = 0;
        if (at < args.Count && TrySwitch(args[at], out bool on))
        {
            command.BorderOn = on;
            at++;
        }
        double[] slots = (double[])BorderDefaults.Clone();
        for (int slot = 0; at < args.Count; slot++, at++)
        {
            if (slot > 2 || !TryNumber(args[at], out slots[slot])) return false;
        }
        command.BorderWidth = Math.Clamp(slots[0], 1, 600);
        command.BorderFade = Math.Clamp(slots[1], 0, 600);
        command.BorderCycle = Math.Clamp(slots[2], 0, 60);
        return true;
    }

    /// <summary><c>-lable on|off [字号] [文本…]</c>。段内第一个数字永远是字号，剩下的都是文本。</summary>
    private static bool ParseLabel(List<string> args, EffectCommand command)
    {
        int at = 0;
        if (at < args.Count && TrySwitch(args[at], out bool on))
        {
            at++;
            if (!on) { command.Text = null; return true; }   // off：文字与斜线都不显示，后面的词忽略
        }
        if (at < args.Count && TryNumber(args[at], out double size))
        {
            command.FontSize = Math.Clamp(size, 8, 400);
            at++;
        }
        string text = string.Join(" ", args.Skip(at));
        command.Text = text.Length == 0 ? null : text;
        return true;
    }

    /// <summary>on/true/yes 是一组，off/false/no 是一组；不是开关词返回 false。</summary>
    private static bool TrySwitch(string token, out bool on)
    {
        if (token.Equals("on", StringComparison.OrdinalIgnoreCase) || token.Equals("true", StringComparison.OrdinalIgnoreCase)
            || token.Equals("yes", StringComparison.OrdinalIgnoreCase))
        { on = true; return true; }
        if (token.Equals("off", StringComparison.OrdinalIgnoreCase) || token.Equals("false", StringComparison.OrdinalIgnoreCase)
            || token.Equals("no", StringComparison.OrdinalIgnoreCase))
        { on = false; return true; }
        on = false;
        return false;
    }

    private static bool IsNumber(string token) => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static bool TryNumber(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>颜色词是否认识：类型词表、颜色名表、或 #RRGGBB / #AARRGGBB。</summary>
    public static bool IsKnownColor(string word) =>
        SlashParser.KnownTone(word) || NamedPalette.ContainsKey(word) || IsHex(word);

    private static bool IsHex(string word) =>
        (word.Length is 7 or 9) && word[0] == '#'
        && word.Skip(1).All(c => Uri.IsHexDigit(c));

    /// <summary>存进指令时统一小写（十六进制除外），方便夹具比对。</summary>
    private static string NormalizeColor(string word) => IsHex(word) ? word.ToUpperInvariant() : word.ToLowerInvariant();

    private static string SlashToneName(SlashTone tone) => tone switch
    {
        SlashTone.Warn => "warn",
        SlashTone.Error => "error",
        _ => "info",
    };

    /// <summary>
    /// 颜色词 → 画刷。类型词走 <see cref="SlashParser.BrushOf"/>（info 跟主题强调色走、换肤后跟着变），
    /// 颜色名与十六进制转成固定色。词不认识时返回 null，调用方接强调色兜底。
    /// </summary>
    public static Brush? BrushOf(string word)
    {
        if (string.IsNullOrEmpty(word)) return null;
        if (SlashParser.Vocabulary.TryGetValue(word, out SlashTone tone)) return SlashParser.BrushOf(tone);
        string hex = NamedPalette.TryGetValue(word, out string? named) ? named : word;
        if (!IsHex(hex)) return null;
        try { return new System.Windows.Media.SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return null; }
    }

    /// <summary>撒花词表的对外查法（无头入口用它判「这条是不是撒花」）。</summary>
    public static bool IsConfettiWord(string word) => ConfettiWords.Contains(word, StringComparer.OrdinalIgnoreCase);

    /// <summary>收起词表的对外查法（无头入口用它判「这条是不是收起」）。</summary>
    public static bool IsHideWord(string word) => HideWords.Contains(word, StringComparer.OrdinalIgnoreCase);

    /// <summary>段开头的对外查法（无头入口用它判「这条是不是新语法，该不该接管」）。</summary>
    public static bool IsSectionSwitch(string word) => SectionSwitches.Contains(word, StringComparer.OrdinalIgnoreCase);
}