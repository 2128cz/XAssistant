using System.Runtime.InteropServices;

namespace XAssistant.Services;

/// <summary>
/// 屏幕像素与物理尺寸之间的换算。
/// “鼠标移动了多少米”只能由指针位移乘以显示器点距得到：优先用 GDI 读到的
/// primary 显示器物理宽度（毫米）除以其像素宽度；读不到时退回 96 DPI 假设。
/// </summary>
/// <remarks>
/// 两点必须知道的近似：
/// 1）多显示器下统一采用 primary 的点距，跨屏移动时会有偏差；
/// 2）指针位移是经过“提高指针精确度”加速后的结果，因此快速挥动会低估手的真实行程。
/// </remarks>
public static class DisplayMetrics
{
    /// <summary>取不到显示器物理尺寸时使用的假设值：96 DPI 下的每像素米数。</summary>
    private const double FallbackMetersPerPixel = 0.0254 / 96;

    private const int HorzSize = 4; // 屏幕物理宽度，毫米
    private const int HorzResolution = 8; // 屏幕宽度，像素

    /// <summary>每屏幕像素对应的物理米数，进程启动时读取一次（显示器配置运行中一般不会变）。</summary>
    public static double MetersPerPixel { get; } = ReadMetersPerPixel();

    /// <summary>把指针走过的屏幕像素折算成物理距离，单位米。</summary>
    public static double PixelsToMeters(double pixels) => pixels * MetersPerPixel;

    private static double ReadMetersPerPixel()
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return FallbackMetersPerPixel;
        try
        {
            int millimeters = GetDeviceCaps(hdc, HorzSize);
            int pixels = GetDeviceCaps(hdc, HorzResolution);
            if (millimeters <= 0 || pixels <= 0)
                return FallbackMetersPerPixel;
            double value = millimeters / 1000d / pixels;
            // 虚拟机与部分驱动会报出荒谬值；只接受 0.025µm/px ~ 2mm/px 之间的点距
            return value is > 0.000025 and < 0.002 ? value : FallbackMetersPerPixel;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
}
