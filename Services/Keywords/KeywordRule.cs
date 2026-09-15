using System.Collections.Generic;

namespace XAssistant.Services.Keywords;

/// <summary>
/// 一条关键词规则：敲到 <see cref="Word"/> 这个词时该干什么。
/// 三件事各自独立：换肤（<see cref="Theme"/> / <see cref="Colors"/>）、
/// 屏幕中间的警告带文案（<see cref="Banner"/>）、从顶部浮岛下方吐粒子（<see cref="Glyph"/> / <see cref="Image"/>）。
/// 只写一项就只做那一项。
/// </summary>
public sealed record KeywordRule
{
    /// <summary>触发词，一律小写；比较时忽略大小写与首尾空白。</summary>
    public required string Word { get; init; }

    /// <summary>要切到的主题：Light / Dark。留空表示不动主题。</summary>
    public string? Theme { get; init; }

    /// <summary>
    /// 自定义色板：画刷键 → 颜色（#RGB / #RRGGBB / #AARRGGBB）。留空表示就用主题原色。
    /// 键必须落在 <see cref="ThemeManager.BrushKeys"/> 里，认不出的键会被加载器丢掉——
    /// 垫底仍是 <see cref="Theme"/>（没写就沿用当前主题）。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Colors { get; init; }

    /// <summary>色板名，只用于日志与自检断言。</summary>
    public string? Palette { get; init; }

    /// <summary>
    /// 掉下来的粒子字形。WPF 不支持彩色 emoji 字体，所以这些一律是单色轮廓、跟着强调色着色；
    /// 要彩色就配 <see cref="Image"/>。留空且没配图表示这条规则不吐粒子。
    /// </summary>
    public string Glyph { get; init; } = "";

    /// <summary>图片文件名，按 Assets/Keywords/images/ 下相对路径解析；找不到就退回字形。</summary>
    public string? Image { get; init; }

    /// <summary>一次触发吐几颗。默认一颗就够看：同屏几十颗既费渲染，又像撒了把沙子。</summary>
    public int Particles { get; init; } = 1;

    /// <summary>一次触发最多吐几颗：写再多也夹到这里，同屏上限另看效果层。</summary>
    public const int MaxParticlesPerTrigger = 4;

    /// <summary>
    /// 触发时横在屏幕中间那条警告带上写什么，例：「AI接管中」——
    /// 两侧由主题色拼出的斜线会把它夹成「//// AI接管中 ////」那种效果。
    /// 留空则回退到词本身；过长的部分会被截到 <see cref="MaxBannerLength"/>。
    /// </summary>
    public string? Banner { get; init; }

    /// <summary>警告带文案的长度上限：带子只有 36 DIP 高，写长了只能裁字。</summary>
    public const int MaxBannerLength = 40;

    /// <summary>这条规则要不要动外观（主题或色板）。</summary>
    public bool ChangesTheme => Theme != null || Colors is { Count: > 0 };

    /// <summary>这条规则要不要吐粒子。</summary>
    public bool HasParticle => Glyph.Length > 0 || Image != null;
}
