using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services.Modules;

namespace XAssistant.ViewModels;

/// <summary>
/// 监视模块的卡片集合。整块面板是「模板化生成」的：宿主不认识任何具体模块，
/// 只把每个模块自己注册的键值对与元数据翻成行，控件形态由值类型 + 元数据决定。
/// </summary>
public sealed class WatchModulesViewModel : ViewModelBase, IDisposable
{
    private readonly WatchModuleRegistry _registry;

    public ObservableCollection<ModuleCardViewModel> Cards { get; } = new();

    public WatchModulesViewModel(WatchModuleRegistry registry)
    {
        _registry = registry;
        _registry.Changed += Rebuild;
        Rebuild();
    }

    public void Dispose() => _registry.Changed -= Rebuild;

    /// <summary>
    /// 注册表每次报 Changed 都整段重建：卡片数是个位数，重建成本可以忽略，
    /// 换来的是「面板显示的永远是注册表那一刻的样子」，不必逐属性同步。
    /// </summary>
    private void Rebuild()
    {
        var live = Cards.ToDictionary(c => c.Id, c => c.EditingKey);
        Cards.Clear();
        foreach (var info in _registry.Modules)
        {
            var card = new ModuleCardViewModel(_registry, info);
            if (live.TryGetValue(info.Id, out string? editing)) card.EditingKey = editing;
            Cards.Add(card);
        }
    }
}

/// <summary>一张模块卡：标题、总开关、参数行、错误行，外加申请/回传的空间。</summary>
public sealed partial class ModuleCardViewModel : INotifyPropertyChanged
{
    private readonly WatchModuleRegistry _registry;
    private readonly WatchModuleInfo _info;

    public ModuleCardViewModel(WatchModuleRegistry registry, WatchModuleInfo info)
    {
        _registry = registry;
        _info = info;
        var metas = info.Metas.ToDictionary(m => m.Key);
        Rows = new ObservableCollection<ModuleRowViewModel>(
            info.Fields.Select(field => new ModuleRowViewModel(field, metas.TryGetValue(field.Key, out var meta) ? meta : null)));
        // 行在模板里，控件的 DataContext 只能拿到行本身；把卡的引用留给行，提交才有地方送
        foreach (var row in Rows) row.Owner = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _info.Id;
    public string Title => _info.Title;
    public bool Enabled => _info.Enabled;
    public string? Error => _info.LastError;
    public ObservableCollection<ModuleRowViewModel> Rows { get; }

    /// <summary>用户正在敲的那一行：重建卡片时保住焦点，免得改一个值整张卡重画把光标弄丢。</summary>
    public string? EditingKey { get; set; }

    [RelayCommand]
    private void Toggle()
    {
        _registry.SetEnabled(Id, !Enabled);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
    }

    /// <summary>勾选行：值直接进注册表（防抖窗口后才回灌模块）。</summary>
    public void Push(ModuleRowViewModel row, object? value)
    {
        if (row.Key == EditingKey) EditingKey = null;
        _registry.PushValue(Id, row.Key, value);
    }

    /// <summary>面板实测出这一格多大就回传（模块只当参考）。</summary>
    public void ReportSpace(double width, double height) => _registry.ReportSpace(Id, width, height);
}

/// <summary>
/// 一行参数。控件形态两条规则：值是 bool 就出勾选，否则出输入框；
/// 保密行走密码框（怎么遮由前端定），元数据说不可输入就整行灰掉。
/// </summary>
public sealed class ModuleRowViewModel : INotifyPropertyChanged
{
    private readonly ModuleField _field;
    private readonly ModuleMeta? _meta;
    private bool _check;
    private string _text = "";

    public ModuleRowViewModel(ModuleField field, ModuleMeta? meta)
    {
        _field = field;
        _meta = meta;
        IsCheck = field.Value is bool;
        _check = field.Value is bool b && b;
        _text = Convert.ToString(field.Value, CultureInfo.InvariantCulture) ?? "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>所属卡片：面板从行上拿不到卡，提交得靠它送回注册表。</summary>
    public ModuleCardViewModel? Owner { get; internal set; }

    /// <summary>
    /// 密码框不支持绑定，面板 Loaded 时把控件交给行存住，卡片重建后能把值回填回去——
    /// 否则用户刚粘完 cookie、改一下别的参数，密码框就看起来空了。
    /// </summary>
    public PasswordBox? Password
    {
        get => _box;
        set
        {
            _box = value;
            if (_box is not null) _box.Password = _text;
        }
    }

    private PasswordBox? _box;

    public string Key => _field.Key;
    public bool IsCheck { get; }
    public bool IsValue => !IsCheck;
    public string Display => string.IsNullOrWhiteSpace(_meta?.DisplayName) ? _field.Label : _meta!.DisplayName!;
    public string? Hint => _meta?.Hint;
    public string? Unit => _meta?.Unit;
    public string? Placeholder => _field.Placeholder;
    public bool Editable => _meta?.Editable ?? true;
    public bool Secret => IsValue && (_meta?.Secret ?? false);
    public bool NumericOnly => _meta?.NumericOnly ?? false;
    public bool IsVisible => _meta?.Visible ?? true;
    public Visibility CheckVisibility => IsCheck && IsVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ValueVisibility => IsValue && IsVisible && !Secret ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SecretVisibility => IsValue && IsVisible && Secret ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnitVisibility => string.IsNullOrEmpty(Unit) ? Visibility.Collapsed : Visibility.Visible;

    public bool Check
    {
        get => _check;
        set { if (_check == value) return; _check = value; Raise(nameof(Check)); }
    }

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; Raise(nameof(Text)); }
    }

    /// <summary>密码框不能绑定，走代码回填；显示时只留长度提示，不把明文摊在界面上。</summary>
    public string SecretHint => _text.Length == 0 ? "（未设置）" : $"已设置 {_text.Length} 个字符";

    public void SetValueFromModule(object? value)
    {
        _field.Value = value;
        if (IsCheck) Check = value is bool b && b;
        else Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        Raise(nameof(SecretHint));
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
