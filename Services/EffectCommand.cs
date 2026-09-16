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

    /// <summary>撒花用的词。</summary>
    private static readonly string[] ConfettiWords = ["confetti", "celebrate", "花"];

    /// <summary>新语法的段开关。任一个出现就走新解析，否则整条按旧语法读。</summary>
    private static readonly string[] SectionSwitches = ["-s", "-border", "-lable", "-label"];

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
        List<string>? current = null;
        foreach (string token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "-s": current = show; break;
                case "-border": current = border; break;
                case "-lable" or "-label": current = label; break;
                default:
                    if (current is null) return false;   // 段开关之外的裸内容：不是已知语法
                    current.Add(token);
                    break;
            }
        }
        return ParseShow(show, command) && ParseBorder(border, command) && ParseLabel(label, command);
    }

    /// <summary><c>-s &lt;色&gt; [持续 [淡入 [淡出]]]</c>。首个词是颜色就吃颜色，剩下的按位置读时间。</summary>
    private static bool ParseShow(List<string> args, EffectCommand command)
    {
        int at = 0;
        if (at < args.Count && !IsNumber(args[at]))
        {
            if (!IsKnownColor(args[at])) return false;
            command.Color = NormalizeColor(args[at]);
            at++;
        }
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