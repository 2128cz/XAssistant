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
    // 输入按“光标 + 落子”建模，而不是往字符串末尾追加。
    // 追加式缓冲只要中间少了一个字符，后面每一位都会错位，进度永远停在 N-1/N，
    // 再多敲也补不回那一格——句子就此卡死。改成光标推进后，落不下的键要么跳过缺的那格，
    // 要么被拒收，两条路都能继续往前走。
    private readonly List<TypedCell> _cells = new();

    /// <summary>一次落子占用的正文格。<see cref="Typed"/> 为 \0 表示这一格是提交时由程序补上的。</summary>
    private readonly record struct TypedCell(char Typed, int TextIndex);

    /// <summary>光标：下一个字符该落在正文哪一格。最后一子落在哪，光标就在哪后面。</summary>
    private int Caret => _cells.Count == 0 ? 0 : _cells[^1].TextIndex + 1;

    /// <summary>允许向前跳多远。一格空格加一个字母已经够接住“漏敲一键”；再远就是整段抄袭。</summary>
    private const int MaxSkipAhead = 2;

    /// <summary>剩余不足这个数时，回车直接把它跳过收尾；再多就得先给用户看清楚差在哪。</summary>
    private const int AutoFinishRemaining = 4;

    /// <summary>没敲错时的默认提示，也是换句后的初始状态。</summary>
    private const string IdleHint = "忽略大小写，标点照写 · 回车检查";

    private bool _lastKeyRejected, _submitPrompted;

    public void HandleKeyboardInput(string input)
    {
        if (!HasContent || CurrentSentence == null || string.IsNullOrEmpty(input)) return;
        if (input == "\r") { SubmitVerse(); return; }
        if (input == "\b") { Backspace(); return; }
        // 已经过关的这一句不再改动，否则多余按键会把光标推回去、让同一句重复计分
        if (VerseComplete) return;

        string text = PracticeText.Normalize(CurrentSentence.Text);
        bool changed = false;
        foreach (char raw in input)
        {
            if (!char.IsControl(raw)) changed |= Place(PracticeText.FoldChar(raw), text);
        }
        if (changed)
        {
            if (_cells.Count > 0 && !_verseClock.IsRunning && !_verseAwarded) _verseClock.Start();
            UpdateVerse(markErrors: false);
            // 光标推到正文末尾就是整句落齐，不必再等回车——旧版靠整串相等判定，这里改成靠位置
            if (!_verseAwarded && Caret >= text.Length) CompleteVerse();
        }
    }

    /// <summary>把一把键放到正文上：命中光标就推进，命中后面就跳过缺的那几格，都对不上就拒收。</summary>
    private bool Place(char c, string text)
    {
        int caret = Caret;
        if (caret >= text.Length) return false;
        int hit = -1;
        for (int i = caret; i < text.Length && i - caret <= MaxSkipAhead; i++)
            if (PracticeText.EqualChar(c, text[i])) { hit = i; break; }

        if (hit < 0)
        {
            // 对不上的键不写进正文：只计一次错并把当前词标红，让用户看见自己敲错了什么
            _verseMistakes++;
            _lastKeyRejected = true;
            _submitPrompted = false;
            return true;
        }
        // 跳过的那几格照旧计错，分数照扣，不会因为“程序帮我补了”而白过
        _verseMistakes += hit - caret;
        _cells.Add(new TypedCell(c, hit));
        _lastKeyRejected = false;
        _submitPrompted = false;
        return true;
    }

    /// <summary>退格回到上一子落下的位置，连带它当时跳过的那几格一起退回。</summary>
    private void Backspace()
    {
        if (_cells.Count == 0) return;
        _cells.RemoveAt(_cells.Count - 1);
        _lastKeyRejected = false;
        _submitPrompted = false;
        UpdateVerse(markErrors: false);
    }

    [RelayCommand]
    private void ClearInput()
    {
        // Keep this sentence's awarded state: clearing must not allow duplicate score farming.
        _cells.Clear();
        _lastKeyRejected = _submitPrompted = false;
        VerseComplete = false;
        VerseProgress = 0;
        VerseInput = "";
        foreach (var word in Words) { word.HasError = false; foreach (var c in word.Characters) { c.IsCorrect = false; c.IsWrong = false; } }
    }
    private readonly PracticeCatalog _catalog;
    private readonly IPracticeScoreStore _store;
    private readonly PracticeLedger _ledger;
    private readonly Queue<PracticeSentence> _bag = new();
    private readonly Stopwatch _verseClock = new();
    private bool _verseAwarded, _canSave = true;
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
    private void UpdateVerse(bool markErrors)
    {
        var text = PracticeText.Normalize(CurrentSentence?.Text ?? "");
        var caret = Caret;
        // 只有真敲下去的键才算“抄对”；程序补上的（\0）和被跳过去的格子一律标红，分数照样扣
        var typedCells = new HashSet<int>(_cells.Where(cell => cell.Typed != '\0').Select(cell => cell.TextIndex));
        // 上一次按键对不上时，把光标后面第一个真实字符标红：光标可能正停在词间空格上，
        // 那一格不渲染成字符，不往后找就什么都看不见
        var shown = caret;
        if (_lastKeyRejected)
        {
            while (shown < text.Length && text[shown] == ' ') shown++;
            shown = Math.Min(shown + 1, text.Length);
        }
        var position = 0;
        foreach (var word in Words)
        {
            bool wrong = false;
            foreach (var character in word.Characters)
            {
                character.IsCorrect = typedCells.Contains(position);
                character.IsWrong = !character.IsCorrect && (position < shown || markErrors);
                wrong |= character.IsWrong;
                position++;
            }
            word.HasError = wrong;
            // 词间空格被跨过去时不会染红任何字符（它本来就不渲染），给前一个词补上下划线告警
            word.HasError |= position < caret && !typedCells.Contains(position);
            position++; // One canonical space between displayed words.
        }
        VerseInput = new string(_cells.Where(cell => cell.Typed != '\0').Select(cell => cell.Typed).ToArray());
        VerseProgress = text.Length > 0 ? caret * 100d / text.Length : 0;
        OnPropertyChanged(nameof(ProgressText));
        if (_verseAwarded) return; // 完成的提示由 CompleteVerse 负责，别在这儿覆盖
        VerseStatus = markErrors ? "红色是还没敲进去的字符。再按一次回车会跳过它们，每个按一次错误计。"
            : _lastKeyRejected ? "这个键和当前字符对不上，已按一次错误计。继续敲后面能对上的字符就会跨过它。"
            : IdleHint;
    }
    [RelayCommand]
    private void SubmitVerse()
    {
        if (CurrentSentence == null) return;
        if (VerseComplete) { NextSentence(); return; }
        var text = PracticeText.Normalize(CurrentSentence.Text);
        int remaining = text.Length - Caret;
        // 整句已经落齐却已经计过分（「清空」后重抄），回车只负责换下一句，不再重复给分
        if (remaining <= 0) { if (_verseAwarded) NextSentence(); else CompleteVerse(); return; }
        // 逃生阀：总有字符是敲不进去的（钩子丢键、键盘布局打不出），差得不多就替用户补上收尾，
        // 差得多则先把红格子摊开看清楚，第二次回车再跳过。补上的每格都计一次错，没有白过的路。
        if (remaining <= AutoFinishRemaining || _submitPrompted)
        {
            for (int i = Caret; i < text.Length; i++) { _verseMistakes++; _cells.Add(new TypedCell('\0', i)); }
            _lastKeyRejected = false;
            UpdateVerse(markErrors: false);
            CompleteVerse();
            return;
        }
        _lastKeyRejected = false;
        _submitPrompted = true;
        UpdateVerse(markErrors: true);
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
        CurrentSentence = _bag.Dequeue();
        _cells.Clear();
        _lastKeyRejected = _submitPrompted = false;
        VerseInput = "";
        VerseComplete = _verseAwarded = false;
        VerseProgress = 0;
        _verseMistakes = 0;
        _verseClock.Reset();
        Words.Clear();
        foreach (var word in CurrentSentence.Words) Words.Add(new PracticeWordState(word));
        VerseStatus = IdleHint;
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
