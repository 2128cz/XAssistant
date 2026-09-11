using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

public partial class ClickCounterViewModel : ViewModelBase, IDisposable
{
    /// <summary>移动取样节拍。10 Hz 足够让指示图标看着连续，也把文本分配限制在每秒十次。</summary>
    private const int MovementSampleIntervalMs = 100;

    /// <summary>一个取样窗口内多少像素的净位移算作把指示图标“打满”；按 10 Hz 约合 2200 px/s。</summary>
    private const double FullDeflectionPixels = 220;

    /// <summary>每个节拍把上一次的偏移按该比例保留，停下后约 3 个节拍（300 ms）回到中心。</summary>
    private const double DeflectionDecay = 0.55;

    /// <summary>速率的指数平滑权重：单个窗口的突发位移不会把读数抽成毛刺。</summary>
    private const double SpeedSmoothing = 0.3;

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
    private long _clickSequence;
    private bool _disposed;

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
    /// 静止时不分配任何字符串，只有指示图标还在向中心回落才继续更新。
    /// </summary>
    private void OnMovementTick(object? sender, EventArgs e)
    {
        var sample = _hookService.ReadMovementSample();
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
        }
        else if (MouseOffsetX != 0 || MouseOffsetY != 0 || _smoothedPixelsPerSecond > 0)
        {
            Relax();
        }

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
        MouseOffsetX = Math.Clamp(
            MouseOffsetX * DeflectionDecay + sample.DeltaX / FullDeflectionPixels, -1d, 1d);
        MouseOffsetY = Math.Clamp(
            MouseOffsetY * DeflectionDecay + sample.DeltaY / FullDeflectionPixels, -1d, 1d);
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

    private static double Decay(double value) => Math.Abs(value) < 0.01 ? 0 : value * DeflectionDecay;

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
        _movementTimer.Stop();
        _movementTimer.Tick -= OnMovementTick;
        FlushMovement();
        GC.SuppressFinalize(this);
    }
}
