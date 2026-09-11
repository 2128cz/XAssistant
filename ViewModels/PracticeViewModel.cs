using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services;

namespace XAssistant.ViewModels;

/// <summary>Random verse practice driven by the existing keyboard hook.</summary>
public partial class PracticeViewModel : ObservableObject, IDisposable
{
    private XAssistant.Services.Interfaces.IKeyboardHookService? _hook;
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    public void ConnectKeyboard(XAssistant.Services.Interfaces.IKeyboardHookService hook)
    {
        if (_hook != null) _hook.TextInput -= OnHookInput;
        _hook = hook;
        _hook.TextInput += OnHookInput;
    }
    private void OnHookInput(string input)
    {
        if (_dispatcher.CheckAccess()) { if (_hook != null) HandleKeyboardInput(input); }
        else _dispatcher.BeginInvoke(new Action(() => { if (_hook != null) HandleKeyboardInput(input); }));
    }
    public void Dispose() { if (_hook != null) _hook.TextInput -= OnHookInput; _hook = null; }
    public void HandleKeyboardInput(string input)
    {
        if (!HasContent || string.IsNullOrEmpty(input)) return;
        if (input == "\r") { SubmitVerse(); return; }

        if (input == "\b") { if (VerseInput.Length > 0) VerseInput = VerseInput[..^1]; return; }
        foreach (char c in input.Where(c => !char.IsControl(c)))
            if (!VerseComplete && VerseInput.Length < (CurrentSentence?.Text.Length ?? 0)) VerseInput += c;
    }
    [RelayCommand]
    private void ClearInput()
    {
        // Keep this sentence's awarded state: clearing must not allow duplicate score farming.
        VerseInput = "";
        VerseComplete = false;
        VerseProgress = 0;
        foreach (var word in Words) { word.HasError = false; foreach (var c in word.Characters) { c.IsCorrect = false; c.IsWrong = false; } }
    }
    private readonly PracticeCatalog _catalog;
    private readonly IPracticeScoreStore _store;
    private readonly PracticeLedger _ledger;
    private readonly Queue<PracticeSentence> _bag = new();
    private readonly Stopwatch _verseClock = new();
    private bool _loading, _verseAwarded, _canSave = true;
    private int _verseMistakes;
    public ObservableCollection<PracticeWordState> Words { get; } = [];
    public ObservableCollection<PracticeResult> RecentResults { get; } = [];
    public string[] Categories { get; }
    [ObservableProperty] private string _selectedCategory = "全部";
    [ObservableProperty] private PracticeSentence? _currentSentence;
    [ObservableProperty] private string _verseInput = "";
    [ObservableProperty] private bool _autoAdvance;
    [ObservableProperty] private bool _verseComplete;
    [ObservableProperty] private double _verseProgress;
    [ObservableProperty] private string _verseStatus = "";
    [ObservableProperty] private string _persistenceStatus = "成绩保存在本机";
    public bool HasContent => _catalog.Sentences.Count > 0;
    public string ProgressText => $"{(int)Math.Round(VerseProgress * (CurrentSentence?.Text.Length ?? 0) / 100d)} / {CurrentSentence?.Text.Length ?? 0}";
    public long TotalScore => _ledger.TotalScore;
    public int CompletedRounds => _ledger.CompletedRounds;
    public string SourceText => CurrentSentence == null ? "题库不可用" : $"{CurrentSentence.Category} · {CurrentSentence.Source}";
    public string Translation => CurrentSentence?.Translation ?? "请检查 Assets/Practice/sentences.json";

