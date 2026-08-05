using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

// 速记捕获窗的视图模型：正文输入、来源展示、Ctrl+Enter 保存（失败保留文字可重试）/ Esc 取消
public partial class QuickNoteViewModel : ObservableObject
{
    private readonly IQuickNoteDatabaseService _dbService;
    private readonly ILogger<QuickNoteViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _content = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string _source = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    [ObservableProperty]
    private string? _errorMessage;

    // 保存成功事件：窗口收到后关闭
    public event Action? SaveSucceeded;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasSource => !string.IsNullOrWhiteSpace(Source);

    public bool CanSave => !IsSaving && !string.IsNullOrWhiteSpace(Content);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public QuickNoteViewModel(
        IQuickNoteDatabaseService dbService,
        ILogger<QuickNoteViewModel> logger
    )
    {
        _dbService = dbService;
        _logger = logger;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        IsSaving = true;
        ErrorMessage = null;
        try
        {
            // 来源为空串时存 null（表列可空）
            string? source = string.IsNullOrWhiteSpace(Source) ? null : Source;
            bool ok = await _dbService.SaveAsync(Content.Trim(), source);
            if (ok)
            {
                SaveSucceeded?.Invoke();
            }
            else
            {
                ErrorMessage = "保存失败，内容已保留：请检查速记数据库连接后按 Ctrl+Enter 重试";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "速记保存时发生未预期异常");
            ErrorMessage = "保存失败，内容已保留：请按 Ctrl+Enter 重试";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
