using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services;

namespace XAssistant.ViewModels;

/// <summary>
/// 设置页视图模型：当前承载界面主题切换（浅色 / 深色）与键盘动画窗口入口。
/// 主题切换即时生效并持久化到 appsettings.json，下次启动自动应用。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly Services.Interfaces.IConfigurationService _configService;
    private readonly KeyAnimationViewModel _keyAnimationViewModel;

    /// <summary>当前是否处于深色主题（供 UI 显示选中状态）</summary>
    [ObservableProperty]
    private bool _isDarkTheme;

    public SettingsViewModel(
        Services.Interfaces.IConfigurationService configService,
        KeyAnimationViewModel keyAnimationViewModel
    )
    {
        _configService = configService;
        _keyAnimationViewModel = keyAnimationViewModel;
        IsDarkTheme = _configService.GetTheme() == ThemeManager.Dark;
    }

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
        if (ThemeManager.Current == theme)
            return;

        // 即时应用 + 持久化
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