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
                || string.IsNullOrWhiteSpace(sentence.Translation) || string.IsNullOrWhiteSpace(sentence.Source) || sentence.Words is not { Count: > 0 })
                throw new InvalidDataException("题目必须有唯一 ID、译文和单词。");
            // 分类以文件位置为凭：语言 = 一级目录，句式 = 文件名。绕过加载器拼出来的题库视为脏数据。
            if (string.IsNullOrWhiteSpace(sentence.Language) || string.IsNullOrWhiteSpace(sentence.Category))
                throw new InvalidDataException($"题目 {sentence.Id} 缺少语言或句式分类，必须由题库目录结构赋值。");
            if (sentence.Words.Any(word => word == null || string.IsNullOrWhiteSpace(word.Text) || word.Text.Any(char.IsWhiteSpace)
                || string.IsNullOrWhiteSpace(word.Annotation) || string.IsNullOrWhiteSpace(word.Translation)))
                throw new InvalidDataException($"题目 {sentence.Id} 的单词缺少文本、注音或释义。");
            // 要敲的字符必须是键盘上打得出来的 ASCII；否则这个句子永远敲不完，只能靠回车跳过。
            if (sentence.Text.Any(ch => ch < 0x20 || ch > 0x7E))
                throw new InvalidDataException($"题目 {sentence.Id} 的正文含不可打印或非 ASCII 字符，请改写成键盘能打出的形式。");
            if (sentence.Text.Length > 300 || PracticeText.Tokens(sentence.Text).Length is < 1 or > 28) throw new InvalidDataException("每句练习最多300字符。");
        }
    }
}

/// <summary>
/// 题库文件的契约：一个 JSON = 一个句式，它属于哪门语言由所在的目录决定。
/// JSON 里不再重复写语言与句式，避免目录和字段两边各说一套。
/// </summary>
public sealed class PracticeCatalogFile
{
    public int Version { get; set; } = 1;
    /// <summary>句式显示名，缺省用文件名。</summary>
    public string Name { get; set; } = "";
    /// <summary>拼写约定说明，显示在筛选下拉的悬停提示里。</summary>
    public string Note { get; set; } = "";
    /// <summary>遮住待敲字母、只留注音：看假名回忆罗马音的闭卷模式。</summary>
    public bool Mask { get; set; }
    public List<PracticeSentence> Sentences { get; set; } = [];
}
public sealed class PracticeSentence
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Translation { get; set; } = "";
    public List<PracticeWord> Words { get; set; } = [];
    /// <summary>语言名，取题库文件所在的一级目录，由加载器写入。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Language { get; set; } = "";
    /// <summary>句式名，取题库文件名，由加载器写入。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Category { get; set; } = "";
    /// <summary>是否遮住待敲字母，取自题库文件。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Mask { get; set; }
    /// <summary>本句式的拼写约定说明，取自题库文件。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Note { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Text => string.Join(" ", Words.Select(word => word.Text + word.Suffix));
}
public sealed class PracticeWord
{
    /// <summary>要敲出来的形式：英文是单词本身，日语是罗马音。</summary>
    public string Text { get; set; } = "";
    /// <summary>卡片上方的注音：英文是 IPA，日语是假名或汉字。</summary>
    public string Annotation { get; set; } = "";
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
