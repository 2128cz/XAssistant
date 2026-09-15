using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XAssistant.Services.Keywords;

/// <summary>
/// 关键词表：代码里内置一份最小可用的规则，再读 <c>Assets/Keywords/keywords.json</c> 追加或覆盖
/// （按词比对，忽略大小写）。内置写在代码里是为了「资源没拷全也不至于什么都不发生」，
/// JSON 是给用户自己加词留的入口——加词不必改代码，也不必懂 C#。
/// </summary>
public sealed class KeywordCatalog
{
    private readonly Dictionary<string, KeywordRule> _byWord = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON 里被丢掉的坏条目（打错的主题名、不认识的画刷键、带空格的词……），加载时逐条记下来。</summary>
    public List<string> LoadNotes { get; } = [];

    public IReadOnlyCollection<KeywordRule> Rules => _byWord.Values;

    /// <summary>关键词表默认的落点：输出目录下的 Assets/Keywords。</summary>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "Assets", "Keywords");

    public static KeywordCatalog Load(string root)
    {
        var catalog = new KeywordCatalog();
        foreach (var rule in BuiltIns) catalog.Add(rule);

        string file = Path.Combine(root, "keywords.json");
        if (!File.Exists(file))
            return catalog;

        try
        {
            var parsed = JsonSerializer.Deserialize<KeywordFile>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var dto in parsed?.Keywords ?? [])
            {
                var rule = dto.ToRule(root, catalog.LoadNotes);
                if (rule != null) catalog.Add(rule);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            catalog.LoadNotes.Add($"keywords.json 没读成（{error.GetType().Name}），只用内置表：{error.Message}");
        }
        return catalog;
    }

    public KeywordRule? Match(string word) =>
        word.Length > 0 && _byWord.TryGetValue(word.Trim(), out var rule) ? rule : null;

    /// <summary>同一个词后写的覆盖先写的：JSON 里改一条内置规则就能改行为，不必动代码。</summary>
    private void Add(KeywordRule rule) => _byWord[rule.Word] = rule;

    /// <summary>
    /// 内置规则。词一律小写：切词时已经转过小写，比对再忽略大小写只是兜底。
    /// 只收 ASCII 词——中文输入法下词流拿到的是拼音字母（与词频统计同一条物理键规则），
    /// 写「花」永远命中不了，想触发就配 "hua"。
    /// 粒子数默认不写就是 1 颗（浮岛一次只吐一颗），只有雪、雨这种「就是要下一把」的才给 2。
    /// banner 是横在屏幕中间那条警告带上的字，不写就显示词本身。
    /// </summary>
    private static readonly KeywordRule[] BuiltIns =
    [
        new() { Word = "white", Theme = ThemeManager.Light, Glyph = "🤍", Banner = "已切到浅色主题" },
        new() { Word = "black", Theme = ThemeManager.Dark, Glyph = "🖤", Banner = "已切到深色主题" },
        new() { Word = "dark", Theme = ThemeManager.Dark, Glyph = "🌑" },
        new() { Word = "flower", Glyph = "🌸", Banner = "送你一朵花" },
        new() { Word = "fuck", Glyph = "🖕", Banner = "🖕" },
        new() { Word = "cat", Glyph = "🐱", Banner = "喵" },
        new() { Word = "dog", Glyph = "🐶", Banner = "汪" },
        new() { Word = "heart", Glyph = "❤" },
        new() { Word = "star", Glyph = "★", Particles = 2 },
        new() { Word = "fire", Glyph = "🔥" },
        new() { Word = "snow", Glyph = "❄", Particles = 2 },
        new() { Word = "rain", Glyph = "💧", Particles = 2 },
        new() { Word = "moon", Glyph = "🌙" },
        new() { Word = "sun", Glyph = "☀" },
        new() { Word = "rocket", Glyph = "🚀" },
        new() { Word = "coffee", Glyph = "☕" },
        new() { Word = "beer", Glyph = "🍺" },
        new() { Word = "money", Glyph = "💰" },
        new() { Word = "music", Glyph = "♪" },
        new() { Word = "skull", Glyph = "☠" },
        // 只写字、不换肤也不掉粒子的例子：警告带单独当提示用
        new() { Word = "ai", Glyph = "🤖", Banner = "AI接管中" },
        // 自定义色板的样例：整张界面换成矩阵绿，走的仍是同一套程序化覆盖通道
        new()
        {
            Word = "matrix", Theme = ThemeManager.Dark, Palette = "matrix", Glyph = "▚",
            Banner = "矩阵模式 · 敲 black / white 回到原色",
            Colors = new Dictionary<string, string>
            {
                ["AccentBrush"] = "#39FF14",
                ["AccentText"] = "#041004",
                ["LinkBrush"] = "#39FF14",
                ["SuccessBrush"] = "#39FF14",
                ["HighlightNumber"] = "#39FF14",
                ["TextPrimary"] = "#D6FFD2",
                ["TextSecondary"] = "#7FD27A",
                ["TextHint"] = "#5C9C58",
                ["KeyChipText"] = "#B7FFB0",
                ["DashboardNumber"] = "#B339FF14",
                ["DashboardWatermark"] = "#2639FF14",
            },
        },
    ];

