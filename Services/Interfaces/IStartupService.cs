namespace XAssistant.Services.Interfaces;

/// <summary>
/// Windows 开机自启动服务，通过注册表控制。
/// </summary>
public interface IStartupService
{
    /// <summary>
    /// 检查当前应用是否已设为开机自启动。
    /// </summary>
    bool IsStartWithWindowsEnabled();

    /// <summary>
    /// 启用或禁用开机自启动。
    /// </summary>
    /// <param name="enable">true 为启用，false 为取消</param>
    void SetAutoStart(bool enable);
}
