using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XAssistant.Services.Modules;

namespace XAssistant.Services;

/// <summary>hooks 探测的结果。<see cref="AgentHooksState.Indeterminate"/> 是「看不出来」而不是「没装」——
/// 文件读不动、平台根本没这个配置、或探测件的话读不懂都归这里，卡片不能把它渲染成一个绿色的「未部署」。</summary>
public enum AgentHooksState { Installed, NotInstalled, Indeterminate, NotInstallable }

/// <summary>
/// 一个可装平台：<b>模块侧的来源词</b> → <see cref="Key"/>（安装器的 <c>-Platform</c> 取值）+
/// 判定「装了没有」要看的那个配置文件 + 两个如实的说明。
/// </summary>
/// <param name="Source">监视模块的 <c>IdeSource</c> / 命令行的 <c>-from</c>，卡片按它来问。</param>
/// <param name="Key">安装器的平台键。今天与 <see cref="Source"/> 同名，分成两个字段是因为
/// 卡片认领的来源词与 CLI 的取值各自都可能改，绑死一处早晚对不上。</param>
/// <param name="Label">给人看的平台名（取自安装器的表，别在这里另起一套称呼）。</param>
/// <param name="ConfigPath">「装了」的判据文件：内容里出现 <c>agent-status.ps1</c> 就算已部署。</param>
/// <param name="Installable">能不能装：<c>kind = unknown</c>（Cursor / Windsurf / Codex）的安装器拒写，
/// 这里也别说得好像能装。</param>
/// <param name="NeedsRestart">生效要不要重启 IDE：settings-hooks 那几家<b>不热重载</b>（安装器明说），
/// DSH 的 Cordis patch 热重载。</param>
/// <param name="Note">验证状态与形态说明，原样抄自安装器，卡片直接晒。</param>
public sealed record AgentHooksPlatform(
    string Source, string Key, string Label, string ConfigPath,
    bool Installable, bool NeedsRestart, string Note);

/// <summary>一次探测：平台表里的那一行 + 状态 + 给人看的两句话。</summary>
/// <param name="Platform">查到的平台行（来源词不认识时为 null）。</param>
/// <param name="State">装没装。</param>
/// <param name="Detail">共用探测件 <see cref="IdeProbe.Hooks"/> 的原文——卡片想省事就直接显示它。</param>
/// <param name="Hint">按平台形态改写过的人话（DSH 不写「重启 IDE 生效」那种瞎话）。</param>
public sealed record AgentHooksStatus(
    AgentHooksPlatform? Platform, AgentHooksState State, string Detail, string Hint)
{
    /// <summary>现在就是「已部署」。</summary>
    public bool IsInstalled => State == AgentHooksState.Installed;

    /// <summary>这一行该不该给「安装」按钮：不认得的来源词与未校准的平台都不给。
    /// 装了还按已部署上色，重复安装是幂等的（安装器第一步就是摘旧条目）。</summary>
    public bool CanInstall => Platform is { Installable: true };
}

/// <summary>
/// 一次安装/拆卸的结果。<b>失败也是结果，不是异常</b>：装不上顶多是这张卡不干活，不能把宿主带崩。
/// </summary>
/// <param name="Success">安装器退出码 0 且没被我们半路拦下。</param>
/// <param name="ExitCode">子进程退出码；没等到（超时或起不来）时为 -1。</param>
/// <param name="Lines">安装器自己打的行——<b>这才是给用户看的解释</b>（它连「重启才生效」都写在这儿）。</param>
/// <param name="Error">stderr 的首行，只在 <see cref="Success"/> 为 false 时值得一眼。</param>
public sealed record AgentHooksResult(
    bool Success, int ExitCode, string Source, IReadOnlyList<string> Lines, string? Error)
{
    /// <summary>整段输出压成一行，给只有一行的卡片读数用。</summary>
    public string Text => string.Join(" ｜ ", Lines.Select(l => l.Trim()).Where(l => l.Length > 0));
}

