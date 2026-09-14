using System.IO;
using System.Text.Json;
using XAssistant.Models;

namespace XAssistant.Services;

public interface IPracticeScoreStore
{
    PracticeLedger Load();
    void Save(PracticeLedger ledger);
}
public sealed class PracticeScoreStore(string filePath) : IPracticeScoreStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public PracticeLedger Load()
    {
        if (!File.Exists(filePath)) return new();
        var ledger = JsonSerializer.Deserialize<PracticeLedger>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidDataException("成绩文件为空。");
        if (ledger.Version != 1 || ledger.TotalScore < 0 || ledger.CompletedRounds < 0 || ledger.RecentResults == null)
            throw new InvalidDataException("成绩文件格式不正确。");
        return ledger;
    }
    public void Save(PracticeLedger ledger)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(ledger, JsonOptions));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>
    /// 按「语言目录 / 句式文件」两级结构加载题库：扫根目录下所有 JSON，
    /// 一级目录名就是语言，文件名（或文件里的 name）就是句式。分类只认目录结构，
    /// JSON 里重复写的 category 一律不算，避免两边各说一套。
    /// </summary>
    public static PracticeCatalog LoadCatalogTree(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到题库目录 {root}。");
        var catalog = new PracticeCatalog();
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file);
            // 直接躺在根下的文件没有语言目录，归进「通用」，不因为缺分类就整个题库加载不了
            var language = relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar)
                ? relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] : "通用";
            var parsed = JsonSerializer.Deserialize<PracticeCatalogFile>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException($"题库文件 {relative} 是空的。");
            if (parsed.Version != 1) throw new InvalidDataException($"题库文件 {relative} 的版本不支持。");
            if (parsed.Sentences.Count == 0) throw new InvalidDataException($"题库文件 {relative} 没有题目。");
            var name = string.IsNullOrWhiteSpace(parsed.Name) ? Path.GetFileNameWithoutExtension(file) : parsed.Name.Trim();
            foreach (var sentence in parsed.Sentences)
            {
                sentence.Language = language;
                sentence.Category = name;
                sentence.Note = parsed.Note?.Trim() ?? "";
                sentence.Mask = parsed.Mask;
                catalog.Sentences.Add(sentence);
            }
        }
        catalog.Validate();
        return catalog;
    }
}
