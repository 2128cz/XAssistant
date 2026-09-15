using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using XAssistant.Services.Keywords;
using XAssistant.Views;

namespace XAssistant.Services;

/// <summary>
/// 无头模式：<c>XAssistant.exe --fx warn 3 "AI Computer Use"</c>、<c>--fx confetti</c>、<c>--fx off</c>。
///
/// 存在的理由是让别的进程（脚本、快捷键、MCP 垫片、CI 通知）也能触发同一套效果，
/// 而不必再装一个客户端：同一个 exe 既能当界面也能当命令。这条路只开效果层——
/// 不建容器、不装钩子、不开主窗、不碰数据库，放完就退。
/// </summary>
public static class EffectCli
{
    /// <summary>无头效果的开关名。写成一个而不是多个别名：脚本里抄一次就记住了。</summary>
    public const string Switch = "--fx";

    /// <summary>撒花默认撒几颗，以及撒完留多久（粒子寿命最长 3.8 s）。</summary>
    private const int ConfettiCount = 12;
    private const double ConfettiSeconds = 4.6;

    /// <summary>收尾留一点余量，别让进程比动画先退。</summary>
    private const double GraceSeconds = 0.6;

    /// <summary>
    /// 这轮启动是不是来放效果的。是则执行并返回 true，调用方（App.OnStartup）就此结束启动流程。
    /// 参数按空格重新拼回一条命令，所以 <c>--fx warn 3 AI Computer Use</c> 与 <c>--fx "warn 3 AI Computer Use"</c> 等价。
    /// </summary>
    public static bool TryHandle(string[] args, System.Windows.Application app)
    {
        int at = Array.IndexOf(args, Switch);
        if (at < 0) return false;
        string line = string.Join(" ", args.Skip(at + 1)).Trim();

        double alive = Apply(app, line);
        // 兜底的自杀计时器：正常情况下效果窗自己关完，OnLastWindowClose 就会把进程带走；
        // 但 "off" 或未知命令可能一个窗口都没开过，那时只能靠这里退。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(alive + GraceSeconds) };
        timer.Tick += (_, _) => { timer.Stop(); app.Shutdown(); };
        timer.Start();
        return true;
    }

    /// <summary>执行一条命令，返回这个效果还要占多久（秒）。未知命令返回 0，调用方立刻退。</summary>
    private static double Apply(System.Windows.Application app, string line)
    {
        double seconds = 0;
        // 效果层是 WPF 窗口，只能在 UI 线程碰；OnStartup 本来就在 UI 线程，这里只是把意图写清楚
        app.Dispatcher.Invoke(() =>
        {
            if (line.Length == 0 || SlashParser.IsHide(line)) { EffectsWindow.HideBanner(); seconds = 0; return; }
            if (IsConfetti(line)) { EffectsWindow.Confetti(ConfettiCount); seconds = ConfettiSeconds; return; }
            if (SlashParser.TryParse(line, out SlashCommand command))
            {
                EffectsWindow.ShowBanner(command.Text, SlashParser.BrushOf(command.Tone), command.Seconds, command.Blinks);
                seconds = command.Seconds;
                return;
            }
            // 词表外的第一条词：与打字时同一套判据——整条跳过，不猜、不报错、不弹东西
            seconds = 0;
        });
        return seconds;
    }

    private static bool IsConfetti(string line) =>
        line.Equals("confetti", StringComparison.OrdinalIgnoreCase)
        || line.Equals("celebrate", StringComparison.OrdinalIgnoreCase)
        || line.Equals("花", StringComparison.Ordinal);
}
