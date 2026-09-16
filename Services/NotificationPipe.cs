using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace XAssistant.Services;

/// <summary>
/// 实例间的命令转发：无头 xa 实例「放完就退」，养不住顶部持久消息栈这类要一直钉着的窗，
/// 所以常驻主程序在时把整条命令原样交给它执行——效果窗与消息栈都归主程序一份，
/// 多个 xa 进程也不会抢同一块屏幕。协议一行式：一次连接 = 一行 = 一条完整效果命令
/// （与 <c>xa</c> 后面那段一字不差，收端走同一个 <see cref="EffectCommand.TryParse"/>）。
/// </summary>
public static class NotificationPipe
{
    /// <summary>管道名（\\.\pipe\ 下用户桌面唯一）。换名要连安装脚本一起换。</summary>
    public const string PipeName = "XAssistant.Notify";

    /// <summary>连接超时：主程序没跑时快速失败，别让无头实例白等。</summary>
    private const int ConnectTimeoutMs = 250;

    /// <summary>
    /// 试着把一行命令交给常驻实例。true = 已交接（本轮不该自己开窗）；
    /// false = 没人在听（超时/管道不存在等，一律当主程序不在，本地自己放）。
    /// </summary>
    public static bool TryForward(string line)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(ConnectTimeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(line);
            writer.Flush();
            return true;
        }
        catch (Exception)
        {
            // 超时、管道不存在、共享冲突——调用方只需要一个答案：没人接手
            return false;
        }
    }
}

/// <summary>
/// 管道服务端：常驻主程序启动它，每次连接读一行并触发 <see cref="LineReceived"/>。
/// 回调在后台线程上触发，订阅方自己调度回 UI 线程（App 的接线就是这么做的）。
/// </summary>
public sealed class NotificationPipeServer : IDisposable
{
    /// <summary>收到一整行效果命令（不含行尾换行）。</summary>
    public event Action<string>? LineReceived;

    private NamedPipeServerStream? _server;
    private Thread? _loop;
    private volatile bool _stopping;

    /// <summary>
    /// 开始监听。false = 管道已被别的实例占着（或系统拒绝）——只记日志即可，
    /// 一个桌面同时只需要一个服务端。
    /// </summary>
    public bool TryStart()
    {
        try
        {
            _server = new NamedPipeServerStream(
                NotificationPipe.PipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.None);
            _loop = new Thread(ServeLoop) { IsBackground = true, Name = "xa-notify" };
            _loop.Start();
            return true;
        }
        catch (IOException) { return false; }   // 名字被占：已有实例在听
        catch (UnauthorizedAccessException) { return false; }
    }

    private void ServeLoop()
    {
        NamedPipeServerStream server = _server!;
        using var reader = new StreamReader(server, new UTF8Encoding(false));
        while (!_stopping)
        {
            try { server.WaitForConnection(); }
            // Dispose 会打断阻塞的 WaitForConnection：退出走查，不是错误
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }

            string? line = null;
            try
            {
                line = reader.ReadLine();
                server.Disconnect();       // 断开后同一实例继续等下一次连接
            }
            catch (IOException) { continue; }              // 客户端半路跑了：这轮作废，接着等
            catch (ObjectDisposedException) { break; }     // Dispose 撞上了 ReadLine：退出走查
            catch (InvalidOperationException) { break; }   // 连接已断/状态不对，同上
            if (!string.IsNullOrWhiteSpace(line)) LineReceived?.Invoke(line);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _server?.Dispose(); } catch (IOException) { /* 打断用的，异常本身就是要的副作用 */ }
        _loop?.Join(TimeSpan.FromMilliseconds(500));
        _server = null;
        _loop = null;
    }
}