    private sealed class KeywordFile
    {
        public int Version { get; set; } = 1;
        public string Note { get; set; } = "";
        public List<KeywordDto> Keywords { get; set; } = [];
    }

    private sealed class KeywordDto
    {
        public string Word { get; set; } = "";
        public string? Theme { get; set; }
        public string? Palette { get; set; }
        public Dictionary<string, string>? Colors { get; set; }
        public string? Glyph { get; set; }
        public string? Image { get; set; }
        public int Particles { get; set; } = 1;
        public string? Banner { get; set; }

        /// <summary>校验放在这里：JSON 是人手写的，坏条目只能丢掉并说明原因，不能带进运行时。</summary>
        public KeywordRule? ToRule(string root, List<string> notes)
        {
            string word = (Word ?? "").Trim().ToLowerInvariant();
            if (word.Length == 0 || word.Any(char.IsWhiteSpace))
            {
                notes.Add($"丢掉一条规则：词「{Word}」是空的或带空格，切词后永远匹配不上");
                return null;
            }

            string? theme = null;
            if (Theme is { Length: > 0 })
            {
                if (Theme.Equals(ThemeManager.Light, StringComparison.OrdinalIgnoreCase)) theme = ThemeManager.Light;
                else if (Theme.Equals(ThemeManager.Dark, StringComparison.OrdinalIgnoreCase)) theme = ThemeManager.Dark;
                else notes.Add($"丢掉「{word}」的主题「{Theme}」：只认 Light / Dark");
            }

            Dictionary<string, string>? colors = null;
            foreach ((string key, string value) in Colors ?? [])
            {
                if (!ThemeManager.BrushKeys.Contains(key, StringComparer.Ordinal))
                {
                    notes.Add($"丢掉「{word}」的色板键「{key}」：不是主题画刷键");
                    continue;
                }
                colors ??= new Dictionary<string, string>(StringComparer.Ordinal);
                colors[key] = value;
            }

            string? image = null;
            if (Image is { Length: > 0 })
            {
                string path = Path.IsPathRooted(Image) ? Image : Path.Combine(root, "images", Image);
                if (File.Exists(path)) image = path;
                else notes.Add($"丢掉「{word}」的图片「{Image}」：找不到文件（{path}）");
            }

            string glyph = (Glyph ?? "").Trim();
            // 一长串文字掉下来不叫粒子，截一下
            if (glyph.Length > 8) glyph = glyph[..8];
            // 带子只有 36 DIP 高，写长了只能裁字：截断并说明，别默默显示半句
            string? banner = (Banner ?? "").Trim();
            if (banner.Length > KeywordRule.MaxBannerLength)
            {
                notes.Add($"「{word}」的警告带文案截到 {KeywordRule.MaxBannerLength} 字（原来 {banner.Length} 字）");
                banner = banner[..KeywordRule.MaxBannerLength];
            }

            if (theme == null && colors == null && glyph.Length == 0 && image == null && banner.Length == 0)
            {
                notes.Add($"丢掉「{word}」：既不换肤、不吐粒子也不写字，这条规则什么都不做");
                return null;
            }

            return new KeywordRule
            {
                Word = word,
                Theme = theme,
                Palette = Palette,
                Colors = colors,
                Glyph = glyph,
                Image = image,
                Particles = Math.Clamp(Particles, 1, KeywordRule.MaxParticlesPerTrigger),
                Banner = banner.Length == 0 ? null : banner,
            };
        }
    }
}