/// <summary>
/// 监视模块卡片背后的那只手：把 <c>mcp/agent-hooks/install-agent-hooks.ps1</c> 当 CLI 用，
/// 装／拆／查各家 AI 编码代理的 hooks。
///
/// 三条设计上的老实话：
///
/// 1. <b>安装器是唯一契约，这里不复制它的逻辑</b>。平台表、配置文件位置、形态（settings-hooks /
///    hooks-file / Cordis patch）都照抄它当前的取值；改它那边要同步这张表——漂移的代价是卡片说「已部署」
///    而屏幕上什么都没有，所以每个字段的来源都写在注释里，便于对照。
/// 2. <b>写配置文件是「改用户 IDE 全局状态」，只由用户点卡片上的按钮触发</b>（决策同 <c>deploy.ps1</c>：
///    部署不自动装 hooks）。本类不主动写、不重试、也不绕开安装器自己拼 JSON。
/// 3. <b>探测复用 <see cref="IdeProbe.Hooks"/>，不重抄「文件里有没有 agent-status.ps1」</b>。
///    它硬写的「（重启 IDE 生效）」对 DSH 是错的（patch 热重载），所以对外的话术由这张表按形态改写，
///    <see cref="AgentHooksStatus.Detail"/> 里保留它原文；哪天它能接受「要不要重启」当参数，
///    这层改写就该删掉。
///
/// <see cref="Install"/> 与 <see cref="Remove"/> 都是<b>同步</b>调用（拉 powershell 子进程，冷启动约 1 秒）。
/// 类自己不启动线程、不排 ThreadPool：卡片若在激活路径或 tick 里要点它，请放到后台去跑，别堵 UI 线程。
/// </summary>
public static class AgentHooksInstaller
{
    /// <summary>安装器脚本名：找它、认它都按这个名字。</summary>
    public const string ScriptFileName = "install-agent-hooks.ps1";

    /// <summary>脚本位置的显式覆盖（自测/便携装），与 <c>agent-status.ps1</c> 认 <c>XASSISTANT_XA</c> 同一套路。</summary>
    public const string ScriptEnvVar = "XASSISTANT_AGENT_HOOKS_SCRIPT";

    /// <summary>等到这个毫秒数就放弃取退出码。装到一半<b>不杀进程</b>——它可能正写 IDE 的配置。</summary>
    private const int WaitMilliseconds = 60_000;

    /// <summary>DSH 的默认 profile，与安装器的 <c>-Profile</c> 默认值一致（改了要两边同步）。</summary>
    public const string DefaultProfile = "web";

    private const string Marker = "agent-status.ps1";

    /// <summary>
    /// 平台表（照 <c>install-agent-hooks.ps1</c> 的 <c>$Platforms</c> + <c>$DefaultRoots</c>）。
    /// 每次现算：<c>DSH</c> 的根目录读环境变量 <c>DSH_HOME</c>，环境变量是会变的。
    /// </summary>
    public static IReadOnlyList<AgentHooksPlatform> Platforms() =>
    [
        new("qoder", "qoder", "Qoder CN（桌面版 / IDE）",
            Path.Combine(Home(), ".qoder-cn", "settings.json"),
            true, true, "已实测（接管遮罩 + 5 事件）；settings.json 的 hooks 节点"),
        new("claude", "claude", "Claude Code",
            Path.Combine(Home(), ".claude", "settings.json"),
            true, true, "协议源头，结构同 Qoder，未本机实测；settings.json 的 hooks 节点"),
        new("trae", "trae", "Trae CN",
            Path.Combine(Home(), ".trae-cn", "hooks.json"),
            true, true, "独立 hooks.json（官方要 version:1，只 3 个事件）；schema 待用 Trae 的 Hooks 面板核对"),
        new("trae-intl", "trae-intl", "Trae（国际版）",
            Path.Combine(Home(), ".trae", "hooks.json"),
            true, true, "同 trae"),
        new("dsh", "dsh", "DeepSeek Harness",
            DshConfigPath(DefaultProfile),
            true, false, "原生 Cordis 事件插件：只维护 cordis.patch.yml 里带标记的一小段"),
        new("cursor", "cursor", "Cursor",
            Path.Combine(Home(), ".cursor", "hooks.json"),
            false, true, "待校准：安装器对它拒写（kind = unknown），得先在它自己的 UI 里配一条确认结构"),
        new("windsurf", "windsurf", "Windsurf（Cascade）",
            Path.Combine(Home(), ".codeium", "windsurf", "hooks.json"),
            false, true, "待校准：安装器对它拒写（kind = unknown）"),
        new("codex", "codex", "Codex CLI",
            Path.Combine(Home(), ".codex", "hooks.json"),
            false, true, "待校准：安装器对它拒写（kind = unknown）"),
    ];

