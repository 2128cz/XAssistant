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
    public static string Normalize(string text) => text.Replace('’', '\'').Replace('‘', '\'')
        .Replace('“', '"').Replace('”', '"').Replace('–', '-').Replace('—', '-');
    public static string[] Tokens(string text) => Regex.Matches(Normalize(text), @"[\p{L}\p{N}]+(?:'[\p{L}\p{N}]+)*")
        .Select(match => match.Value.ToLowerInvariant()).ToArray();
    public static bool Equal(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
