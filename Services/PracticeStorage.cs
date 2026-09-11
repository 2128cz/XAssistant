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
    public static PracticeCatalog LoadCatalog(string path)
    {
        var catalog = JsonSerializer.Deserialize<PracticeCatalog>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("题库为空。");
        catalog.Validate();
        return catalog;
    }
}
