namespace XAssistant.Models;

/// <summary>
/// 一段窗口内累积的鼠标移动汇总。净位移（DeltaX/DeltaY）回答“往哪儿偏”，
/// 路径长度（PathPixels）回答“磨了多少米”——来回抖动时前者接近 0、后者持续增长。
/// </summary>
/// <param name="DeltaX">窗口内水平净位移，屏幕像素，向右为正。</param>
/// <param name="DeltaY">窗口内垂直净位移，屏幕像素，向下为正。</param>
/// <param name="PathPixels">窗口内路径长度，即各次位移模长之和，屏幕像素。</param>
/// <param name="ElapsedSeconds">该窗口实际覆盖的时长，用于换算 px/s。</param>
/// <param name="ScreenX">采样结束时指针的屏幕横坐标。</param>
/// <param name="ScreenY">采样结束时指针的屏幕纵坐标。</param>
/// <param name="WheelNotches">窗口内滚轮格数，向上为正；分数滚轮会给出非整数。</param>
public readonly record struct MouseMovementSample(
    int DeltaX,
    int DeltaY,
    double PathPixels,
    double ElapsedSeconds,
    int ScreenX,
    int ScreenY,
    double WheelNotches
);
