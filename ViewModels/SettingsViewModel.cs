using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services;

namespace XAssistant.ViewModels;

/// <summary>
/// 设置页视图模型：当前承载界面主题切换（浅色 / 深色）、打字关键词彩蛋开关，与键盘动画窗口入口。
/// 主题切换即时生效并持久化到 appsettings.json，下次启动自动应用；关键词换肤不落盘（见 KeywordWatcher）。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly Services.Interfaces.IConfigurationService _configService;
    private readonly KeyAnimationViewModel _keyAnimationViewModel;
    private readonly Services.Keywords.KeywordWatcher _keywords;

    /// <summary>当前是否处于深色主题（供 UI 显示选中状态）</summary>
    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>打字关键词彩蛋开关（white / black / flower …）</summary>
    [ObservableProperty]
    private bool _keywordEffects;

    public SettingsViewModel(
        Services.Interfaces.IConfigurationService configService,
        KeyAnimationViewModel keyAnimationViewModel,
        Services.Keywords.KeywordWatcher keywords
    )
    {
        _configService = configService;
        _keyAnimationViewModel = keyAnimationViewModel;
        _keywords = keywords;
        IsDarkTheme = _configService.GetTheme() == ThemeManager.Dark;
        // 读初始值走字段不走属性：属性 setter 会回写配置，启动时就多落一次盘
        _keywordEffects = _configService.GetKeywordEffects();
        _keywords.Enabled = _keywordEffects;
    }

    partial void OnKeywordEffectsChanged(bool value)
    {
        _configService.SetKeywordEffects(value);
        // 开关要当场生效，不能等下次启动：刚关掉还在掉粒子是Bug
        _keywords.Enabled = value;
    }

    /// <summary>试一次：只掉粒子不动主题，让人不必去猜哪个词会触发。</summary>
    [RelayCommand]
    private void PreviewKeywords() => _keywords.Preview();

    [RelayCommand]
    private void ApplyLight()
    {
        ApplyTheme(ThemeManager.Light);
    }

    [RelayCommand]
    private void ApplyDark()
    {
        ApplyTheme(ThemeManager.Dark);
    }

    private void ApplyTheme(string theme)
    {
        // 判据不能只看 Current：关键词色板可能正盖在同一套主题上，
        // 这时候点按钮是要把色板抹回主题原色的
        if (ThemeManager.Current == theme && ThemeManager.PaletteName == null)
            return;

        // 即时应用 + 持久化（Apply 会一并清掉关键词留下的色板）
        ThemeManager.Apply(theme);
        _configService.SetTheme(theme);
        IsDarkTheme = theme == ThemeManager.Dark;
    }

    /// <summary>打开独立的键盘动画透明窗口（透视模式）</summary>
    [RelayCommand]
    private void OpenKeyAnimationWindow()
    {
        Views.KeyAnimationWindow.EnsureOpen(_keyAnimationViewModel);
    }
}