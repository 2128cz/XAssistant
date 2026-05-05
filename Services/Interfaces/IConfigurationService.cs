namespace XAssistant.Services.Interfaces;

/// <summary>
/// 应用程序配置服务，管理各种开关类设置。
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// 获取是否开机自启动的配置。
    /// </summary>
    bool GetAutoStart();

    /// <summary>
    /// 设置是否开机自启动。
    /// </summary>
    void SetAutoStart(bool enabled);

    /// <summary>
    /// 获取上次程序退出时是否处于录制状态的配置。
    /// </summary>
    bool GetRecordingAutoStart();

    /// <summary>
    /// 设置录制状态（用于下次启动自动恢复）。
    /// </summary>
    void SetRecordingAutoStart(bool isRecording);

    bool GetKeyRecordingAutoStart();
    void SetKeyRecordingAutoStart(bool autoStart);
}
