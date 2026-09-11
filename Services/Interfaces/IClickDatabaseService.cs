using System.Collections.Generic;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 负责点击记录的持久化存储（SQLite 等）。
/// </summary>
public interface IClickDatabaseService
{
    /// <summary>
    /// 保存一条点击记录。
    /// </summary>
    /// <param name="record">包含按键类型和时间的记录</param>
    void SaveClick(MouseClickRecord record);

    /// <summary>
    /// 获取各鼠标按键的累计点击次数。
    /// </summary>
    /// <returns>键为按键名称（"Left","Middle","Right"），值为累计次数</returns>
    Dictionary<string, int> GetClickCounts();

    Dictionary<string, int> GetClickCountsByDate(DateTime date);

    /// <summary>
    /// 把一段鼠标移动的路径像素累加到指定日期。移动事件每秒上千条，
    /// 因此不逐条存记录，只按天维护一个累计值。
    /// </summary>
    void AddMovementPixels(DateTime date, double pixels);

    /// <summary>指定日期已累计的移动路径像素。</summary>
    double GetMovementPixels(DateTime date);

    /// <summary>所有日期合计的移动路径像素。</summary>
    double GetTotalMovementPixels();
}
