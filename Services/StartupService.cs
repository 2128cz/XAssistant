using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public class StartupService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _appName;
    private readonly string _exePath;

    public StartupService(string appName)
    {
        _appName = appName;
        // 如果获取不到路径，直接抛异常，因为后续功能没法用
        _exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前进程的可执行文件路径。");
    }

    public void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            // 无法打开注册表键，忽略或记录日志
            return;
        }

        if (enabled)
            key.SetValue(_appName, _exePath);
        else
            key.DeleteValue(_appName, throwOnMissingValue: false);
    }

    public bool IsStartWithWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(_appName) != null;
    }
}