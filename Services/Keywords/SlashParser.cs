using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Brush / Brushes / Color 会跟 WPF 的撞名
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace XAssistant.Services.Keywords;

/// <summary>警告语的颜色档：只这三档，别的词不算命令。</summary>
public enum SlashTone { Info, Warn, Error }

/// <summary>
/// 一条斜杠命令：显示多久、闪几下、什么颜色、写什么。
/// <paramref name="Hide"/> 为真表示「收起当前那条」，此时其余字段无意义。
/// </summary>
public readonly record struct SlashCommand(SlashTone Tone, double Seconds, int Blinks, string Text, bool Hide);

/// <summary>
/// 斜杠命令的语法：<c>/warn 3 AI Computer Use</c>、<c>/e 1.5-3 编译失败</c>、<c>/i 跑完了</c>，
/// 光一个 <c>/</c> 收起当前那条。三段依次是「类型 → 显示时间-闪烁次数 → 正文」，空格分隔。
///
/// 次数段可以只写一个数（<c>3</c> = 默认显示时长、闪 3 下）；带横杠才是两段（<c>1.5-3</c>）。
/// 类型词表是封闭的：斜杠后第一个词不在表里，整条就直接丢掉等下一个斜杠，
/// 不会拿它当命令反复重试——否则用户敲一句 <c>/me</c> 之类就会被半生不熟的解析结果误伤。
/// </summary>
public static class SlashParser
{
    /// <summary>不写时长时的默认显示秒数。</summary>
    public const double DefaultSeconds = 2.4;

    /// <summary>闪烁次数上限：再多就成频闪，看着难受也不礼貌。</summary>
    public const int MaxBlinks = 5;

    /// <summary>斜杠后只认这几个词（大小写无所谓）。表外的一律「不是命令」，直接跳过整条。</summary>
    public static readonly IReadOnlyDictionary<string, SlashTone> Vocabulary =
        new Dictionary<string, SlashTone>(StringComparer.OrdinalIgnoreCase)
        {
            ["info"] = SlashTone.Info, ["log"] = SlashTone.Info, ["i"] = SlashTone.Info, ["l"] = SlashTone.Info,
            ["warn"] = SlashTone.Warn, ["warning"] = SlashTone.Warn, ["w"] = SlashTone.Warn,
            ["error"] = SlashTone.Error, ["err"] = SlashTone.Error, ["e"] = SlashTone.Error, ["r"] = SlashTone.Error,
        };

    /// <summary>第一个词是不是已知类型。用于「一见到空格就定性」，不匹配的立刻放弃。</summary>
    public static bool KnownTone(string firstToken) => Vocabulary.ContainsKey(firstToken);

    /// <summary>解析一整行（不含前导斜杠）。返回 false = 不是已知命令或没正文，调用方直接丢掉这条。</summary>
    public static bool TryParse(string line, out SlashCommand command)
    {
        command = default;
        string[] parts = (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (!Vocabulary.TryGetValue(parts[0], out var tone)) return false;
        if (parts.Length < 2) return false;   // 只有类型没有正文：不显示空条带，也当没匹配上跳过

        double seconds = DefaultSeconds;
        int blinks = 1;
        int textFrom = 1;
        // 第二段长得像「秒」「秒-下数」或「下数」才当参数，否则整段都是正文
        if (TryReadSpec(parts[1], out double specSeconds, out int specBlinks, out bool specHasSeconds))
        {
            seconds = specHasSeconds ? specSeconds : DefaultSeconds;
            blinks = specBlinks;
            textFrom = 2;
        }
        string text = string.Join(" ", parts.Skip(textFrom));
        if (text.Length == 0) return false;

        command = new SlashCommand(tone, Math.Clamp(seconds, 0.6, 30), Math.Clamp(blinks, 1, MaxBlinks), text, false);
        return true;
    }

    /// <summary>收起当前那条：光一个斜杠，或 <c>/off</c>、<c>/clear</c>、<c>/stop</c>。</summary>
    public static bool IsHide(string line) =>
        new[] { "", "off", "stop", "clear", "close", "x" }.Contains((line ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 参数段。<c>3</c> = 闪 3 下、用默认时长；<c>1.5-3</c> = 显示 1.5 秒、闪 3 下。
    /// 返回 false 表示这一段不是参数（那就是正文的开头）。
    /// </summary>
    private static bool TryReadSpec(string token, out double seconds, out int blinks, out bool hasSeconds)
    {
        seconds = DefaultSeconds;
        blinks = 1;
        hasSeconds = false;
        string[] halves = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (halves.Length > 2) return false;
        if (halves.Length == 2)
        {
            if (!double.TryParse(halves[0], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                !int.TryParse(halves[1], out blinks)) return false;
            hasSeconds = true;
            return true;
        }
        // 只有一段：光一个数就是闪烁次数（时长用默认），光一个小数就是时长（闪一下）
        if (int.TryParse(halves[0], out blinks)) return true;
        if (double.TryParse(halves[0], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            blinks = 1;
            hasSeconds = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 这一档用什么颜色。error 走主题的 DangerBrush，info 走当前强调色（换肤后跟着变）；
    /// warn 需要一支琥珀黄——两套主题里都没有现成的警告色，就按字面意思给一支，深色浅色界面上都读得清。
    /// </summary>
    public static Brush BrushOf(SlashTone tone)
    {
        var app = System.Windows.Application.Current;
        return tone switch
        {
            SlashTone.Error => app?.TryFindResource("DangerBrush") as Brush ?? Brushes.OrangeRed,
            SlashTone.Warn => new SolidColorBrush(Color.FromRgb(0xFF, 0xC5, 0x3D)),
            _ => app?.TryFindResource("AccentBrush") as Brush ?? Brushes.Gainsboro,
        };
    }
}
