using System;
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Brush / Brushes 会跟 WPF 的撞名
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using XAssistant.Views;

namespace XAssistant.Services;

/// <summary>
/// 「执行一条命令行」的唯一语义：解析 → 收起 / 撒花 / 上屏，带文字的消息同时钉进顶部持久栈。
/// 无头实例的本地兜底（<see cref="EffectCli"/>）与常驻实例（App 的管道回调）走的必须是同一个方法——
/// 两条路径行为不许分叉。须在 UI 线程调用（跨线程时调用方先包一层 Dispatcher）。
/// </summary>
public static class EffectDispatch
{
    /// <summary>撒花默认撒几颗，以及撒完留多久（粒子寿命最长 3.8 s）。</summary>
    public const int ConfettiCount = 12;
    public const double ConfettiSeconds = 4.6;

    /// <summary>执行一行命令，返回这个效果还要占屏多久（秒）。收起与未知命令都是 0。</summary>
    public static double OnLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return 0;
        // 词表外的第一条词：与打字时同一套判据——整条跳过，不猜、不报错、不弹东西
        if (!EffectCommand.TryParse(line, out EffectCommand command)) return 0;
        if (command.Hide) { EffectsWindow.HideBanner(); return 0; }
        if (command.Confetti) { EffectsWindow.Confetti(ConfettiCount); return ConfettiSeconds; }
        EffectsWindow.ShowCommand(command);
        // 全屏那条大字淡完就没了；同一句话钉进消息栈，事后抬眼就能回看（只有边框没文字的指令不入栈）
        // -from 的平台标记跟着进栏：徽章贴 IDE 图标，多平台聚合也能认出是谁发的
        if (command.Text is { Length: > 0 } text)
            Views.MessageStackWindow.Push(text, EffectCommand.BrushOf(command.Color) ?? Accent(), command.Source);
        return command.FadeIn + command.Hold + command.FadeOut;
    }

    /// <summary>类型词 info 跟着主题强调色走，取不到资源时接一个中性灰兜底。</summary>
    private static Brush Accent() =>
        System.Windows.Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.Gainsboro;
}
