using System;

namespace XAssistant.Services;

/// <summary>
/// 一行的时间账：什么时候开始擦出、什么时候真的没了。效果窗与调度器共用这一份算法。
///
/// 要守住的等式是「<b>屏幕让位的时刻 == 最后一条真的消失的时刻</b>」。上一版不等：
/// 退场动画被接在预算<em>之后</em>（预算 = 淡入 + 持续 + 淡出，擦出又另花一个淡出），
/// 于是调度器以为屏幕空了就把下一条送上屏，<c>Banner</c> 换组时一把摘掉正在擦到一半的上一行，
/// 全屏边框也跟着在正文还没走完时先收——用户看到的正是「动画没播完，border 先关了」。
/// 现在擦出从 <c>Until − 淡出</c> 开始、到 <c>Until</c> 恰好擦完，两边对同一个时刻说话。
/// </summary>
public static class EffectTiming
{
    /// <summary>擦出时长区间：再短就看不清是「扫出去」，再长就拖成另一条消息。</summary>
    public const double MinExitSeconds = 0.2, MaxExitSeconds = 3;

    /// <summary>擦出多久：命令没写淡出（0）就用宿主的默认扫描时长。</summary>
    public static double ExitSeconds(double fadeOut, double fallback) =>
        Math.Clamp(fadeOut <= 0 ? fallback : fadeOut, MinExitSeconds, MaxExitSeconds);

    /// <summary>
    /// 擦出从哪一刻开始。退场住在这一行自己的预算里，所以是 <c>until - exitSeconds</c>；
    /// 唯一的例外是入场被同行错峰挤后了而预算又短（那个时刻落在入场完成之前）：等入场走完再擦，
    /// 宁可比预算多留一会儿，也不把入场动画腰斩。
    /// </summary>
    public static TimeSpan ExitStart(TimeSpan entryDone, TimeSpan until, double exitSeconds)
    {
        var byBudget = until - TimeSpan.FromSeconds(exitSeconds);
        return entryDone > byBudget ? entryDone : byBudget;
    }
}