    /// <summary>来源词 → 平台行（忽略大小写；认平台名也认来源词，今天是同一个字）。</summary>
    public static AgentHooksPlatform? Find(string? source) =>
        string.IsNullOrWhiteSpace(source)
            ? null
            : Platforms().FirstOrDefault(p =>
                string.Equals(p.Source, source.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Key, source.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 装了没有：读那个配置文件、问共用探测件。<b>不碰安装器</b>（一次子进程冷启动不值得），
    /// 所以这是卡片每轮 tick 都能问的便宜调用。
    /// </summary>
    /// <param name="source">模块侧来源词。</param>
    /// <param name="root">改道到别的根（自测指临时目录，不碰真机 IDE 配置）；<c>Platform</c> 之外的平台用它等价于安装器的 <c>-Root</c>。</param>
    /// <param name="profile">DSH 的 profile，别的平台忽略。</param>
    public static AgentHooksStatus Status(string? source, string? root = null, string? profile = null)
    {
        var platform = Find(source);
        if (platform is null)
            return new(null, AgentHooksState.Indeterminate, $"平台表里没有「{source}」",
                "卡片认领的来源词要在这张表里才有得装");

        // DSH 的 patch 落在 <DSH_HOME>/profiles/<profile>/，root 指的是 DSH_HOME 而不是 profile 目录
        string path = platform.Key == "dsh"
            ? DshConfigPath(EffectiveProfile(profile), root)
            : root is { Length: > 0 }
                ? Path.Combine(root, Path.GetFileName(platform.ConfigPath))
                : platform.ConfigPath;

        if (!platform.Installable)
            return new(platform, AgentHooksState.NotInstallable, platform.Note,
                "这台机器的安装器不给它写配置（schema 未校准）：先在它的官方 UI 里配一条、" +
                "确认生成的文件与结构，再回来补这张表");

        // 复用共用探测件。它只包了 IOException，权限被拒（IDE 占着文件、目录被 ACL 挡住）会抛出来，
        // 这里兜住——探测失败是「看不出来」，不能变成一张红卡或一次崩溃。
        string probe;
        try { probe = IdeProbe.Hooks(path); }
        catch (Exception ex) { probe = "读取失败：" + ex.Message; }

        var state =
            probe.StartsWith("已部署", StringComparison.Ordinal) ? AgentHooksState.Installed :
            probe.StartsWith("未部署", StringComparison.Ordinal) ? AgentHooksState.NotInstalled :
            AgentHooksState.Indeterminate;

        return new(platform with { ConfigPath = path }, state, probe, HintFor(state, platform, path));
    }

    /// <summary>装。<b>同步</b>：拉一个 powershell 子进程，约 1 秒。调用方（激活路径）请自己放后台，本类不起线程。</summary>
    public static AgentHooksResult Install(
        string? source, string? root = null, string? profile = null, bool dryRun = false)
    {
        var platform = Find(source);
        if (platform is null) return Refused(source ?? "", "平台表里没有这个词");
        if (!platform.Installable)
            return Refused(platform.Source, platform.Note + "（安装器对未校准的平台拒写，-Force 才肯试，这里不代你按）");

        var args = new List<string> { "-Platform", platform.Key };
        if (platform.Key == "dsh") { args.Add("-Profile"); args.Add(EffectiveProfile(profile)); }
        if (root is { Length: > 0 }) { args.Add("-Root"); args.Add(root); }
        if (dryRun) args.Add("-DryRun");
        return Run(platform.Source, args);
    }

    /// <summary>拆（只摘指向 agent-status.ps1 的条目，用户自己的 handler 原样保留）。同 <see cref="Install"/> 一样是同步子进程。</summary>
    /// <remarks>
    /// 未校准的平台在这里<b>照放</b>：安装器的拒写判定刻意把 <c>-Remove</c> 排除在外，
    /// 就是给「用户自己 -Force 装过、现在要收干净」留的后路，拆卸不会猜着写配置。
    /// </remarks>
    public static AgentHooksResult Remove(string? source, string? root = null, string? profile = null)
    {
        var platform = Find(source);
        if (platform is null) return Refused(source ?? "", "平台表里没有这个词");

        var args = new List<string> { "-Platform", platform.Key, "-Remove" };
        if (platform.Key == "dsh") { args.Add("-Profile"); args.Add(EffectiveProfile(profile)); }
        if (root is { Length: > 0 }) { args.Add("-Root"); args.Add(root); }
        return Run(platform.Source, args);
    }

    /// <summary>装成了的话该跟用户说什么——按形态给，别把 DSH 也说成「重启 IDE」。</summary>
    private static string HintFor(AgentHooksState state, AgentHooksPlatform platform, string path) => state switch
    {
        AgentHooksState.Installed => platform.NeedsRestart
            ? $"已部署（{path}）——{platform.Label} 的 hooks 不支持热重载，重启 IDE 才生效"
            : $"已部署（{path}）——DSH 的用户 patch 支持热重载；当前进程没加载就重启一次 dsh web",
        AgentHooksState.NotInstalled => $"没部署（{path}）。点安装只增删指向 {Marker} 的条目，" +
                                        "覆盖前备份 .bak，你自己挂的别的 handler 不动",
        _ => "配置读不动（IDE 正占着、权限不够、或文件是坏的 JSON）：这一行不能算数，" +
             $"手动看一眼 {path}",
    };

    private static AgentHooksResult Refused(string source, string why) =>
        new(false, -1, source, [$"没有调用安装器：{why}"], why);

    /// <summary>
    /// 真正拉子进程。<see cref="ProcessStartInfo.ArgumentList"/> 逐项加，不拼命令字符串：
    /// 根目录带空格、带引号都不会把参数拆坏（本机踩过的「重载错绑」那类坑，一律走显式类型）。
    /// 参数名一律写全——<c>-Root</c> 与 <c>-Remove</c> 共享前缀 <c>-R</c>，缩写的参数名在 PowerShell 里是歧义。
    /// </summary>
    private static AgentHooksResult Run(string source, IReadOnlyList<string> tail)
    {
        string script = ScriptPath() ?? Path.Combine(RepoGuess(), "mcp", "agent-hooks", ScriptFileName);
        if (!File.Exists(script))
            return new(false, -1, source, [$"找不到安装器脚本：{script}"], script);

        // 两条流都得抽，而本类不起线程：交错用异步读，避开「安装器把 stderr 缓冲区写满、
        // 我们卡在 stdout 的同步 ReadToEnd 上」这个经典死锁。对外仍是同步调用——
        // 下面就地等它俩结束，不给调用方 Task。
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = Path.GetDirectoryName(script) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // 给它一个干净的 EOF：子进程绝不该等一个没人敲的键盘
            StandardOutputEncoding = ChildOutputEncoding(),
            StandardErrorEncoding = ChildOutputEncoding(),
        };
        // -NoProfile：不加载用户 profile（里面常有 chdir、模块、提示语，会把输出与退出码搅浑）
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
            psi.ArgumentList.Add(a);
        foreach (string a in tail) psi.ArgumentList.Add(a);

        var lines = new List<string>();
        try
        {
            using var proc = new Process { StartInfo = psi };
            try
            {
                proc.Start();
            }
            catch (Exception ex)   // 起不来（powershell 被删、执行策略被 GPO 锁死）也是结果，不是异常
            {
                return new(false, -1, source, [$"启动安装器失败：{ex.Message}"], ex.Message);
            }

            Task<string> stdoutRead = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderrRead = proc.StandardError.ReadToEndAsync();
            proc.StandardInput.Close();

            bool exited = proc.WaitForExit(WaitMilliseconds);
            int code = exited ? proc.ExitCode : -1;
            string stdout = Await(stdoutRead), stderr = Await(stderrRead);

            if (!exited)
            {
                // 不杀进程：它可能正写配置写到一半，砍下去才是真把 IDE 的配置留在道上
                lines.Add($"等了 {WaitMilliseconds / 1000} 秒没等到退出码——没有杀它（可能正在写配置），" +
                          "这轮算失败，去配置文件里自己看一眼再决定要不要重跑");
            }
            lines.AddRange(stdout.Split('\n'));

            // 失败的解释常常只在 stderr（未校准平台那条就是 throw 出来的），不能只信 stdout
            string? error = NonEmptyLines(stderr).FirstOrDefault();
            if (error is not null) lines.AddRange(stderr.Split('\n'));

            return new(exited && code == 0, code, source, Clean(lines), error);
        }
        catch (Exception ex)
        {
            // 这个类对外不抛：安装失败是卡片上的一行字
            return new(false, -1, source, [$"安装器调用异常：{ex.Message}"], ex.Message);
        }
    }

    /// <summary>等一条流读完：超时或读失败都当空串——拿不到输出不影响「退出码才是判据」。</summary>
    private static string Await(Task<string> read)
    {
        try { return read.Wait(WaitMilliseconds) ? read.Result : ""; }
        catch (Exception) { return ""; }
    }

    private static IEnumerable<string> NonEmptyLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>去掉空行与行尾 \r，只留给人看的行。</summary>
    private static IReadOnlyList<string> Clean(IEnumerable<string> lines) =>
        lines.Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// 子进程 stdout 的解码：<b>这台机器上 powershell 写的是 cp936</b>（实测把安装器的输出直接落盘，
    /// 按 cp936 解出 0 个替换符、按 UTF-8 解出 64 个 U+FFFD——错解码会「看见」根本不存在的报错）。
    /// 所以按系统 ANSI 码页取，取不到才退回 UTF-8。
    /// .NET 8 里码页表要先注册一次 <see cref="CodePagesEncodingProvider"/>，不注册就抛 <see cref="ArgumentException"/>。
    /// </summary>
    private static Encoding ChildOutputEncoding()
    {
        if (_childEncoding is not null) return _childEncoding;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _childEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception)
        {
            // 码页拿不到（非中文机器上 ANSI 是 1252、或码页表被裁掉）就按 UTF-8 读，
            // 输出顶多花屏，调用仍然成功——安装器的退出码才是判据
            _childEncoding = Encoding.UTF8;
        }
        return _childEncoding;
    }

