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
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

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
    public static readonly DependencyProperty MouseMovementTextProperty = RegisterMouse<string>(nameof(MouseMovementText), "移动数据未采集");
    public static readonly DependencyProperty MouseWheelTextProperty = RegisterMouse<string>(nameof(MouseWheelText), "滚轮数据未采集");

    /// <summary>小鼠标图标在位移窗内的归一化偏移（-1..1），像素换算由本控件按实际尺寸完成。</summary>
    public static readonly DependencyProperty MouseOffsetXProperty = RegisterMouse(nameof(MouseOffsetX), 0d, MouseOffsetChanged);
    public static readonly DependencyProperty MouseOffsetYProperty = RegisterMouse(nameof(MouseOffsetY), 0d, MouseOffsetChanged);

    public static readonly DependencyProperty MouseDeltaTextProperty = RegisterMouse<string>(nameof(MouseDeltaText), "Δx 0 · Δy 0 px");
    public static readonly DependencyProperty MouseDistanceTextProperty = RegisterMouse<string>(nameof(MouseDistanceText), "移动 今日 0.00 m · 累计 0.00 m");

    /// <summary>最近一次按下的键名（Left/Middle/Right），与自增的 <see cref="MouseClickPulse"/> 配合点亮对应区域。</summary>
    public static readonly DependencyProperty MouseButtonProperty = RegisterMouse<string>(nameof(MouseButton), string.Empty);
    public static readonly DependencyProperty MouseClickPulseProperty = RegisterMouse(nameof(MouseClickPulse), 0L, MouseClickChanged);

    /// <summary>垫在鼠标读数下方的 X/Y 偏移轨迹，坐标已是控件内那块 Canvas 的设计尺寸，本控件不再换算。</summary>
    public static readonly DependencyProperty MouseTrailXPointsProperty =
        RegisterMouse(nameof(MouseTrailXPoints), new PointCollection());
    public static readonly DependencyProperty MouseTrailYPointsProperty =
        RegisterMouse(nameof(MouseTrailYPoints), new PointCollection());

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
    public PointCollection MouseTrailXPoints { get => (PointCollection)GetValue(MouseTrailXPointsProperty); set => SetValue(MouseTrailXPointsProperty, value); }
    public PointCollection MouseTrailYPoints { get => (PointCollection)GetValue(MouseTrailYPointsProperty); set => SetValue(MouseTrailYPointsProperty, value); }

    public ObservableCollection<KeyCap> KeyCaps { get; } = new();
    private INotifyCollectionChanged? _collection;
    private readonly HashSet<INotifyPropertyChanged> _observedItems = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<Type, (PropertyInfo? Key, PropertyInfo? Count)> ItemProperties = new();
    private bool _observing;
    private bool _refreshPending;
    private static readonly Dictionary<string, string> Aliases;

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

    static KeyboardHeatmap() { Aliases = BuildAliases(); }

    public KeyboardHeatmap()
    {
        foreach (var key in Layout)
            KeyCaps.Add(new KeyCap(key));
        InitializeComponent();
        LegendBar.Background = BuildRampBrush();
        Loaded += (_, _) => { _observing = true; ObserveSource(); Refresh(); LayoutMouseGlyph(); };
        Unloaded += (_, _) => { _observing = false; DetachSource(); };
        Refresh();
    }

    private static DependencyProperty Register<T>(string name, T value) => DependencyProperty.Register(
        name, typeof(T), typeof(KeyboardHeatmap), new PropertyMetadata(value, VisualPropertyChanged));

    /// <summary>
    /// 鼠标移动相关属性的注册。这些值随取样节拍高频变化（节拍定义在 ClickCounterViewModel），
    /// 绝不能再走 <see cref="VisualPropertyChanged"/>：那条路径会连带重建 144 个键帽的着色，
    /// 把热力图的开销放大到跟随刷新频率。
    /// </summary>
    private static DependencyProperty RegisterMouse<T>(string name, T value, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(KeyboardHeatmap), new PropertyMetadata(value, changed));

    private static void MouseOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyboardHeatmap)d).LayoutMouseGlyph();

    private static void MouseClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyboardHeatmap)d).FlashMouseButton(((KeyboardHeatmap)d).MouseButton);

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
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => { _refreshPending = false; Refresh(); }));
    }

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
            Count = count;
            // 最少/最多次数归一化：本时段有记录的键铺满整个色阶，按得最多的始终是最亮
            double intensity = count <= 0
                ? 0
                : maximum <= minimum
                    ? 1
                    : PressedFloor
                        + (1 - PressedFloor)
                        * ((double)(count - minimum) / (maximum - minimum));
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
        }
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
        var brush = new SolidColorBrush(RampColor(intensity));
        brush.Freeze();
        return brush;
    }

    private static Brush MakeBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
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
