using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace XAssistant.Controls;

/// <summary>
/// The design's complete 144-key arrangement. Each visual position has a stable
/// physical identity; main digits and numpad digits never share a counter.
/// </summary>
public partial class KeyboardHeatmap : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(KeyboardHeatmap), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty RecentKeyProperty = Register(nameof(RecentKey), string.Empty);
    public static readonly DependencyProperty ShowHeaderProperty = Register(nameof(ShowHeader), true);
    public static readonly DependencyProperty IsRecordingProperty = Register(nameof(IsRecording), false);
    public static readonly DependencyProperty PeriodLabelProperty = Register(nameof(PeriodLabel), "今日 / 扩展布局 144 键");
    public static readonly DependencyProperty MousePeriodLabelProperty = Register(nameof(MousePeriodLabel), "今日");
    public static readonly DependencyProperty LeftClickCountProperty = Register(nameof(LeftClickCount), 0);
    public static readonly DependencyProperty MiddleClickCountProperty = Register(nameof(MiddleClickCount), 0);
    public static readonly DependencyProperty RightClickCountProperty = Register(nameof(RightClickCount), 0);
    public static readonly DependencyProperty MouseMovementTextProperty = RegisterTick<string>(nameof(MouseMovementText), "移动数据未采集");
    public static readonly DependencyProperty MouseWheelTextProperty = RegisterTick<string>(nameof(MouseWheelText), "滚轮数据未采集");

    /// <summary>小鼠标图标在位移窗内的归一化偏移（-1..1），像素换算由本控件按实际尺寸完成。</summary>
    public static readonly DependencyProperty MouseOffsetXProperty = RegisterTick(nameof(MouseOffsetX), 0d, MouseOffsetChanged);
    public static readonly DependencyProperty MouseOffsetYProperty = RegisterTick(nameof(MouseOffsetY), 0d, MouseOffsetChanged);

    public static readonly DependencyProperty MouseDeltaTextProperty = RegisterTick<string>(nameof(MouseDeltaText), "Δx 0 · Δy 0 px");
    public static readonly DependencyProperty MouseDistanceTextProperty = RegisterTick<string>(nameof(MouseDistanceText), "移动 今日 0.00 m · 累计 0.00 m");

    /// <summary>最近一次按下的键名（Left/Middle/Right），与自增的 <see cref="MouseClickPulse"/> 配合点亮对应区域。</summary>
    public static readonly DependencyProperty MouseButtonProperty = RegisterTick<string>(nameof(MouseButton), string.Empty);
    public static readonly DependencyProperty MouseClickPulseProperty = RegisterTick(nameof(MouseClickPulse), 0L, MouseClickChanged);

    /// <summary>每敲一键自增一次的脉冲。连击同一个键时 <see cref="RecentKey"/> 根本不变，靠它才能让倾斜动画每次都重播。</summary>
    public static readonly DependencyProperty KeyStrikePulseProperty = RegisterTick(nameof(KeyStrikePulse), 0L, KeyStrikeChanged);

    /// <summary>垫在鼠标读数下方的 X / Y 偏移与滚轮速率三条轨迹，坐标已是控件内那块 Canvas 的设计尺寸，本控件不再换算。</summary>
    public static readonly DependencyProperty MouseTrailXPointsProperty =
        RegisterTick(nameof(MouseTrailXPoints), new PointCollection());
    public static readonly DependencyProperty MouseTrailYPointsProperty =
        RegisterTick(nameof(MouseTrailYPoints), new PointCollection());
    public static readonly DependencyProperty MouseTrailWheelPointsProperty =
        RegisterTick(nameof(MouseTrailWheelPoints), new PointCollection());

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string RecentKey { get => (string)GetValue(RecentKeyProperty); set => SetValue(RecentKeyProperty, value); }
    public bool ShowHeader { get => (bool)GetValue(ShowHeaderProperty); set => SetValue(ShowHeaderProperty, value); }
    public bool IsRecording { get => (bool)GetValue(IsRecordingProperty); set => SetValue(IsRecordingProperty, value); }
    public string PeriodLabel { get => (string)GetValue(PeriodLabelProperty); set => SetValue(PeriodLabelProperty, value); }
    public string MousePeriodLabel { get => (string)GetValue(MousePeriodLabelProperty); set => SetValue(MousePeriodLabelProperty, value); }
    public int LeftClickCount { get => (int)GetValue(LeftClickCountProperty); set => SetValue(LeftClickCountProperty, value); }
    public int MiddleClickCount { get => (int)GetValue(MiddleClickCountProperty); set => SetValue(MiddleClickCountProperty, value); }
    public int RightClickCount { get => (int)GetValue(RightClickCountProperty); set => SetValue(RightClickCountProperty, value); }
    public string MouseMovementText { get => (string)GetValue(MouseMovementTextProperty); set => SetValue(MouseMovementTextProperty, value); }
    public string MouseWheelText { get => (string)GetValue(MouseWheelTextProperty); set => SetValue(MouseWheelTextProperty, value); }
    public double MouseOffsetX { get => (double)GetValue(MouseOffsetXProperty); set => SetValue(MouseOffsetXProperty, value); }
    public double MouseOffsetY { get => (double)GetValue(MouseOffsetYProperty); set => SetValue(MouseOffsetYProperty, value); }
    public string MouseDeltaText { get => (string)GetValue(MouseDeltaTextProperty); set => SetValue(MouseDeltaTextProperty, value); }
    public string MouseDistanceText { get => (string)GetValue(MouseDistanceTextProperty); set => SetValue(MouseDistanceTextProperty, value); }
    public string MouseButton { get => (string)GetValue(MouseButtonProperty); set => SetValue(MouseButtonProperty, value); }
    public long MouseClickPulse { get => (long)GetValue(MouseClickPulseProperty); set => SetValue(MouseClickPulseProperty, value); }
    public long KeyStrikePulse { get => (long)GetValue(KeyStrikePulseProperty); set => SetValue(KeyStrikePulseProperty, value); }
    public PointCollection MouseTrailXPoints { get => (PointCollection)GetValue(MouseTrailXPointsProperty); set => SetValue(MouseTrailXPointsProperty, value); }
    public PointCollection MouseTrailYPoints { get => (PointCollection)GetValue(MouseTrailYPointsProperty); set => SetValue(MouseTrailYPointsProperty, value); }
    public PointCollection MouseTrailWheelPoints { get => (PointCollection)GetValue(MouseTrailWheelPointsProperty); set => SetValue(MouseTrailWheelPointsProperty, value); }

    public ObservableCollection<KeyCap> KeyCaps { get; } = new();
    private INotifyCollectionChanged? _collection;
    private readonly HashSet<INotifyPropertyChanged> _observedItems = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<Type, (PropertyInfo? Key, PropertyInfo? Count)> ItemProperties = new();
    private bool _observing;
    private bool _refreshPending;
    private static readonly Dictionary<string, string> Aliases;

    /// <summary>键位标识到其布局定义的索引，倾斜动画要按敲到的键算出它在板面上的位置。</summary>
    private static readonly Dictionary<string, KeyDefinition> KeyById = new(StringComparer.Ordinal);

    /// <summary>热力色阶，从低到高单调变亮。按键着色与图例渐变条共用这一份，避免两处描述漂移。</summary>
    private static readonly byte[][] HeatRamp =
    {
        new byte[] { 0x20, 0x2D, 0x30 },
        new byte[] { 0x30, 0x4C, 0x43 },
        new byte[] { 0x4F, 0x77, 0x50 },
        new byte[] { 0x8B, 0xB8, 0x55 },
        new byte[] { 0xC4, 0xFF, 0x75 },
    };

    /// <summary>有记录的按键最低档强度：让“按得最少”与“从未按过”保持可见色差，而不是掉进同一个底色。</summary>
    private const double PressedFloor = 0.18;

    static KeyboardHeatmap()
    {
        Aliases = BuildAliases();
        foreach (var key in Layout) KeyById[key.Id] = key;
    }

    public KeyboardHeatmap()
    {
        _tilt = new TiltDriver(ApplyTilt);
        foreach (var key in Layout)
            KeyCaps.Add(new KeyCap(key));
        InitializeComponent();
        LegendBar.Background = BuildRampBrush();
        Loaded += (_, _) => { _observing = true; ObserveSource(); Refresh(); LayoutMouseGlyph(); _tilt.Reset(); };
        Unloaded += (_, _) => { _observing = false; DetachSource(); _tilt.Reset(); };
        Refresh();
    }

    /// <summary>键帽名的设计字号，与 KeyboardHeatmap.xaml 里那个 TextBlock 的原值同值。</summary>
    private const double DesignLabelFont = 10;
    private const double DesignCountFont = 8;

    /// <summary>
    /// 键盘块被 Viewbox 等比缩小后，固定设计字号会跟着一起变小：1120 的设计稿缩到一半时
    /// 10 号字只剩 5 px 左右，读不出是什么键。所以按实际缩放反算设计字号，
    /// 保证落在屏幕上的字不低于这个下限。
    /// </summary>
    private const double MinLabelPx = 8.5, MinCountPx = 6.5;

    /// <summary>放大上限：字号相对键帽长太大会把键帽糊满，宁可少读两个像素也不挤。</summary>
    private const double MaxLabelFont = 16, MaxCountFont = 12;

    /// <summary>键盘块在设计单位下的尺寸，与 XAML 里 ItemsControl 的 Width/Height 同值；改一处要同步另一处。</summary>
    private const double BoardDesignWidth = 1120, BoardDesignHeight = 331;

    /// <summary>
    /// 排版变化时重算键帽字号：窄布局（右侧摆着排行与最近按键）下键盘会被缩得很小，
    /// 靠这两个字号把键帽名拉回可读，计数拉不回来就直接收掉（见 <see cref="CountVisibility"/>）。
    /// </summary>
    public static readonly DependencyProperty LabelFontSizeProperty = RegisterLayout(nameof(LabelFontSize), DesignLabelFont);
    public static readonly DependencyProperty CountFontSizeProperty = RegisterLayout(nameof(CountFontSize), DesignCountFont);
    public static readonly DependencyProperty CountVisibilityProperty = RegisterLayout(nameof(CountVisibility), Visibility.Visible);

    public double LabelFontSize { get => (double)GetValue(LabelFontSizeProperty); set => SetValue(LabelFontSizeProperty, value); }
    public double CountFontSize { get => (double)GetValue(CountFontSizeProperty); set => SetValue(CountFontSizeProperty, value); }
    public Visibility CountVisibility { get => (Visibility)GetValue(CountVisibilityProperty); set => SetValue(CountVisibilityProperty, value); }

    private static DependencyProperty Register<T>(string name, T value) => DependencyProperty.Register(
        name, typeof(T), typeof(KeyboardHeatmap), new PropertyMetadata(value, VisualPropertyChanged));

    /// <summary>由排版算出来的视觉参数：变了只需重排文字，不该连带重建 144 个键帽的着色。</summary>
    private static DependencyProperty RegisterLayout<T>(string name, T value) => DependencyProperty.Register(
        name, typeof(T), typeof(KeyboardHeatmap), new PropertyMetadata(value));

    /// <summary>
    /// 高频节拍属性的注册。鼠标侧（取样节拍定义在 ClickCounterViewModel）与键盘侧（每敲一键一次）共用这一条：
    /// 这类回调绝不能再走 <see cref="VisualPropertyChanged"/>，那条路径会连带重建 144 个键帽的着色，
    /// 把热力图的开销放大到跟随刷新频率。
    /// </summary>
    private static DependencyProperty RegisterTick<T>(string name, T value, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(KeyboardHeatmap), new PropertyMetadata(value, changed));

    private static void MouseOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyboardHeatmap)d).LayoutMouseGlyph();

    private static void MouseClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyboardHeatmap)d).FlashMouseButton(((KeyboardHeatmap)d).MouseButton);

    private static void KeyStrikeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyboardHeatmap)d).StrikeTilt();

    private static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (KeyboardHeatmap)d;
        if (view._observing) view.ObserveSource();
        view.RequestRefresh();
    }

    private static void VisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((KeyboardHeatmap)d).RequestRefresh();

    private void DetachSource()
    {
        if (_collection is not null) _collection.CollectionChanged -= CollectionChanged;
        _collection = null;
        foreach (var item in _observedItems) item.PropertyChanged -= ItemChanged;
        _observedItems.Clear();
    }

    private void ObserveSource()
    {
        DetachSource();
        if (!_observing || ItemsSource is null) return;
        _collection = ItemsSource as INotifyCollectionChanged;
        if (_collection is not null) _collection.CollectionChanged += CollectionChanged;
        ObserveItems();
    }

    private void ObserveItems()
    {
        if (ItemsSource is null) return;
        var current = new HashSet<INotifyPropertyChanged>(ReferenceEqualityComparer.Instance);
        foreach (var item in ItemsSource)
            if (item is INotifyPropertyChanged notifying) current.Add(notifying);
        foreach (var item in _observedItems.Where(item => !current.Contains(item)).ToArray())
        {
            item.PropertyChanged -= ItemChanged;
            _observedItems.Remove(item);
        }
        foreach (var item in current)
            if (_observedItems.Add(item)) item.PropertyChanged += ItemChanged;
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => { if (_observing) ObserveItems(); RequestRefresh(); }));
            return;
        }
        ObserveItems();
        RequestRefresh();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is "Key" or "Count" or "Value") RequestRefresh();
    }

    private void RequestRefresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RequestRefresh)); return; }
        if (_refreshPending || !IsInitialized) return;
        _refreshPending = true;
        // 连打时每次按键至少两三个键帽的 Count 变、每帧都能排进一次 Refresh；
        // 色阶与计数不值得 60fps 刷新，150 ms 一道闸门把风暴压成每秒几次
        double due = RefreshMinIntervalMs - (DateTime.UtcNow - _lastRefreshAt).TotalMilliseconds;
        if (due <= 0)
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(RunRefresh));
        else
        {
            _refreshTimer ??= BuildRefreshTimer();
            _refreshTimer.Start();
        }
    }

    private void RunRefresh()
    {
        _refreshTimer?.Stop();
        _lastRefreshAt = DateTime.UtcNow;
        _refreshPending = false;
        Refresh();
    }

    private DispatcherTimer BuildRefreshTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshMinIntervalMs) };
        timer.Tick += (_, _) => RunRefresh();
        return timer;
    }

    /// <summary>热力图数据刷新的最小间隔（ms）：数字与色阶的刷新频率用不上渲染帧率。</summary>
    private const double RefreshMinIntervalMs = 150;
    private DateTime _lastRefreshAt = DateTime.MinValue;
    private DispatcherTimer? _refreshTimer;

    private void Refresh()
    {
        if (!IsInitialized) return;
        var chromeVisibility = ShowHeader ? Visibility.Visible : Visibility.Collapsed;
        // HeaderLegend 不再跟随 ShowHeader 隐藏：色阶条是读图必需的解释性元素
        foreach (var element in new UIElement[] { TopAccent, KeyboardWatermark, SideCode, SideTitle, HeaderPeriod })
            element.Visibility = chromeVisibility;
        ContentStack.Margin = ShowHeader ? new Thickness(20, 20, 20, 18) : new Thickness(0);
        FrameBorder.BorderThickness = ShowHeader ? new Thickness(1) : new Thickness(0);
        FrameBorder.Background = ShowHeader ? new SolidColorBrush(Color.FromArgb(0xEB, 0x12, 0x1A, 0x1D)) : Brushes.Transparent;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unmapped = new List<string>();
        if (ItemsSource is not null)
        {
            foreach (var item in ItemsSource)
            {
                if (!TryRead(item, out var name, out var count)) continue;
                if (!Aliases.TryGetValue(Normalize(name), out var key))
                {
                    if (count > 0) unmapped.Add($"{name}: {count:N0}");
                    continue;
                }
                counts.TryGetValue(key, out var prior);
                counts[key] = prior + count;
                if (!sources.TryGetValue(key, out var names)) sources[key] = names = new List<string>();
                names.Add($"{name}: {count:N0}");
            }
        }
        // 只统计有记录的按键：min/max 构成归一化区间，未记录键保持最低色
        long maximum = 0, minimum = 0;
        foreach (var value in counts.Values)
        {
            if (value <= 0) continue;
            if (value > maximum) maximum = value;
            if (minimum == 0 || value < minimum) minimum = value;
        }
        LegendMinText.Text = maximum > 0 ? minimum.ToString("N0", CultureInfo.CurrentCulture) : "-";
        LegendMaxText.Text = maximum > 0 ? maximum.ToString("N0", CultureInfo.CurrentCulture) : "-";
        Aliases.TryGetValue(Normalize(RecentKey), out var recent);
        foreach (var cap in KeyCaps)
        {
            counts.TryGetValue(cap.Id, out var count);
            sources.TryGetValue(cap.Id, out var names);
            cap.Update(count, minimum, maximum, IsRecording && cap.Id == recent, names);
        }
        MouseCountsText.Text = $"{MousePeriodLabel} 左 {LeftClickCount:N0} / 中 {MiddleClickCount:N0} / 右 {RightClickCount:N0}";
        StatusText.Text = IsRecording ? "● 正在记录 · 144 键扩展布局" : "○ 已暂停 · 144 键扩展布局";
        CoverageText.Text = unmapped.Count > 0 ? $"描边为最近输入 · 另有 {unmapped.Count} 类按键" : "描边为最近输入 · 次数实时同步";
        CoverageText.ToolTip = unmapped.Count > 0
            ? "以下按键未对应此布局，仍保留在完整统计中：\n" + string.Join("\n", unmapped)
            : "主键区与数字键盘分别计数；Win 键位合计左右 Windows 键。";
    }

    private static bool TryRead(object? item, out string key, out long count)
    {
        key = string.Empty;
        count = 0;
        if (item is null) return false;
        // 生产数据全是 KeyCountItem：直读属性；反射只给鸭子类型的测试源兜底，
        // 否则每次刷新要对几百条目做反射取值 + 字符串往返，纯浪费。
        // 这个具体类型只在主工程里编得动——tiltshot 夹具单编本文件不引用主工程，故用符号隔开，
        // 那边自动退回下面的反射兜底（KeyCountItem 也有 Key/Count 属性，行为一致只是慢一点点）。
#if KEYCOUNTITEM_FASTPATH
        if (item is XAssistant.ViewModels.KeyCountItem direct)
        {
            key = direct.Key;
            count = direct.Count;
            return key.Length > 0 && count >= 0;
        }
#endif
        var type = item.GetType();
        if (!ItemProperties.TryGetValue(type, out var properties))
        {
            properties = (type.GetProperty("Key"), type.GetProperty("Count") ?? type.GetProperty("Value"));
            ItemProperties[type] = properties;
        }
        key = properties.Key?.GetValue(item) as string ?? string.Empty;
        var value = properties.Count?.GetValue(item);
        return key.Length > 0 && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out count) && count >= 0;
    }

    private static string Normalize(string? value) => value == " " ? "SPACE" : new((value ?? string.Empty).Trim().ToUpperInvariant()
        .Where(c => !char.IsWhiteSpace(c) && c != '_').ToArray());

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string id, params string[] names) { foreach (var name in names) map[Normalize(name)] = id; }
        foreach (var key in Layout) Add(key.Id, key.Id);
        Add("ESC", "Esc", "Escape", "退出");
        Add("BACKSPACE", "Backspace", "Back", "退格");
        Add("TAB", "Tab", "制表");
        Add("CAPS", "Caps", "Caps Lock", "Capital", "大写锁定");
        Add("SPACE", "Space", "Spacebar", "空格", "空格键");
        Add("ENTER", "Enter", "Return", "回车");
        Add("LSHIFT", "Shift", "Left Shift", "LeftShift", "LShiftKey", "左 Shift");
        Add("RSHIFT", "Right Shift", "RightShift", "RShiftKey", "右 Shift");
        Add("LCTRL", "Ctrl", "Control", "Left Ctrl", "Left Control", "LeftCtrl", "LControlKey", "左 Ctrl");
        Add("RCTRL", "Right Ctrl", "Right Control", "RightCtrl", "RControlKey", "右 Ctrl");
        Add("LALT", "Alt", "Left Alt", "LeftAlt", "LMenu", "左 Alt");
        Add("RALT", "Right Alt", "RightAlt", "RMenu", "AltGr", "右 Alt");
        // The reference contains one Win key. Its tooltip explicitly reports
        // left/right source names; neither side is misrepresented as Fn.
        Add("WIN", "Win", "Windows", "Left Windows", "Right Windows", "LWin", "RWin", "LeftWin", "RightWin", "左 Win", "右 Win");
        Add("MENU", "Menu", "Apps", "Application", "Application Key", "应用程序");
        Add("INSERT", "Insert", "Ins", "插入");
        Add("DELETE", "Delete", "Del", "删除");
        Add("PAGEUP", "Page Up", "PageUp", "PgUp", "Prior");
        Add("PAGEDOWN", "Page Down", "PageDown", "PgDn", "Next");
        Add("LEFT", "Left", "Left Arrow", "←", "左");
        Add("RIGHT", "Right", "Right Arrow", "→", "右");
        Add("UP", "Up", "Up Arrow", "↑", "上");
        Add("DOWN", "Down", "Down Arrow", "↓", "下");
        Add("PRINTSCREEN", "Print Screen", "PrintScreen", "PrtSc", "Prnt Scrn", "Snapshot");
        Add("SCROLLLOCK", "Scroll Lock", "Scroll", "ScrLk");
        Add("PAUSE", "Pause", "Pause Break", "Break");
        Add("NUMLOCK", "Num Lock", "NumLock", "Num");
        for (int n = 0; n <= 9; n++) Add($"NUM{n}", $"Num {n}", $"NumPad{n}", $"Numpad {n}");
        Add("NUMDIVIDE", "Num /", "Num Divide", "Divide", "NumPadDivide");
        Add("NUMMULTIPLY", "Num *", "Num Multiply", "Multiply", "NumPadMultiply");
        Add("NUMSUBTRACT", "Num -", "Num −", "Num Subtract", "Subtract", "NumPadSubtract");
        Add("NUMADD", "Num +", "Num Add", "Add", "NumPadAdd");
        Add("NUMENTER", "Num Enter", "NumPadEnter");
        Add("NUMDECIMAL", "Num Del", "Num .", "Decimal", "Num Decimal", "NumPadDecimal");
        Add("MINUS", "-", "−", "OemMinus");
        Add("EQUALS", "=", "OemPlus");
        Add("BACKTICK", "`", "Oem3", "Oemtilde");
        Add("LBRACKET", "[", "OemOpenBrackets", "Oem4");
        Add("RBRACKET", "]", "OemCloseBrackets", "Oem6");
        Add("BACKSLASH", "\\", "Oem5", "OemPipe");
        Add("SEMICOLON", ";", "Oem1", "OemSemicolon");
        Add("QUOTE", "'", "Oem7", "OemQuotes");
        Add("COMMA", ",", "OemComma");
        Add("PERIOD", ".", "OemPeriod");
        Add("SLASH", "/", "Oem2", "OemQuestion");
        Add("MUTE", "Mute", "Volume Mute", "VolumeMute");
        Add("VOLDOWN", "Vol−", "Vol-", "Volume Down", "VolumeDown");
        Add("VOLUP", "Vol+", "Volume Up", "VolumeUp");
        Add("PLAY", "Play", "Play/Pause", "Media Play Pause", "MediaPlayPause");
        return map;
    }

    public sealed class KeyCap : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Id { get; }
        public string Label { get; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public long Count { get; private set; }
        public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
        public Brush HeatBrush { get; private set; } = MakeBrush(0x20, 0x2D, 0x30);
        public Brush LabelBrush { get; private set; } = MakeBrush(0xDA, 0xE7, 0xDD);
        public Brush CountBrush { get; private set; } = MakeBrush(0x98, 0xB4, 0xA7);
        public Brush OutlineBrush { get; private set; } = MakeBrush(0xBC, 0xD5, 0xA2, 0x2B);
        public string Tooltip { get; private set; } = string.Empty;
        internal KeyCap(KeyDefinition key) { Id = key.Id; Label = key.Label; X = key.X; Y = key.Y; Width = key.Width; }

        internal void Update(long count, long minimum, long maximum, bool recent, List<string>? sourceNames)
        {
            // 最少/最多次数归一化：本时段有记录的键铺满整个色阶，按得最多的始终是最亮
            double intensity = count <= 0
                ? 0
                : maximum <= minimum
                    ? 1
                    : PressedFloor
                        + (1 - PressedFloor)
                        * ((double)(count - minimum) / (maximum - minimum));
            // 强度量化到 256 级再比对：连打时 max 每轮都在动，未受影响的键的 intensity
            // 只会漂小数——不量化就全部算「变了」，闸门白设
            int intensityQ = (int)Math.Round(Math.Clamp(intensity, 0, 1) * 255);
            // 变更闸门：144 个键帽每轮都被扫，绝大多数这轮什么都没变——直接返回，
            // 不换刷、不拼 tooltip、不广播整键绑定失效；否则连打几秒 UI 就崩
            if (count == _shownCount && intensityQ == _shownIntensityQ && recent == _shownRecent) return;
            Count = count;
            HeatBrush = HeatColor(intensity);
            LabelBrush = intensity >= 0.72 ? MakeBrush(0x20, 0x31, 0x1A) : MakeBrush(0xDA, 0xE7, 0xDD);
            CountBrush = intensity >= 0.72 ? MakeBrush(0x43, 0x65, 0x28) : MakeBrush(0x98, 0xB4, 0xA7);
            OutlineBrush = recent ? MakeBrush(0xC4, 0xFF, 0x75) : MakeBrush(0xBC, 0xD5, 0xA2, 0x2B);
            string identity = Id.StartsWith("NUM", StringComparison.Ordinal) && Id != "NUMLOCK" ? $"数字键盘 {Label}" : Label;
            if (Id == "WIN") identity = "Win（左右 Windows 合计）";
            if (Id is "LSHIFT" or "LCTRL" or "LALT") identity = "左 " + Label;
            if (Id is "RSHIFT" or "RCTRL" or "RALT") identity = "右 " + Label;
            Tooltip = $"{identity} · {Count:N0} 次" + (sourceNames is { Count: > 0 } ? "\n" + string.Join("\n", sourceNames) : "\n此时间段无对应记录");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            _shownCount = count;
            _shownIntensityQ = intensityQ;
            _shownRecent = recent;
        }

        // 上一轮真正显示出去的三要素：只有它们变了才值得换刷与广播
        private long _shownCount = -1;
        private int _shownIntensityQ = -1;
        private bool _shownRecent;
    }

    /// <summary>把归一化强度 [0,1] 映射到色阶上的颜色，与 <see cref="BuildRampBrush"/> 同源。</summary>
    private static Color RampColor(double intensity)
    {
        double position = Math.Clamp(intensity, 0, 1) * (HeatRamp.Length - 1);
        int index = Math.Min((int)position, HeatRamp.Length - 2);
        double mix = position - index;
        byte Channel(int channel)
            => (byte)Math.Round(HeatRamp[index][channel]
                + (HeatRamp[index + 1][channel] - HeatRamp[index][channel]) * mix);
        return Color.FromRgb(Channel(0), Channel(1), Channel(2));
    }

    private static Brush HeatColor(double intensity)
    {
        var c = RampColor(intensity);
        return CachedBrush(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
    }

    private static Brush MakeBrush(byte r, byte g, byte b, byte a = 255) =>
        CachedBrush(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

    /// <summary>
    /// 按 ARGB 缓存冻结画刷：144 个键帽每轮刷新都要配刷，不缓存就是每秒几万个
    /// SolidColorBrush 对象的 GC 风暴；颜色取值空间只有几千种，缓存几乎满命中。
    /// </summary>
    private static readonly Dictionary<uint, Brush> BrushCache = new();

    private static Brush CachedBrush(uint argb)
    {
        if (BrushCache.TryGetValue(argb, out var hit)) return hit;
        // WPF Color 没有 FromUInt32：拆回字节走 FromArgb（System.Drawing 同名类型的 API，别记混）
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        BrushCache[argb] = brush;
        return brush;
    }

    /// <summary>
    /// 图例渐变条。按“条上位置 -> 实际映射后的颜色”采样，
    /// 因此条的最左端就是“按得最少的键”的真实颜色，而不是色阶原点的底色。
    /// </summary>
    private static Brush BuildRampBrush()
    {
        var gradient = new LinearGradientBrush
        {
            // MappingMode 默认 RelativeToBoundingBox：(0,0)->(1,0) 就是从左到右
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        const int steps = 24;
        for (int i = 0; i <= steps; i++)
        {
            double offset = (double)i / steps;
            gradient.GradientStops.Add(new GradientStop(
                RampColor(PressedFloor + (1 - PressedFloor) * offset), offset));
        }
        gradient.Freeze();
        return gradient;
    }

    // ===== 整块键盘随敲击倾斜 =====

    /// <summary>键帽行高，与 KeyboardHeatmap.xaml 里 ContentPresenter 的 Height 同值；改一处要同步另一处。</summary>
    private const double KeyRowHeight = 43;

    /// <summary>Viewbox 拿到多大就等于键盘能被画到多大：按实际缩放反算键帽字号。</summary>
    private void OnBoardSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => ApplyKeyCapFonts(e.NewSize);

    private void ApplyKeyCapFonts(Size available)
    {
        if (available.Width <= 1 || available.Height <= 1) return;
        double scale = Math.Min(available.Width / BoardDesignWidth, available.Height / BoardDesignHeight);
        // 屏幕上的字号 = 设计字号 × scale，所以反算就是「下限 ÷ scale」
        LabelFontSize = Math.Clamp(Math.Ceiling(MinLabelPx / scale), DesignLabelFont, MaxLabelFont);
        CountFontSize = Math.Clamp(Math.Ceiling(MinCountPx / scale), DesignCountFont, MaxCountFont);
        // 计数是次要信息：放大到上限还读不动就收掉，别把键帽糊成一团
        CountVisibility = CountFontSize * scale >= MinCountPx ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 键盘块在设计单位下的外接框。延迟到首次使用才算：静态字段初始化按声明顺序跑，
    /// 直接写在 <see cref="Layout"/> 之前会读到还没赋值的数组。
    /// </summary>
    private static Rect? _boardBox;
    internal static Rect BoardBox => _boardBox ??= ComputeBoardBox();

    /// <summary>
    /// 倾斜动画的时间驱动（弹簧、停留计时、收敛、渲染循环的挂卸都在它那儿）。
    /// 本控件只负责两头：把敲到的键换算成敲击向量喂进去，把算出的姿态写回键盘块的渲染变换。
    /// </summary>
    private readonly TiltDriver _tilt;

    /// <summary>逐键仿射的载体，与 <see cref="KeySlots"/> 同序，挂在键帽容器的 RenderTransform 上。</summary>
    private MatrixTransform[]? _keyTilts;

    /// <summary>
    /// 键帽槽位（画布坐标）。透视要逐键算雅可比，槽位就是它的输入；布局是静态的，所以全实例共用一份。
    /// 延迟到首次使用才算，理由同 <see cref="BoardBox"/>：静态字段按声明顺序初始化，写在 Layout 之前会读到空数组。
    /// </summary>
    private static Rect[]? _keySlots;
    private static Rect[] KeySlots => _keySlots ??= BuildKeySlots();

    /// <summary>敲一键就把倾斜目标交给驱动器；“等待输入”这类对不上键位的占位文字不该牵动键盘。</summary>
    private void StrikeTilt()
    {
        if (TryPressVector(RecentKey, out double u, out double v))
            _tilt.Press(u, v);
    }

    /// <summary>
    /// 键名 → 该键在板面上的归一化敲击位置。拆出来是给离屏夹具用的：倾斜动画拿到的键位对不对，
    /// 只看真键名能不能经过别名表、布局坐标、板框归一化这条链落到预期的 u / v 上。
    /// </summary>
    internal static bool TryPressVector(string? keyName, out double u, out double v)
    {
        u = v = 0;
        if (!Aliases.TryGetValue(Normalize(keyName), out var id) || !KeyById.TryGetValue(id, out var key)) return false;
        (u, v) = KeyboardTilt.PressVector(new Point(key.X + key.Width / 2, key.Y + KeyRowHeight / 2), BoardBox);
        return true;
    }

    /// <summary>
    /// 把姿态写回每个键帽自己的仿射变换。离屏夹具也走这条路径，保证验的就是实装用的那 144 块变换。
    /// 透视不是「整块板共用一个仿射矩阵」能画出来的：仿射保持平行性，远边永远不比近边短，
    /// 所以由 <see cref="KeyboardTilt.ApplyPose"/> 把单应逐键压成一阶仿射，144 块拼出梯形轮廓。
    /// </summary>
    internal void ApplyTilt(double u, double v)
    {
        if (!AttachKeyTilts()) return;   // 容器还没生成齐（首次排版前），下一帧再试
        var box = BoardBox;
        KeyboardTilt.ApplyPose(u, v, box.Width, box.Height, KeySlots, _keyTilts!);
    }

    /// <summary>
    /// 取得（必要时新建）挂在键帽容器上的变换数组。键盘块的容器由 Canvas 面板承载、不虚拟化，
    /// 一次生成长期复用，所以这个数组只建一次，每帧只改 <see cref="MatrixTransform.Matrix"/>。
    /// 返回 false 表示还有容器没生成：此时不能写姿态，否则写了一半会让剩下的键帽停在上一帧的位置上。
    /// </summary>
    private bool AttachKeyTilts()
    {
        var slots = KeySlots;
        if (_keyTilts is null || _keyTilts.Length != slots.Length) _keyTilts = new MatrixTransform[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            if (BoardHost.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter container) return false;
            _keyTilts[i] ??= new MatrixTransform();
            // 容器被重建过则 RenderTransform 会跟着丢掉，按引用比一下重新挂上
            if (!ReferenceEquals(container.RenderTransform, _keyTilts[i])) container.RenderTransform = _keyTilts[i];
        }
        return true;
    }

    /// <summary>离屏夹具的另一个缝隙：按固定步长把倾斜动画推完，不必等真渲染帧。</summary>
    internal void StepTilt(double deltaSeconds) => _tilt.Step(deltaSeconds);

    private static Rect ComputeBoardBox()
    {
        double left = double.MaxValue, right = double.MinValue, top = double.MaxValue, bottom = double.MinValue;
        foreach (var key in Layout)
        {
            left = Math.Min(left, key.X);
            right = Math.Max(right, key.X + key.Width);
            top = Math.Min(top, key.Y);
            bottom = Math.Max(bottom, key.Y + KeyRowHeight);
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>把每颗键帽的槽位按布局声明算成矩形，供逐键投影用。</summary>
    private static Rect[] BuildKeySlots()
    {
        var slots = new Rect[Layout.Length];
        for (int i = 0; i < Layout.Length; i++)
            slots[i] = new Rect(Layout[i].X, Layout[i].Y, Layout[i].Width, KeyRowHeight);
        return slots;
    }

    // ===== 位移窗内的小鼠标图标 =====

    /// <summary>图标可移动范围还要向内收这么多像素，避免图标压住位移窗边框；与 XAML 里的标记矩形 Margin 同值。</summary>
    private const double GlyphInset = 3;

    /// <summary>点击时点亮的亮度，以及各块静置亮度；与 KeyboardHeatmap.xaml 里的初值一致。</summary>
    private const double ZoneLit = 0.95;

    private const double ZoneRest = 0.12;
    private const double WheelRest = 0.34;

    /// <summary>
    /// 图标跟随时长。必须短于取样节拍，否则每次动画只走完一小截就被新目标重启，
    /// 图标会持续落后于手——节拍加快后原来的 140 ms 就犯了这个毛病。
    /// </summary>
    private static readonly Duration GlyphGlide = TimeSpan.FromMilliseconds(60);

    private static readonly Duration ZoneFade = TimeSpan.FromMilliseconds(420);

    private static readonly Duration ZoneRelease = TimeSpan.FromMilliseconds(220);

    private UIElement? _litZone;

    private void OnMoveSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => LayoutMouseGlyph();

    /// <summary>
    /// 把归一化偏移换算成位移窗内的像素。ViewModel 不知道控件实际尺寸，而图标又必须被钳在矩形内，
    /// 所以换算留在 View 层；用 TranslateTransform 而非 Canvas.Left，避免每个取样节拍触发一次布局。
    /// </summary>
    private void LayoutMouseGlyph()
    {
        if (MoveSurface is null || MouseGlyph is null || GlyphShift is null)
            return;
        var reachX = Math.Max(0, (MoveSurface.ActualWidth - MouseGlyph.ActualWidth) / 2 - GlyphInset);
        var reachY = Math.Max(0, (MoveSurface.ActualHeight - MouseGlyph.ActualHeight) / 2 - GlyphInset);
        Glide(TranslateTransform.XProperty, Math.Clamp(MouseOffsetX, -1d, 1d) * reachX);
        Glide(TranslateTransform.YProperty, Math.Clamp(MouseOffsetY, -1d, 1d) * reachY);
    }

    private void Glide(DependencyProperty axis, double target) => GlyphShift.BeginAnimation(axis,
        new DoubleAnimation(target, GlyphGlide)
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        });

    /// <summary>点亮本次点击对应的区域；同一时刻只保留一块亮起，其余淡回静置亮度。</summary>
    private void FlashMouseButton(string? button)
    {
        if (LeftClickZone is null)
            return;
        // 用 UIElement 接：本文件没有 using System.Windows.Controls（避开与 WinForms 的类型歧义）
        UIElement? zone = button switch
        {
            "Left" => LeftClickZone,
            "Right" => RightClickZone,
            "Middle" => WheelZone,
            _ => null,
        };
        if (zone is null)
            return;
        var previous = _litZone;
        if (previous is not null && !ReferenceEquals(previous, zone))
            FadeToRest(previous);
        _litZone = zone;
        // 先写局部值当作动画起点：连点同一区域时从满亮接着淡出，不会先闪回静置亮度
        zone.Opacity = ZoneLit;
        zone.BeginAnimation(OpacityProperty, new DoubleAnimation(RestOpacity(zone), ZoneFade));
    }

    private void FadeToRest(UIElement zone) =>
        zone.BeginAnimation(OpacityProperty, new DoubleAnimation(RestOpacity(zone), ZoneRelease));

    /// <summary>滚轮块静置时更亮：它没有左右键那样的分界，靠亮度才能认出来。</summary>
    private double RestOpacity(UIElement zone) => ReferenceEquals(zone, WheelZone) ? WheelRest : ZoneRest;

    internal readonly record struct KeyDefinition(string Id, string Label, double X, double Y, double Width);

    // Coordinates and widths are transcribed from the reference .pen layout.
    private static readonly KeyDefinition[] Layout =
    {
        new("M1", "M1", 0, 48, 32.5),
        new("M2", "M2", 36.5, 48, 32.5),
        new("M3", "M3", 73, 48, 32.5),
        new("M4", "M4", 109.5, 48, 32.5),
        new("M5", "M5", 0, 96, 32.5),
        new("M6", "M6", 36.5, 96, 32.5),
        new("M7", "M7", 73, 96, 32.5),
        new("M8", "M8", 109.5, 96, 32.5),
        new("M9", "M9", 0, 144, 32.5),
        new("M10", "M10", 36.5, 144, 32.5),
        new("M11", "M11", 73, 144, 32.5),
        new("M12", "M12", 109.5, 144, 32.5),
        new("M13", "M13", 0, 192, 32.5),
        new("M14", "M14", 36.5, 192, 32.5),
        new("M15", "M15", 73, 192, 32.5),
        new("M16", "M16", 109.5, 192, 32.5),
        new("M17", "M17", 0, 240, 32.5),
        new("M18", "M18", 36.5, 240, 32.5),
        new("M19", "M19", 73, 240, 32.5),
        new("M20", "M20", 109.5, 240, 32.5),
        new("M21", "M21", 0, 288, 32.5),
        new("M22", "M22", 36.5, 288, 32.5),
        new("M23", "M23", 73, 288, 32.5),
        new("M24", "M24", 109.5, 288, 32.5),
        new("F13", "F13", 156, 0, 55.5625),
        new("F14", "F14", 216.5625, 0, 55.5625),
        new("F15", "F15", 277.125, 0, 55.5625),
        new("F16", "F16", 337.6875, 0, 55.5625),
        new("F17", "F17", 398.25, 0, 55.5625),
        new("F18", "F18", 458.8125, 0, 55.5625),
        new("F19", "F19", 519.375, 0, 55.5625),
        new("F20", "F20", 579.9375, 0, 55.5625),
        new("F21", "F21", 640.5, 0, 55.5625),
        new("F22", "F22", 701.0625, 0, 55.5625),
        new("F23", "F23", 761.625, 0, 55.5625),
        new("F24", "F24", 822.1875, 0, 55.5625),
        new("MUTE", "Mute", 882.75, 0, 55.5625),
        new("VOLDOWN", "Vol−", 943.3125, 0, 55.5625),
        new("VOLUP", "Vol+", 1003.875, 0, 55.5625),
        new("PLAY", "Play", 1064.4375, 0, 55.5625),
        new("ESC", "Esc", 156, 48, 55.5625),
        new("F1", "F1", 216.5625, 48, 55.5625),
        new("F2", "F2", 277.125, 48, 55.5625),
        new("F3", "F3", 337.6875, 48, 55.5625),
        new("F4", "F4", 398.25, 48, 55.5625),
        new("F5", "F5", 458.8125, 48, 55.5625),
        new("F6", "F6", 519.375, 48, 55.5625),
        new("F7", "F7", 579.9375, 48, 55.5625),
        new("F8", "F8", 640.5, 48, 55.5625),
        new("F9", "F9", 701.0625, 48, 55.5625),
        new("F10", "F10", 761.625, 48, 55.5625),
        new("F11", "F11", 822.1875, 48, 55.5625),
        new("F12", "F12", 882.75, 48, 55.5625),
        new("PRINTSCREEN", "PrtSc", 943.3125, 48, 55.5625),
        new("SCROLLLOCK", "ScrLk", 1003.875, 48, 55.5625),
        new("PAUSE", "Pause", 1064.4375, 48, 55.5625),
        new("BACKTICK", "`", 156, 96, 37),
        new("1", "1", 198, 96, 37),
        new("2", "2", 240, 96, 37),
        new("3", "3", 282, 96, 37),
        new("4", "4", 324, 96, 37),
        new("5", "5", 366, 96, 37),
        new("6", "6", 408, 96, 37),
        new("7", "7", 450, 96, 37),
        new("8", "8", 492, 96, 37),
        new("9", "9", 534, 96, 37),
        new("0", "0", 576, 96, 37),
        new("MINUS", "−", 618, 96, 37),
        new("EQUALS", "=", 660, 96, 37),
        new("BACKSPACE", "Backspace", 702, 96, 74),
        new("INSERT", "Ins", 788, 96, 40.66666667),
        new("HOME", "Home", 833.66666667, 96, 40.66666667),
        new("PAGEUP", "PgUp", 879.33333333, 96, 40.66666667),
        new("NUMLOCK", "Num", 932, 96, 43.25),
        new("NUMDIVIDE", "/", 980.25, 96, 43.25),
        new("NUMMULTIPLY", "*", 1028.5, 96, 43.25),
        new("NUMSUBTRACT", "−", 1076.75, 96, 43.25),
        new("TAB", "Tab", 156, 144, 55.5),
        new("Q", "Q", 216.5, 144, 37),
        new("W", "W", 258.5, 144, 37),
        new("E", "E", 300.5, 144, 37),
        new("R", "R", 342.5, 144, 37),
        new("T", "T", 384.5, 144, 37),
        new("Y", "Y", 426.5, 144, 37),
        new("U", "U", 468.5, 144, 37),
        new("I", "I", 510.5, 144, 37),
        new("O", "O", 552.5, 144, 37),
        new("P", "P", 594.5, 144, 37),
        new("LBRACKET", "[", 636.5, 144, 37),
        new("RBRACKET", "]", 678.5, 144, 37),
        new("BACKSLASH", "\\", 720.5, 144, 55.5),
        new("DELETE", "Del", 788, 144, 40.66666667),
        new("END", "End", 833.66666667, 144, 40.66666667),
        new("PAGEDOWN", "PgDn", 879.33333333, 144, 40.66666667),
        new("NUM7", "7", 932, 144, 43.25),
        new("NUM8", "8", 980.25, 144, 43.25),
        new("NUM9", "9", 1028.5, 144, 43.25),
        new("NUMADD", "+", 1076.75, 144, 43.25),
        new("CAPS", "Caps", 156, 192, 65.33333333),
        new("A", "A", 226.33333333, 192, 37.33333333),
        new("S", "S", 268.66666667, 192, 37.33333333),
        new("D", "D", 311, 192, 37.33333333),
        new("F", "F", 353.33333333, 192, 37.33333333),
        new("G", "G", 395.66666667, 192, 37.33333333),
        new("H", "H", 438, 192, 37.33333333),
        new("J", "J", 480.33333333, 192, 37.33333333),
        new("K", "K", 522.66666667, 192, 37.33333333),
        new("L", "L", 565, 192, 37.33333333),
        new("SEMICOLON", ";", 607.33333333, 192, 37.33333333),
        new("QUOTE", "'", 649.66666667, 192, 37.33333333),
        new("ENTER", "Enter", 692, 192, 84),
        new("NUM4", "4", 932, 192, 43.25),
        new("NUM5", "5", 980.25, 192, 43.25),
        new("NUM6", "6", 1028.5, 192, 43.25),
        new("LSHIFT", "Shift", 156, 240, 84.75),
        new("Z", "Z", 245.75, 240, 37.66666667),
        new("X", "X", 288.41666667, 240, 37.66666667),
        new("C", "C", 331.08333333, 240, 37.66666667),
        new("V", "V", 373.75, 240, 37.66666667),
        new("B", "B", 416.41666667, 240, 37.66666667),
        new("N", "N", 459.08333333, 240, 37.66666667),
        new("M", "M", 501.75, 240, 37.66666667),
        new("COMMA", ",", 544.41666667, 240, 37.66666667),
        new("PERIOD", ".", 587.08333333, 240, 37.66666667),
        new("SLASH", "/", 629.75, 240, 37.66666667),
        new("RSHIFT", "Shift", 672.41666667, 240, 103.56),
        new("UP", "↑", 833.66666667, 240, 40.66666667),
        new("NUM1", "1", 932, 240, 43.25),
        new("NUM2", "2", 980.25, 240, 43.25),
        new("NUM3", "3", 1028.5, 240, 43.25),
        new("NUMENTER", "Enter", 1076.75, 240, 43.25),
        new("LCTRL", "Ctrl", 156, 288, 52.23214286),
        new("WIN", "Win", 213.23214286, 288, 52.23214286),
        new("LALT", "Alt", 270.46428571, 288, 52.23214286),
        new("SPACE", "Space", 327.69642857, 288, 208.92857143),
        new("RALT", "Alt", 541.625, 288, 52.23214286),
        new("FN", "Fn", 598.85714286, 288, 52.23214286),
        new("MENU", "Menu", 656.08928571, 288, 52.23214286),
        new("RCTRL", "Ctrl", 713.32142857, 288, 62.67857143),
        new("LEFT", "←", 788, 288, 40.66666667),
        new("DOWN", "↓", 833.66666667, 288, 40.66666667),
        new("RIGHT", "→", 879.33333333, 288, 40.66666667),
        new("NUM0", "0", 932, 288, 89),
        new("NUMDECIMAL", ".", 1026, 288, 44.5),
    };
}