    private static Encoding? _childEncoding;

    /// <summary>DSH 的 patch 路径：<c>DSH_HOME</c>（没有则 <c>~/.dsh</c>）/ profiles / &lt;profile&gt; / cordis.patch.yml。</summary>
    private static string DshConfigPath(string profile, string? root = null) =>
        Path.Combine(root is { Length: > 0 } ? root : DshHome(), "profiles", profile, "cordis.patch.yml");

    private static string EffectiveProfile(string? profile) =>
        string.IsNullOrWhiteSpace(profile) ? DefaultProfile : profile.Trim();

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DshHome()
    {
        string? env = Environment.GetEnvironmentVariable("DSH_HOME");
        return string.IsNullOrWhiteSpace(env) ? Path.Combine(Home(), ".dsh") : env;
    }

    /// <summary>
    /// 安装器脚本的位置：环境变量覆盖 → 输出目录旁的 <c>mcp\agent-hooks</c> → 从输出目录逐级上溯
    /// 找仓库里的 <c>mcp\agent-hooks</c>（<c>bin\Debug\net8.0-windows</c> 上三级就是仓库根，
    /// 与 <c>agent-status.ps1</c> 用 <c>..\..\bin\Debug\...</c> 找回 xa 是同一套路）。找不到返回 null，
    /// 调用处会把「猜的位置」写进结果里给人看。
    /// </summary>
    public static string? ScriptPath()
    {
        string? env = Environment.GetEnvironmentVariable(ScriptEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "mcp", "agent-hooks", ScriptFileName);
            if (File.Exists(candidate)) return candidate;
            if (dir.Parent is null) break;
        }
        return null;
    }

    /// <summary>找不到脚本时用来报「我往哪儿找过」的仓库根猜测。</summary>
    private static string RepoGuess()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 3 && dir?.Parent is not null; i++) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
