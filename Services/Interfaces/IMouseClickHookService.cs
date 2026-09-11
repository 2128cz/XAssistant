using System;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 全局鼠标钩子服务：捕获点击，并累积指针移动量。
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

    /// <summary>
    /// 当检测到鼠标点击时触发（带屏幕坐标）。
    /// 参数：按键名称（"Left"/"Middle"/"Right"）、屏幕 X、屏幕 Y。
    /// </summary>
    event Action<string, int, int>? MouseClickedAt;

    /// <summary>
    /// 取走自上次调用以来累积的移动量；期间没有任何移动或滚轮事件时返回 null。
    /// 由消费方按自己的刷新节拍拉取，钩子回调不主动推送，避免高频事件压垮订阅者。
    /// </summary>
    MouseMovementSample? ReadMovementSample();

    /// <summary>
    /// 丢弃尚未取样的移动量并重新锚定指针位置。
    /// 开始/停止记录时调用，否则跨越钩子空档会算出一段假位移。
    /// </summary>
    void ResetMovementTracking();
}
