using System;
using XAssistant.Views;

namespace XAssistant.Services;

/// <summary>
/// 「执行一行命令行」的唯一语义：解析 → 杀除 / 收起 / 交进播放队列。
/// 无头实例的本地兜底（<see cref="EffectCli"/>）与常驻实例（App 的管道回调）走的必须是同一个方法——
/// 两条路径行为不许分叉。必须在 UI 线程调用（跨线程时调用方先包一层 Dispatcher）。
///
/// 上屏与顶部栈都不在这里做了：它们归 <see cref="EffectQueue"/> 那份调度器，一条播完才播下一条。
/// </summary>
public static class EffectDispatch
{
    /// <summary>撒花默认撒几颗，以及撒完留多久（粒子寿命最长 3.8 s）。</summary>
    public const int ConfettiCount = 12;
    public const double ConfettiSeconds = 4.6;

    /// <summary>
    /// 执行一行命令，返回这一轮要占屏多久（秒），无头实例拿它定自己的退出时间。
    /// 收起、杀除与未知命令都是 0。
    /// </summary>
    public static double OnLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return 0;
        // 词表外的第一条词：与打字时同一套判据——整条跳过，不猜、不报错、不弹东西
        if (!EffectCommand.TryParse(line, out EffectCommand command)) return 0;
        if (command.Kill) { EffectQueue.Shared.Kill(command); return 0; }
        if (command.Hide) { EffectQueue.Shared.SkipCurrent(); return 0; }
        if (command.Confetti) { EffectsWindow.Confetti(ConfettiCount); return ConfettiSeconds; }
        EffectQueue.Shared.Submit(command);
        return command.ScreenSeconds;
    }
}
