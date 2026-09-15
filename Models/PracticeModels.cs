using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace XAssistant.Models;

public sealed class PracticeCatalog
{
    public int Version { get; set; } = 1;
    public List<PracticeSentence> Sentences { get; set; } = [];
    /// <summary>
    /// 加载时被黑名单洗掉的不可见字符与怪空白，按「文件 · 句 · 字段 → 码位」记下来。
    /// 洗不能静默：题库里混进了什么只有报出来才有人去改源文件。
    /// </summary>
    public List<string> IgnorableFindings { get; } = [];
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
            // 不可见字符先报：\n 也是空白，排在后面就会被「单词缺少文本」那条误报成缺字段，
            // 报错了地方人就找不到真因。判据与 PracticeText.Sanitize 同一份，不各写一套。
            var invisible = PracticeText.IgnorableCodePoints(sentence.Text);
            if (invisible.Count > 0)
                throw new InvalidDataException($"题目 {sentence.Id} 的正文含不可见字符 {string.Join("、", invisible.Distinct().Order())}，必须先过 PracticeText.Sanitize。");
            if (sentence.Words.Any(word => word == null || string.IsNullOrWhiteSpace(word.Text) || word.Text.Any(char.IsWhiteSpace)
                || string.IsNullOrWhiteSpace(word.Annotation) || string.IsNullOrWhiteSpace(word.Translation)))
                throw new InvalidDataException($"题目 {sentence.Id} 的单词缺少文本、注音或释义。");
            // 要敲的字符得是某个真实键盘布局打得出来的：ASCII 可见字符（英文布局）或西里尔字母（俄语布局）。
            // 假名、汉字、长音符这类既折叠不掉又敲不出，只会在正文里留下一格永远落不下去的死格。
            var untypable = PracticeText.Normalize(sentence.Text).Where(ch => !IsTypable(ch)).Distinct().ToArray();
            if (untypable.Length > 0)
                throw new InvalidDataException($"题目 {sentence.Id} 的正文有键盘打不出的字符「{new string(untypable)}」，请改写成 ASCII 或西里尔字母形式。");
            if (sentence.Text.Length > 300 || PracticeText.Tokens(sentence.Text).Length is < 1 or > 28) throw new InvalidDataException("每句练习最多300字符。");
        }
    }

    /// <summary>这格键敲不敲得出来：0x20–0x7E 是英文布局的可见字符与空格，0x400–0x4FF 是俄语布局的西里尔字母（含 Ё）。</summary>
    private static bool IsTypable(char ch) =>
        (ch >= 0x20 && ch <= 0x7E) || (ch >= 0x0400 && ch <= 0x04FF);
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
    /// <summary>要敲出来的形式：英文是单词本身，日语是罗马音，俄语是西里尔原文。</summary>
    public string Text { get; set; } = "";
    /// <summary>卡片上方的注音：英文是 IPA，日语是假名或汉字，俄语是拉丁转写。</summary>
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
        // 俄语的 ё 单独占 ` 键，很多人打字时直接按 е，两者必须等价，否则整句都是找不着北的红格
        'ё' => 'е', 'Ё' => 'Е',
        // 俄文引号 « » 在俄语布局里就是 Shift+2 / Shift+3，跟半角引号算同一个键位
        '«' or '»' => '"',
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

    /// <summary>
    /// 黑名单之一：一律剔除的不可见字符。控制字符整段按 <see cref="char.IsControl"/> 认
    /// （U+0000–U+001F 与 U+007F–U+009F，包括被 JSON 转义混进来的 \n \r \t 与 C1 段），
    /// 零宽、方向标记、软连字符、BOM 这些「看着是空白、其实占一格」的格式字符按码位列出来。
    /// 它们会虚增字符数，让进度永远好不到 N/N，所以正文与注音都不收。
    /// </summary>
    public static bool IsIgnorable(char c) => char.IsControl(c)
        || c is '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F'
            or '\u2060' or '\u2061' or '\u2062' or '\u2063' or '\u2064' or '\uFEFF' or '\u00AD';

    /// <summary>
    /// 黑名单之二：五花八门的怪空白。它们不该占一格，但删掉会把两个词粘成一个，所以折回半角空格
    /// （U+3000 表意空格已经在 <see cref="Fold"/> 里折成空格，不重列）。
    /// </summary>
    public static bool IsStraySpace(char c) => c is '\u00A0' or '\u1680' or '\u2007' or '\u202F' or '\u205F' or '\u180E';

    /// <summary>按黑名单洗一遍文本：不可见剔除、怪空白折成半角空格。notes 收集被改动的码位供加载器记账。</summary>
    public static string Sanitize(string? text, List<string>? notes = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var buffer = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (IsIgnorable(c)) { notes?.Add($"U+{(int)c:X4}"); continue; }
            if (IsStraySpace(c)) notes?.Add($"U+{(int)c:X4}→空格");
            buffer.Append(IsStraySpace(c) ? ' ' : c);
        }
        return buffer.ToString();
    }

    /// <summary>文本里残留的不可见字符与怪空白码位。校验用，与 <see cref="Sanitize"/> 共用同一份判据。</summary>
    public static List<string> IgnorableCodePoints(string text)
    {
        var found = new List<string>();
        Sanitize(text, found);
        return found;
    }

    /// <summary>
    /// 注音是不是「本身就要读的正文」：假名、汉字、谚文、长音符与 CJK 符号标点。
    /// 判定走排除法，不是「整串是不是 ASCII」那种白名单——英语音标里的 ː ð ʌ ə 都不在 ASCII，
    /// 白名单会把它们当成假名，斜杠就这么丢了（注音不参与要敲的字符统计，它只决定怎么显示）。
    /// </summary>
    public static bool IsReadingScript(string text) => text.Any(c =>
        c is '\u30FC' or '\u30FB' or '\uFF61' or '\uFF9E' or '\uFF9F'
        || c >= '\u2E80' && c <= '\u9FFF'     // CJK 部首、假名、汉字、CJK 标点连成一段
        || c >= '\uAC00' && c <= '\uD7A3'     // 谚文音节与谚文字母
        || c >= '\uF900' && c <= '\uFAFF');   // CJK 兼容表意文字
}
