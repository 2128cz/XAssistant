using System.Runtime.InteropServices;
using System.Text;

namespace XAssistant.Services;

/// <summary>
/// 进程信息查询的 Win32 直连实现，用于取代 <see cref="System.Diagnostics.Process"/> 的高危成员。
/// 原因：<c>Process.GetProcessById</c> 对已退出进程抛 <c>ArgumentException</c>、
/// <c>Process.MainModule</c> 对无权限或位数不匹配的进程抛异常。
/// 这些调用点位于秒级轮询与 WMI 事件回调里，异常虽被 catch 吞掉，
/// 但每一条第一机会异常都会被调试器记录，长时间 F5 会耗尽 .NET 调试服务的内存。
/// 本类全部以返回值表达失败，不产生异常。
/// </summary>
internal static class ProcessInfoHelper
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x0010_0000;
    private const uint WAIT_TIMEOUT = 0x0000_0102;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        StringBuilder lpExeName,
        ref uint lpdwSize
    );

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    /// <summary>
    /// 进程是否仍然存活。打不开句柄（已退出或无权访问）一律按不可读取处理，返回 false。
    /// </summary>
    public static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;

        IntPtr handle = OpenProcess(SYNCHRONIZE, false, processId);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            return WaitForSingleObject(handle, 0) == WAIT_TIMEOUT;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 尝试取进程主模块完整路径。权限不足、位数不匹配、进程已退出时返回 false，不抛异常。
    /// </summary>
    public static bool TryGetProcessPath(int processId, out string? path)
    {
        path = null;
        if (processId <= 0)
            return false;

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size == 0)
                return false;

            path = buffer.ToString(0, (int)size);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
