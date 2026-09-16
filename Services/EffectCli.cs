using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace XAssistant.Services;

/// <summary>
/// 无头模式，三种写法都认：
///   <c>XAssistant.exe -s info 5 1 1 -border on 50 30 1 -lable on 24 "AI 接管中"</c>（新语法，见 <see cref="EffectCommand"/>）
///   <c>XAssistant.exe --fx warn 3 "AI Computer Use"</c>（旧语法，保留兼容 MCP 垫片与老脚本）
///   <c>XAssistant.exe confetti</c>／<c>XAssistant.exe off</c>（撒花／收起）
///
/// 存在的理由是让别的进程（cmd、脚本、快捷键、MCP 垫片、CI 通知）也能触发同一套效果：
/// 同一个 exe 既能当界面也能当命令。这条路只开效果层——不建容器、不装钩子、不开主窗、
/// 不碰数据库，放完就退。想省掉写全路径的麻烦就用 <c>register-xa.ps1</c> 把 <c>xa</c> 注册进 PATH。
/// </summary>
public static class EffectCli
{
    /// <summary>无头效果的开关名。写成一个而不是多个别名：脚本里抄一次就记住了。</summary>
    public const string Switch = "--fx";

    /// <summary>兜底的自杀计时器留的余量：别让进程比动画先退。撒花的颗数与占屏时长在
    /// <see cref="EffectDispatch"/> 里（两条执行路径共用一份语义）。</summary>
    private const double GraceSeconds = 0.6;

    /// <summary>
    /// 这轮启动是不是来放效果的：返回入口词在参数里的位置，不是则 -1。
    /// 认三种入口：旧开关 <c>--fx</c>、新语法的段开关（<c>-s</c> / <c>-border</c> / <c>-lable</c> 打头）、
    /// 裸词 <c>confetti</c> / <c>off</c>。第一个词就得对上——别的启动参数不会被误吃。
    /// </summary>
    private static int FindEntry(string[] args)
    {
        if (args.Length == 0) return -1;
        string first = args[0];
        if (first.Equals(Switch, StringComparison.OrdinalIgnoreCase)) return 0;
        if (EffectCommand.IsSectionSwitch(first)) return 0;
        if (EffectCommand.IsConfettiWord(first)) return 0;
        if (args.Length == 1 && EffectCommand.IsHideWord(first)) return 0;
        // --fx 不在第一位（例如带启动参数后面又跟了效果命令）也认旧入口
        return Array.IndexOf(args, Switch);
    }

    /// <summary>
    /// 这轮启动是不是来放效果的。是则执行并返回 true，调用方（App.OnStartup）就此结束启动流程。
    /// 参数从入口词起按空格重新拼回一条命令，所以 <c>-s info 5 1 1</c> 与 <c>-s "info 5 1 1"</c> 等价。
    /// 常驻主程序在听管道时整条命令转发给它（效果与消息栈归一份，见 <see cref="NotificationPipe"/>）；
    /// 没人接手才本地自己放。
    /// </summary>
    public static bool TryHandle(string[] args, System.Windows.Application app)
    {
        int at = FindEntry(args);
        if (at < 0) return false;
        string line = string.Join(" ", args.Skip(at)).Trim();

        double alive = 0;
        if (!NotificationPipe.TryForward(line))
            // 效果层是 WPF 窗口，只能在 UI 线程碰；OnStartup 本来就在 UI 线程，这里只是把意图写清楚
            app.Dispatcher.Invoke(() => alive = EffectDispatch.OnLine(line));
        // 兜底的自杀计时器：正常情况下效果窗自己关完就会把进程带走；
        // 但转发成功、"off" 或未知命令可能一个窗口都没开过，那时只能靠这里退。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(alive + GraceSeconds) };
        timer.Tick += (_, _) => { timer.Stop(); app.Shutdown(); };
        timer.Start();
        return true;
    }
}
