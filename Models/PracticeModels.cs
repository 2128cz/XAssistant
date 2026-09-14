using System.Text.RegularExpressions;
using System.IO;

namespace XAssistant.Models;

public sealed class PracticeCatalog
{
    public int Version { get; set; } = 1;
    public List<PracticeSentence> Sentences { get; set; } = [];
    public void Validate()
    {
        if (Version != 1 || Sentences is not { Count: > 0 }) throw new InvalidDataException("题库版本不支持或没有题目。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sentence in Sentences)
        {
            if (sentence == null || string.IsNullOrWhiteSpace(sentence.Id) || !ids.Add(sentence.Id)
                || string.IsNullOrWhiteSpace(sentence.Translation) || string.IsNullOrWhiteSpace(sentence.Category) || string.IsNullOrWhiteSpace(sentence.Source) || sentence.Words is not { Count: > 0 })
                throw new InvalidDataException("题目必须有唯一 ID、译文和单词。");
            if (sentence.Words.Any(word => word == null || string.IsNullOrWhiteSpace(word.Text) || word.Text.Any(char.IsWhiteSpace)
                || string.IsNullOrWhiteSpace(word.Ipa) || string.IsNullOrWhiteSpace(word.Translation)))
                throw new InvalidDataException($"题目 {sentence.Id} 的单词缺少文本、音标或释义。");
            if (sentence.Text.Length > 300 || PracticeText.Tokens(sentence.Text).Length is < 1 or > 28) throw new InvalidDataException("每句练习最多300字符。");
        }
    }
}
public sealed class PracticeSentence
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Translation { get; set; } = "";
    public List<PracticeWord> Words { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string Text => string.Join(" ", Words.Select(word => word.Text + word.Suffix));
}
public sealed class PracticeWord
{
    public string Text { get; set; } = "";
    public string Ipa { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Suffix { get; set; } = "";
}
public sealed class PracticeResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.Now;
    public string SentenceId { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Score { get; set; }
    public int Mistakes { get; set; }
    public double Accuracy { get; set; }
    public double Seconds { get; set; }
}
public sealed class PracticeLedger
{
    public int Version { get; set; } = 1;
    public long TotalScore { get; set; }
    public int CompletedRounds { get; set; }
    public List<PracticeResult> RecentResults { get; set; } = [];
}
public static class PracticeText
{
    // 长度不变的 1:1 折叠。除弯引号与破折号外，还要接住中文输入法全角／标点状态下
    // 产出的字符（“。”“，”“４２”等）：练习的输入直接来自键盘钩子，不折叠这些字符就永远对
    // 不上正文，句子会停在最后一个字符上。
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var buffer = new char[text.Length];
        for (int i = 0; i < text.Length; i++) buffer[i] = Fold(text[i]);
        return new string(buffer);
    }
    private static char Fold(char c) => c switch
    {
        '’' or '‘' or '‛' or '‚' => '\'',
        '“' or '”' or '„' => '"',
        '–' or '—' or '―' or '‐' => '-',
        '。' => '.',
        '、' or '，' => ',',
        '？' => '?',
        '！' => '!',
        '；' => ';',
        '：' => ':',
        '（' => '(',
        '）' => ')',
        '〔' or '［' => '[',
        '〕' or '］' => ']',
        '〈' or '《' => '<',
        '〉' or '》' => '>',
        '～' => '~',
        '　' => ' ', // U+3000 表意空格：不计入长度差异才能与正文逐位对齐
        >= '\uFF01' and <= '\uFF5E' => (char)(c - 0xFEE0), // 全角 ASCII：标点、数字、字母一律转半角
        _ => c,
    };
    public static string[] Tokens(string text) => Regex.Matches(Normalize(text), @"[\p{L}\p{N}]+(?:'[\p{L}\p{N}]+)*")
        .Select(match => match.Value.ToLowerInvariant()).ToArray();
    public static bool Equal(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>单字符折叠。逐键落子时不必为每个字符新建一个字符串。</summary>
    public static char FoldChar(char c) => Fold(c);

    /// <summary>逐位比对：折叠后只区分大小写，与 <see cref="Equal"/> 的口径一致。</summary>
    public static bool EqualChar(char left, char right) => char.ToUpperInvariant(Fold(left)) == char.ToUpperInvariant(Fold(right));
}