    public static PracticeViewModel CreateDefault()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Practice", "sentences.json");
        // Resolve the location without creating the directory until a score is saved.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
#if DEBUG
            "XAssistant_Dev"
#else
            "XAssistant"
#endif
        );
        var store = new PracticeScoreStore(Path.Combine(folder, "practice-scores.json"));
        try { return new PracticeViewModel(PracticeScoreStore.LoadCatalog(file), store); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { return new PracticeViewModel(new PracticeCatalog(), store) { VerseStatus = "题库加载失败：" + error.Message }; }
    }
    public PracticeViewModel(PracticeCatalog catalog, IPracticeScoreStore store)
    {
        _catalog = catalog;
        _store = store;
        if (catalog.Sentences.Count > 0) catalog.Validate();
        Categories = new[] { "全部" }.Concat(catalog.Sentences.Select(s => s.Category).Distinct()).ToArray();
        try { _ledger = store.Load(); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _ledger = new(); _canSave = false; PersistenceStatus = "历史成绩读取失败，本次仅保留在内存：" + error.Message; }
        foreach (var result in _ledger.RecentResults.Take(20)) RecentResults.Add(result);
        NextSentence();
    }
    partial void OnSelectedCategoryChanged(string value) { _bag.Clear(); NextSentence(); }
    partial void OnVerseInputChanged(string value)
    {
        if (_loading || CurrentSentence == null) return;
        if (value.Length > CurrentSentence.Text.Length) { VerseInput = value[..CurrentSentence.Text.Length]; return; }
        VerseComplete = PracticeText.Equal(value, CurrentSentence.Text);
        if (value.Length > 0 && !_verseClock.IsRunning && !_verseAwarded) _verseClock.Start();
        UpdateVerse(markErrors: false);
        if (PracticeText.Equal(value.TrimEnd(), CurrentSentence.Text)) CompleteVerse();
    }
    private void UpdateVerse(bool markErrors)
    {
        var typed = PracticeText.Normalize(VerseInput);
        var position = 0;
        foreach (var word in Words)
        {
            bool wrong = false;
            foreach (var character in word.Characters)
            {
                character.IsCorrect = position < typed.Length && PracticeText.Equal(typed[position].ToString(), character.Text);
                character.IsWrong = !character.IsCorrect && (position < typed.Length || markErrors);
                wrong |= character.IsWrong;
                position++;
            }
            word.HasError = markErrors && wrong;
            position++; // One canonical space between displayed words.
        }
        VerseProgress = CurrentSentence?.Text.Length > 0
            ? Enumerable.Range(0, CurrentSentence.Text.Length).Count(i => i < typed.Length && PracticeText.Equal(typed[i].ToString(), CurrentSentence.Text[i].ToString())) * 100d / CurrentSentence.Text.Length : 0;
        OnPropertyChanged(nameof(ProgressText));
        if (!_verseAwarded) VerseStatus = markErrors ? "红色单词尚未抄对，请修正后按回车。" : "忽略大小写，标点照写 · 回车检查";
    }
    [RelayCommand]
    private void SubmitVerse()
    {
        if (CurrentSentence == null) return;
        if (VerseComplete) { NextSentence(); return; }
        if (PracticeText.Equal(VerseInput.TrimEnd(), CurrentSentence.Text)) { CompleteVerse(); return; }
        _verseMistakes++;
        UpdateVerse(markErrors: true);
        if (VerseInput.TrimEnd().Length > CurrentSentence.Text.Length)
            VerseStatus = "句末有多余字符，请删除后按回车。";
    }
    private void CompleteVerse()
    {
        if (_verseAwarded || CurrentSentence == null) return;
        _verseAwarded = true;
        VerseComplete = true;
        _verseClock.Stop();
        int score = Math.Max(10, CurrentSentence.Text.Length * 2 + 100 - _verseMistakes * 10);
        Award("诗句抄写", score, _verseMistakes, _verseClock.Elapsed.TotalSeconds);
        VerseStatus = $"完成！+{score} 分 · 按回车进入下一句";
        if (AutoAdvance) NextSentence();
    }
    [RelayCommand]
    private void NextSentence()
    {
        if (!HasContent) return;
        if (_bag.Count == 0)
        {
            var eligible = _catalog.Sentences.Where(s => SelectedCategory == "全部" || s.Category == SelectedCategory).OrderBy(_ => Random.Shared.Next()).ToList();
            if (eligible.Count > 1 && eligible[0].Id == CurrentSentence?.Id) (eligible[0], eligible[1]) = (eligible[1], eligible[0]);
            foreach (var sentence in eligible) _bag.Enqueue(sentence);
        }
        if (_bag.Count == 0) return;
        _loading = true;
        CurrentSentence = _bag.Dequeue();
        VerseInput = "";
        VerseComplete = _verseAwarded = false;
        VerseProgress = 0;
        _verseMistakes = 0;
        _verseClock.Reset();
        Words.Clear();
        foreach (var word in CurrentSentence.Words) Words.Add(new PracticeWordState(word));
        VerseStatus = "";
        _loading = false;
        foreach (var name in new[] { nameof(SourceText), nameof(Translation), nameof(ProgressText) }) OnPropertyChanged(name);
    }
    private void Award(string mode, int score, int mistakes, double seconds)
    {
        var result = new PracticeResult { SentenceId = CurrentSentence!.Id, Mode = mode, Score = score,
            Mistakes = mistakes, Seconds = Math.Round(seconds, 1), Accuracy = Math.Round(100d * (Words.Count) / (Math.Max(1, Words.Count) + mistakes), 1) };
        _ledger.TotalScore += score;
        _ledger.CompletedRounds++;
        _ledger.RecentResults.Insert(0, result);
        if (_ledger.RecentResults.Count > 200) _ledger.RecentResults.RemoveRange(200, _ledger.RecentResults.Count - 200);
        RecentResults.Insert(0, result);
        if (RecentResults.Count > 20) RecentResults.RemoveAt(20);
        OnPropertyChanged(nameof(TotalScore)); OnPropertyChanged(nameof(CompletedRounds));
        SaveScores();
    }
    [RelayCommand]
    private void SaveScores()
    {
        if (!_canSave) return;
        try { _store.Save(_ledger); PersistenceStatus = "成绩已保存到本机"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { PersistenceStatus = "保存失败，可重试；当前成绩仍在内存中：" + error.Message; }
    }
}

public partial class PracticeCharacter(string text) : ObservableObject
{
    public string Text { get; } = text;
    [ObservableProperty] private bool _isCorrect;
    [ObservableProperty] private bool _isWrong;
}
public partial class PracticeWordState : ObservableObject
{
    public string Ipa { get; }
    public string Translation { get; }
    public ObservableCollection<PracticeCharacter> Characters { get; }
    [ObservableProperty] private bool _hasError;
    public PracticeWordState(PracticeWord word)
    {
        Ipa = "/" + word.Ipa.Trim('/') + "/";
        Translation = word.Text + " = " + word.Translation;
        Characters = new((word.Text + word.Suffix).Select(c => new PracticeCharacter(c.ToString())));
    }
}
