using System;

namespace XAssistant.Services;

/// <summary>一条消息是哪一类。粒子的角标、以后的分流都读它。</summary>
public enum NoticeKind { Ask, Done, Error }

/// <summary>
/// 从消息正文读出「这条是哪一类」。
///
/// 判据是正文开头那个<b>机械回复领词</b>：hook 与 IDE 模块都按四段格式写
/// （<c>机械回复 · 现况 · 来源 · 信息</c>），领词是封闭词表（提醒 / 警告 / 故障 / 询问 / 回复），
/// 本来就是留给机器认的那一段，所以拿它当分流依据不是猜的。没有领词的手写命令与旧消息才退回
/// <c>-s</c> 的档位。
/// </summary>
public static class Notice
{
    // WPF 不吃彩色 emoji 字体：这三个字形在 Segoe UI Symbol 里都是单色轮廓，染上这条消息自己的颜色
    public const string AskGlyph = "❓", DoneGlyph = "✔", ErrorGlyph = "❌";

    /// <summary>正文第一段（第一个 <c>·</c> 之前）——四段格式里那就是机械回复领词。</summary>
    public static string? LeadWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int at = text.IndexOf('·');
        string lead = (at < 0 ? text : text[..at]).Trim();
        return lead.Length == 0 ? null : lead;
    }

    /// <summary>领词认得出的那几类（认不出来返回 null，交给档位兜底）。</summary>
    public static NoticeKind? KindOf(string? text) => LeadWord(text) switch
    {
        "询问" => NoticeKind.Ask,
        "回复" => NoticeKind.Done,
        "故障" or "警告" or "提醒" => NoticeKind.Error,
        _ => null,
    };

    /// <summary>这条命令的角标字形；只亮边框（没有正文）就不放粒子，返回 null。</summary>
    public static string? GlyphOf(EffectCommand command) =>
        GlyphOf(command.Text, command.Urgent, command.Color);

    public static string? GlyphOf(string? text, bool urgent, string color)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return (KindOf(text) ?? KindOfLevel(urgent, color)) switch
        {
            NoticeKind.Ask => AskGlyph,
            NoticeKind.Done => DoneGlyph,
            _ => ErrorGlyph,
        };
    }

    /// <summary>档位兜底：紧急与 error 档是错误，warn 是「要人看一眼」，其余算完成。</summary>
    public static NoticeKind KindOfLevel(bool urgent, string color)
    {
        if (urgent) return NoticeKind.Error;
        return color.Trim().ToLowerInvariant() switch
        {
            "error" or "err" or "e" or "red" or "danger" => NoticeKind.Error,
            "warn" or "warning" or "w" or "amber" or "orange" => NoticeKind.Ask,
            _ => NoticeKind.Done,
        };
    }
}
