using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services;
using XAssistant.Services.Interfaces;
using Point = System.Windows.Point;

namespace XAssistant.ViewModels;

public partial class ClickCounterViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 移动取样节拍。20 Hz 是为了让背景轨迹曲线看着连续；文本读数也跟着这个节拍走，
    /// 每秒二十次字符串分配对这个量级的数据仍然可忽略。
    /// </summary>
    private const int MovementSampleIntervalMs = 50;

    /// <summary>
    /// 一个取样窗口内多少像素的净位移算作把指示图标“打满”。注意量纲是每拍位移而不是每秒速率：
    /// 一次甩动的峰值只取决于它总共移动了多少像素，与被拆成一拍还是两拍无关，
    /// 所以调快节拍不会把位移窗调敏感（只有持续匀速时因逐拍衰减而差约一成）。
    /// </summary>
    private const double FullDeflectionPixels = 220;

    /// <summary>
    /// 偏移回落到中心的时间常数（秒）。原本写死的“每拍乘 0.55”换算过来就是 e^(-t/0.167)。
    /// 必须按秒而不是按拍定义：按拍固定比例时，回落时长等于跟着节拍变（时间常数 = -Δt/ln f），
    /// 取样节拍从 100 ms 提到 50 ms 会让回落快一倍，图标变成手一停就弹回中心。
    /// </summary>
    private const double DeflectionTimeConstantSeconds = 0.167;

    /// <summary>速率的指数平滑权重：单个窗口的突发位移不会把读数抽成毛刺。</summary>
    private const double SpeedSmoothing = 0.3;

    /// <summary>轨迹背景图的设计尺寸，需与 KeyboardHeatmap 里那块 Canvas 的宽高一致。</summary>
    private const double TrailWidth = 260;
    private const double TrailHeight = 46;

    /// <summary>
    /// 一个取样窗口内多少格滚轮算把曲线打满。量纲与 <see cref="FullDeflectionPixels"/> 一样是每拍位移而不是每秒速率；
    /// 由于偏移按拍衰减，稳态振幅约等于该值的 3.9 倍（时间常数与节拍之比决定），所以单滚一格只顶到约四分之一。
    /// </summary>
    private const double FullDeflectionWheelNotches = 2;

    /// <summary>
    /// 轨迹时间跨度。偏移本身约 0.17 秒就回中心，窗口拉太长只会看到一条贴零的直线，
    /// 6 秒足够看清手上最近几次甩动的形状。
    /// </summary>
    private const int TrailWindowSeconds = 6;

    /// <summary>横向每秒推进的像素，与输入节奏曲线同一套算法：按“距今多少秒”定 x，曲线因此连续左移。</summary>
    private const double TrailPixelsPerSecond = TrailWidth / TrailWindowSeconds;

    /// <summary>累计移动像素落库的最小间隔，避免每个取样节拍都开一次 SQLite 连接。</summary>
    private const int MovementFlushIntervalMs = 1000;

    private readonly IMouseClickHookService _hookService;
    private readonly IClickDatabaseService _dbService;
    private readonly IConfigurationService _configService;
    private readonly DispatcherTimer _movementTimer;
    private DateTime _lastMovementFlushAt = DateTime.Now;
    private DateTime _lastDayCheckAt = DateTime.Now;
    private DateTime _movementDay = DateTime.Today;
    private double _pendingFlushPixels;
    private double _smoothedPixelsPerSecond;
    private double _todayMovementPixels;
    private double _totalMovementPixels;
    private double _sessionWheelNotches;
    /// <summary>滚轮曲线的当前振幅（-1..1）。逐拍衰减，不直接上屏，只用来画轨迹。</summary>
    private double _wheelDeflection;
    private long _clickSequence;
    private bool _disposed;

    /// <summary>轨迹曲线的滑动样本：偏移值配上落地时刻，下标不需要与时间对应，旧点靠“距今多少秒”算 x 后自然滑出。</summary>
    private readonly Queue<(DateTime At, double X, double Y, double Wheel)> _trail = new();

    [ObservableProperty]
    private int _leftClickCount;

    [ObservableProperty]
    private int _middleClickCount;

    [ObservableProperty]
    private int _rightClickCount;

    [ObservableProperty]
    private bool _isRecording;

    // 今天
    [ObservableProperty]
    private int _leftClickToday;

    [ObservableProperty]
    private int _middleClickToday;

    [ObservableProperty]
    private int _rightClickToday;

    // 日历选中日期及对应点击量
    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private int _selectedDateLeftCount;

    [ObservableProperty]
    private int _selectedDateMiddleCount;

    [ObservableProperty]
    private int _selectedDateRightCount;

    // ===== 鼠标移动：热力图上小鼠标指示图标的偏移、增量读数与里程 =====

    /// <summary>指示图标的归一化偏移（-1..1，X 向右、Y 向下为正）；像素换算由 View 层完成。</summary>
    [ObservableProperty]
    private double _mouseOffsetX;

    [ObservableProperty]
    private double _mouseOffsetY;

    /// <summary>
    /// 垫在鼠标读数下方的两条轨迹。给设计尺寸下的绝对坐标而非归一化值：
    /// 这跟输入节奏曲线同一走法，由 View 层的 Viewbox 拉到实际像素，VM 不需要知道控件多大。
    /// </summary>
    [ObservableProperty]
    private PointCollection _mouseTrailXPoints = new();

    [ObservableProperty]
    private PointCollection _mouseTrailYPoints = new();

    /// <summary>滚轮速率轨迹，与 X/Y 同一条时间轴；虚线画在控件那侧。</summary>
    [ObservableProperty]
    private PointCollection _mouseTrailWheelPoints = new();

    /// <summary>本段取样窗口的净位移读数。</summary>
    [ObservableProperty]
    private string _mouseDeltaText = IdleDeltaText;

    /// <summary>移动速率读数，静止时为提示文案。</summary>
    [ObservableProperty]
    private string _mouseMovementText = IdleMovementText();

    /// <summary>今日与累计移动距离，单位米。</summary>
    [ObservableProperty]
    private string _mouseDistanceText = "今日 0.00 m · 累计 0.00 m";

    /// <summary>本次运行以来的滚轮格数。</summary>
    [ObservableProperty]
    private string _mouseWheelText = "滚轮数据未采集";

    /// <summary>最近一次按下的键名，驱动指示图标上对应区域的高亮。</summary>
    [ObservableProperty]
    private string _mouseButton = string.Empty;

    /// <summary>点击计数。连点同一个键时 MouseButton 不变，靠这个自增值让高亮重新播放。</summary>
    [ObservableProperty]
    private long _mouseClickPulse;

    /// <summary>今日移动距离，米。</summary>
    [ObservableProperty]
    private double _todayDistanceMeters;

    /// <summary>累计移动距离，米。</summary>
    [ObservableProperty]
    private double _totalDistanceMeters;

    private const string IdleDeltaText = "Δx 0 · Δy 0 px";

    /// <summary>提示里的取样窗口直接引用常量，与曲线参数文案同一约定：不让文案与实现漂移。</summary>
    private static string IdleMovementText() => $"静止 · 移动量按 {MovementSampleIntervalMs} ms 窗口取样";

    // ===== 新增：首页用聚合属性 =====
    public int MouseTodayClicks => LeftClickToday + MiddleClickToday + RightClickToday;
    public int MouseTotalClicks => LeftClickCount + MiddleClickCount + RightClickCount;

    public string MouseRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush MouseRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public ClickCounterViewModel(
        IMouseClickHookService hookService,
        IClickDatabaseService dbService,
        IConfigurationService configService
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;

        // 加载历史总计
        var counts = _dbService.GetClickCounts();
        LeftClickCount = counts["Left"];
        MiddleClickCount = counts["Middle"];
        RightClickCount = counts["Right"];

        // 移动里程：库里存的是屏幕路径像素，到显示时才折算成米，
        // 这样换显示器/改分辨率不会把历史数据污染成另一种量纲
        _todayMovementPixels = _dbService.GetMovementPixels(DateTime.Today);
        _totalMovementPixels = _dbService.GetTotalMovementPixels();
        RefreshDistanceText();

        RefreshDailyCounts();
        LoadCountsForDate(SelectedDate);

        _hookService.MouseClicked += OnMouseClicked;

        _movementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MovementSampleIntervalMs) };
        _movementTimer.Tick += OnMovementTick;
        _movementTimer.Start();

        if (_configService.GetRecordingAutoStart())
        {
            StartRecording();
        }
    }

    // 当 IsRecording 变化时，自动通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(MouseRecordingStatus));
        OnPropertyChanged(nameof(MouseRecordingColor));
    }

    // SelectedDate 变更时自动加载对应日期的点击量
    partial void OnSelectedDateChanged(DateTime value)
    {
        LoadCountsForDate(value);
    }

    private void LoadCountsForDate(DateTime date)
    {
        var dayCounts = _dbService.GetClickCountsByDate(date);
        SelectedDateLeftCount = dayCounts["Left"];
        SelectedDateMiddleCount = dayCounts["Middle"];
        SelectedDateRightCount = dayCounts["Right"];
    }

    public void RefreshDailyCounts()
    {
        var today = _dbService.GetClickCountsByDate(DateTime.Today);
        LeftClickToday = today["Left"];
        MiddleClickToday = today["Middle"];
        RightClickToday = today["Right"];
        SyncSelectedTodayCounts();
        OnPropertyChanged(nameof(MouseTodayClicks));
    }

    // The selected-day panel shares today's in-memory values while recording.
    // Older selected dates retain their database snapshot until the date changes.
    private void SyncSelectedTodayCounts()
    {
        if (SelectedDate.Date != DateTime.Today)
            return;

        SelectedDateLeftCount = LeftClickToday;
        SelectedDateMiddleCount = MiddleClickToday;
        SelectedDateRightCount = RightClickToday;
    }

    private DateTime _lastRefreshDate = DateTime.Today;

    private void OnMouseClicked(string button)
    {
        if (!IsRecording)
            return;

        // 让指示图标上对应区域闪一下；相同键连点也要重播，所以附一个自增序号
        MouseButton = button;
        MouseClickPulse = ++_clickSequence;

        switch (button)
        {
            case "Left":
                LeftClickCount++;
                break;
            case "Middle":
                MiddleClickCount++;
                break;
            case "Right":
                RightClickCount++;
                break;
        }

        _dbService.SaveClick(new MouseClickRecord { Button = button, ClickTime = DateTime.Now });

        if (DateTime.Today != _lastRefreshDate)
        {
            RefreshDailyCounts();
            _lastRefreshDate = DateTime.Today;
        }
        else
        {
            switch (button)
            {
                case "Left":
                    LeftClickToday++;
                    break;
                case "Middle":
                    MiddleClickToday++;
                    break;
                case "Right":
                    RightClickToday++;
                    break;
            }

            SyncSelectedTodayCounts();
            OnPropertyChanged(nameof(MouseTodayClicks));
        }

        // 通知聚合属性更新
        OnPropertyChanged(nameof(MouseTotalClicks));
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        IsRecording = false;
        _configService.SetRecordingAutoStart(false);
    }

    /// <summary>
    /// 拉取一段鼠标移动量。钩子只累加、不推送，因此取样频率与钩子频率完全解耦；
    /// 静止时不分配任何字符串，只有指示图标还在向中心回落、或轨迹里还有未滑出的点时才继续更新。
    /// </summary>
    private void OnMovementTick(object? sender, EventArgs e)
    {
        var sample = _hookService.ReadMovementSample();
        // 滚轮曲线逐拍都要推一下：没有滚轮的那一拍也得靠衰减把上一个脉冲拉回零线，否则台阶会留在曲线上
        StepWheel(sample);

        if (sample is { } moved)
        {
            if (moved.WheelNotches != 0)
            {
                _sessionWheelNotches += moved.WheelNotches;
                MouseWheelText = $"滚轮 本次 {_sessionWheelNotches:+0.##;-0.##;0} 格";
            }
            if (moved.PathPixels > 0)
            {
                AccumulateMovement(moved);
                Deflect(moved);
                ReportSpeed(moved);
            }
            else if (DisplacementSettling())
            {
                // 只滚了滚轮、指针没动的一拍：位移这边等同静止，图标该继续回落而不是冻在原地
                Relax();
            }
        }
        else if (DisplacementSettling())
        {
            Relax();
        }

        RecordTrail();

        if (_pendingFlushPixels > 0
            && (DateTime.Now - _lastMovementFlushAt).TotalMilliseconds >= MovementFlushIntervalMs)
        {
            _lastMovementFlushAt = DateTime.Now;
            FlushMovement();
        }
        // 每秒一次跨午夜检查：暂停期间根本没有样本，不能只靠移动事件来发现“天变了”
        if ((DateTime.Now - _lastDayCheckAt).TotalMilliseconds >= MovementFlushIntervalMs)
        {
            _lastDayCheckAt = DateTime.Now;
            RollMovementDay();
        }
    }

    /// <summary>把本段路径计入今日/累计里程。</summary>
    private void AccumulateMovement(MouseMovementSample sample)
    {
        if (!IsRecording)
            return;
        RollMovementDay();
        _todayMovementPixels += sample.PathPixels;
        _totalMovementPixels += sample.PathPixels;
        _pendingFlushPixels += sample.PathPixels;
        RefreshDistanceText();
    }

    /// <summary>
    /// 跨午夜结转：先把残留路径结到旧的一天，再把今日里程切到新的一天。
    /// 顺序反了就会把今天的位移记到昨天名下。
    /// </summary>
    private void RollMovementDay()
    {
        var today = DateTime.Today;
        if (today == _movementDay)
            return;
        FlushMovement();
        _movementDay = today;
        _todayMovementPixels = _dbService.GetMovementPixels(today);
        RefreshDistanceText();
    }

    /// <summary>
    /// 按速度而非累计行程偏移：图标朝指针移动方向偏，持续快速移动顶到矩形边界，
    /// 停下后回落中心。若直接用累计行程，图标会长期贴在某个角落不动，反而读不出东西。
    /// </summary>
    private void Deflect(MouseMovementSample sample)
    {
        var decay = DecayFor(sample.ElapsedSeconds);
        MouseOffsetX = Math.Clamp(
            MouseOffsetX * decay + sample.DeltaX / FullDeflectionPixels, -1d, 1d);
        MouseOffsetY = Math.Clamp(
            MouseOffsetY * decay + sample.DeltaY / FullDeflectionPixels, -1d, 1d);
    }

    private void ReportSpeed(MouseMovementSample sample)
    {
        var instant = sample.PathPixels / sample.ElapsedSeconds;
        _smoothedPixelsPerSecond = _smoothedPixelsPerSecond <= 0.5
            ? instant
            : _smoothedPixelsPerSecond + (instant - _smoothedPixelsPerSecond) * SpeedSmoothing;
        MouseDeltaText = $"Δx {Signed(sample.DeltaX)} · Δy {Signed(sample.DeltaY)} px / {sample.ElapsedSeconds * 1000:F0} ms";
        // 读数用平滑值：单个窗口被一次快速扫动抽中时，原始瞬时值会跳出一个无意义的尖峰
        MouseMovementText = $"≈ {_smoothedPixelsPerSecond:F0} px/s · 折算 {DisplayMetrics.PixelsToMeters(_smoothedPixelsPerSecond):F2} m/s";
    }

    /// <summary>静止：速率归零、Δ 读数归零，图标按衰减回中心。</summary>
    private void Relax()
    {
        _smoothedPixelsPerSecond = 0;
        MouseMovementText = IdleMovementText();
        MouseDeltaText = IdleDeltaText;
        MouseOffsetX = Decay(MouseOffsetX);
        MouseOffsetY = Decay(MouseOffsetY);
    }

    /// <summary>静止回落用的衰减：没有样本可参，拿标称节拍当作窗口长度。</summary>
    private static double Decay() => DecayFor(MovementSampleIntervalMs / 1000d);

    private static double DecayFor(double seconds) => Math.Exp(-seconds / DeflectionTimeConstantSeconds);

    private static double Decay(double value) => Math.Abs(value) < 0.01 ? 0 : value * Decay();

    /// <summary>位移这边还没静下来：指示图标仍在回落，或速率读数还没归零。</summary>
    private bool DisplacementSettling() =>
        MouseOffsetX != 0 || MouseOffsetY != 0 || _smoothedPixelsPerSecond > 0;

    /// <summary>
    /// 滚轮折算成 -1..1 的曲线振幅。一格滚轮在单拍里只是一个矩形脉冲，
    /// 不跟一套衰减就会把曲线画成上下台阶；衰减时间常数与位移共用，两条曲线才能横向对照。
    /// </summary>
    private void StepWheel(MouseMovementSample? sample)
    {
        // 无滚轮的一拍用标称节拍当作窗口长度，与 Decay() 同一处理
        var (notches, elapsed) = sample is { } moved
            ? (moved.WheelNotches, moved.ElapsedSeconds)
            : (0d, MovementSampleIntervalMs / 1000d);
        var deflected = _wheelDeflection * DecayFor(elapsed) + notches / FullDeflectionWheelNotches;
        // 小于 0.01 直接归零：衰减的尾巴永远到不了 0，轨迹也就永远排不空，静止时会一直重建三条曲线
        _wheelDeflection = Math.Abs(deflected) < 0.01 ? 0 : Math.Clamp(deflected, -1d, 1d);
    }

    /// <summary>
    /// 把当前偏移计入轨迹并重建三条曲线。只在还在动或轨迹未排空时动手：
    /// 手停下来后旧点会继续向左滑出并自动清空，不会留下一条永远贴零的直线。
    /// 时间用 UTC：本地钟被调整时，按“距今多少秒”算出的 x 不会跳变。
    /// </summary>
    private void RecordTrail()
    {
        var now = DateTime.UtcNow;
        if (MouseOffsetX != 0 || MouseOffsetY != 0 || _wheelDeflection != 0)
            _trail.Enqueue((now, MouseOffsetX, MouseOffsetY, _wheelDeflection));
        while (_trail.Count > 0 && (now - _trail.Peek().At).TotalSeconds > TrailWindowSeconds)
            _trail.Dequeue();
        if (_trail.Count == 0)
        {
            if (MouseTrailXPoints.Count > 0)
            {
                MouseTrailXPoints = new PointCollection();
                MouseTrailYPoints = new PointCollection();
                MouseTrailWheelPoints = new PointCollection();
            }
            return;
        }
        var mid = TrailHeight / 2;
        // 留出 2 px 边距：连续两次扫动把偏移顶到 ±1 时，曲线不会被顶出画布外
        var amplitude = mid - 2;
        var xs = new PointCollection(_trail.Count);
        var ys = new PointCollection(_trail.Count);
        var wheels = new PointCollection(_trail.Count);
        foreach (var (at, x, y, wheel) in _trail)
        {
            var px = TrailWidth - (now - at).TotalSeconds * TrailPixelsPerSecond;
            xs.Add(new Point(px, mid - x * amplitude));
            ys.Add(new Point(px, mid - y * amplitude));
            // 往前滚（远离自己）时格数为正，画在零线上方
            wheels.Add(new Point(px, mid - wheel * amplitude));
        }
        MouseTrailXPoints = xs;
        MouseTrailYPoints = ys;
        MouseTrailWheelPoints = wheels;
    }

    private void FlushMovement()
    {
        if (_pendingFlushPixels <= 0)
            return;
        var pixels = _pendingFlushPixels;
        _pendingFlushPixels = 0;
        _dbService.AddMovementPixels(_movementDay, pixels);
    }

    private void RefreshDistanceText()
    {
        TodayDistanceMeters = DisplayMetrics.PixelsToMeters(_todayMovementPixels);
        TotalDistanceMeters = DisplayMetrics.PixelsToMeters(_totalMovementPixels);
        MouseDistanceText = $"移动 今日 {TodayDistanceMeters:F2} m · 累计 {TotalDistanceMeters:F2} m";
    }

    /// <summary>用 Unicode 减号保持正负号同宽，读数不会左右跳格。</summary>
    private static string Signed(int value) => value >= 0 ? $"+{value}" : $"−{-value}";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hookService.MouseClicked -= OnMouseClicked;
        _movementTimer.Stop();
        _movementTimer.Tick -= OnMovementTick;
        FlushMovement();
        GC.SuppressFinalize(this);
    }
}
