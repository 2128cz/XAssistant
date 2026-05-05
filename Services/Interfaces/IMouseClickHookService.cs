using System;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 全局鼠标点击钩子服务。
/// </summary>
public interface IMouseClickHookService
{
    /// <summary>
    /// 启动鼠标钩子，开始捕获点击事件。
    /// </summary>
    void Start();

    /// <summary>
    /// 停止鼠标钩子，停止捕获点击事件。
    /// </summary>
    void Stop();

    /// <summary>
    /// 当检测到鼠标点击时触发。
    /// 参数为鼠标按键字符串："Left"、"Middle"、"Right" 等。
    /// </summary>
    event Action<string> MouseClicked;
}
