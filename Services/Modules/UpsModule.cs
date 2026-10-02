using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace XAssistant.Services.Modules;

/// <summary>一次拉取拿到的 UPS 快照（只留判据要用的字段）。</summary>
public sealed record UpsSnapshot(bool Reachable, string? Error, IReadOnlyList<UpsDevice> Devices);

public sealed record UpsDevice(
    string Name, bool Online, double? InputVolts, double? OutputVolts, double? LoadPercent,
    double? BatteryVolts, int? BackupMinutes, double? Temperature, IReadOnlyList<string> Alarms);

/// <summary>凭据与地址。手填 Bearer 优先；没填就用账号密码登录换一枚。</summary>
public sealed record UpsCredentials(string BaseUrl, string LoginPath, string Token, string Username, string Password);

/// <summary>一次取数的结果，外加一句「令牌是怎么来的」——面板的令牌状态行显示它。</summary>
public sealed record UpsOutcome(UpsSnapshot Snapshot, string? TokenNote);

/// <summary>取数接口。<see cref="WinPowerApi"/> 是真实实现，测试里塞个假的即可。</summary>
public interface IUpsApi
{
    Task<UpsOutcome> Fetch(UpsCredentials credentials);

    /// <summary>丢掉缓存的令牌，下一次取数强制重新登录。</summary>
    void ForgetToken();
}

/// <summary>HTTP 传输层。单独抽出来是为了让登录/续期这条链路能离线推演。</summary>
public interface IUpsTransport
{
    Task<(int Status, string Body)> Send(string method, string url, string? jsonBody, string? authorization);
}

/// <summary>告警出口。默认走 <see cref="EffectQueue"/>，测试里换成记账的假件。</summary>
public interface IUpsAlertSink
{
    /// <summary>放一条效果命令（与 xa 后面那段一字不差）。</summary>
    void Raise(string commandLine);
    /// <summary>按 tag 收掉（无限重播的告警靠这个停）。</summary>
    void Clear(string tag);
}

/// <summary>
/// UPS 供电监视：轮询 WinPower G2，按判据决定「断电 → 弹紧急警告并无限重播」，市电恢复后按 tag 收掉。
///
/// 四处在意的地方：
///
/// 1. <b>鉴权两条路</b>：直接粘一枚 Bearer，或者只填账号密码由模块自己登录换令牌。
///    令牌过期不报警——它会先自己重新登录一次，成功了就当无事发生；只有登录也拿不到令牌
///    才算「监视中断」（黄档），因为那时候瞎的是监视，不是电。
/// 2. <b>这站鉴权失败也回 HTTP 200</b>，真结果在业务码里（<c>code:"401"</c>）。不看这一层就会把
///    「令牌过期」当成「没设备」，告警从此静默失效——这是最不能错的一种错。
/// 3. <b>元数据是活的</b>：令牌状态行平时 <c>Visible=false</c> 收着，登录成功后模块
///    <c>MutateMeta</c> 把它放出来并写上「上次登录时间 / 是否自动续期」；凭据填全了才放开
///    「重新登录」那一行。前端只读元数据，所以这条通道不会和提交回路打架。
/// 4. <b>进出状态都要迟滞</b>：市电抖一下不该弹全屏，恢复瞬间也不该抢在真恢复之前收警。
/// </summary>
public sealed class UpsModule : IWatchModule
{
    /// <summary>紧急告警的 tag：恢复时按它精确收掉，不会误杀别的紧急档。</summary>
    public const string OutageTag = "ups-outage";
    public const string BatteryTag = "ups-battery";
    public const string WatchdogTag = "ups-watchdog";

    /// <summary>
    /// 演练用的独立 tag。试弹绝不能复用真实告警的参数：带了 <c>-replay</c> 就是一按上去
    /// 无限重播，失去「看一眼效果」的意义；共用 <see cref="OutageTag"/> 还会把真的断电告警一起杀。
    /// </summary>
    public const string DrillTag = "ups-drill";

    private readonly IUpsApi _api;
    private readonly IUpsAlertSink _sink;

    private ModuleContext? _ctx;
    private DateTime _lastFetch = DateTime.MinValue;
    private bool _busy;
    private DateTime? _outageSince;      // 判据开始成立的一刻（还没到确认时长时挂在这）
    private DateTime? _recoverSince;     // 恢复判据开始成立的一刻
    private bool _outageAlerted;

    public UpsModule() : this(new WinPowerApi(), new EffectQueueAlertSink()) { }

    /// <summary>测试用的注入点：假接口 + 假告警出口。</summary>
    public UpsModule(IUpsApi api, IUpsAlertSink sink)
    {
        _api = api;
        _sink = sink;
    }

    public string Id => "ups";
    public string Title => "UPS 供电监视 · WinPower";
    public ModuleSpace Space => new(400, 360);

    // ===== 参数行：bool 走勾选，其余走值输入 =====

    private readonly List<ModuleField> _fields = new()
    {
        new("alertOnOutage", "断电时弹紧急警告", true),
        new("alertOnBatteryLow", "电池低电位时黄档预警", false),
        new("alertOnWatchdog", "监视中断时黄档提示", true),
        new("baseUrl", "站点地址", "https://localhost:9623"),
        new("apiToken", "Bearer 令牌", "", "手填则优先用它，不再走登录"),
        new("username", "账号", "admin"),
        new("password", "密码", "", "只存本机，模块用它换令牌"),
        new("loginPath", "登录接口路径", "/api/v1/login"),
        new("deviceId", "只看某台设备", "", "留空 = 全部设备"),
        new("pollSeconds", "拉取间隔", 20.0),
        new("outageVoltBelow", "市电判据：输入电压低于", 120.0),
        new("batteryLowVolts", "电池低电位阈值", 24.0),
        new("outageHoldSeconds", "断电确认时长", 5.0),
        new("recoverHoldSeconds", "恢复确认时长", 15.0),
        new("alarmWords", "告警关键词", "powerloss, power loss, 停电, shutdown immanent"),
        new("tokenState", "令牌", ""),
        new("relogin", "强制重新登录", false),
        new("status", "状态", "未启用"),
        new("testAlert", "试弹一次紧急警告", false),
    };

    public IReadOnlyList<ModuleField> Fields() => _fields;

    public IReadOnlyList<ModuleMeta> Metas() => new List<ModuleMeta>
    {
        new("alertOnOutage") { Hint = "会固定重播到市电恢复；恢复后按 tag 自动收掉" },
        new("baseUrl") { DisplayName = "站点地址（含端口）Base URL", Hint = "自签证书只对本机与私网放行" },
        new("apiToken") { Secret = true, DisplayName = "Bearer 令牌 Token", Hint = "WinPower 的令牌通常 30 分钟过期，留空让模块自己续" },
        new("username") { DisplayName = "账号 Username" },
        new("password") { Secret = true, DisplayName = "密码 Password", Hint = "只存本机 watch-modules.json，不进仓库" },
        new("loginPath") { Hint = "POST 换令牌的路径；真机不同就改这一行" },
        new("pollSeconds") { NumericOnly = true, Min = 5, Max = 600, Unit = "秒" },
        new("outageVoltBelow") { NumericOnly = true, Min = 0, Max = 300, Unit = "V" },
        new("batteryLowVolts") { NumericOnly = true, Min = 0, Max = 400, Unit = "V", Hint = "电池电压低于它就弹黄档（与断电警互不影响）" },
        new("outageHoldSeconds") { NumericOnly = true, Min = 0, Max = 300, Unit = "秒" },
        new("recoverHoldSeconds") { NumericOnly = true, Min = 0, Max = 600, Unit = "秒" },
        // 令牌状态行平时收着：登录成功那一刻由模块放开，并补上「上次登录时间」的悬停说明
        new("tokenState") { Editable = false, Visible = false, DisplayName = "令牌 Token" },
        new("relogin") { Visible = false, Hint = "凭据填全后才出现；按下后下一次取数强制重新登录" },
        new("status") { Editable = false, Hint = "只读：最近一次判据与拉取结果" },
        new("testAlert") { Hint = "按下即 true；演练走独立 tag 且不重播，不影响真实断电告警" },
    };

    public void OnActivate(ModuleContext context)
    {
        _ctx = context;
        _lastFetch = DateTime.MinValue;      // 激活这一轮就该立刻拉一次，不等满间隔
        context.MutateMeta("tokenState", meta => meta.Visible = false);
        SetStatus("已启用，等待第一次拉取");
    }

    public void OnDeactivate()
    {
        // 关掉模块不能把告警留在屏上无限闪：收干净再走
        _sink.Clear(OutageTag);
        _sink.Clear(BatteryTag);
        _sink.Clear(WatchdogTag);
        _sink.Clear(DrillTag);
        _outageAlerted = false;
        _outageSince = _recoverSince = null;
        SetStatus("未启用");
        _ctx = null;
    }

    public void OnUpdate(ModuleContext context)
    {
        var now = context.Now;
        var interval = TimeSpan.FromSeconds(Math.Clamp(Number("pollSeconds", 20), 5, 600));
        if (_busy || now - _lastFetch < interval) return;
        _ = PollAsync(context, now);
    }

    private async Task PollAsync(ModuleContext context, DateTime now)
    {
        _busy = true;
        _lastFetch = now;
        UpsOutcome outcome;
        try
        {
            outcome = await _api.Fetch(new UpsCredentials(String("baseUrl", "https://localhost:9623"),
                String("loginPath", "/api/v1/login"), String("apiToken", ""), String("username", ""), String("password", "")));
        }
        catch (Exception error)
        {
            outcome = new UpsOutcome(new UpsSnapshot(false, error.GetBaseException().Message, Array.Empty<UpsDevice>()), null);
        }
        finally { _busy = false; }

        ReportToken(context, outcome.TokenNote);
        ApplySnapshot(outcome.Snapshot, now, context);
    }

    /// <summary>
    /// 把「令牌哪来的」写进元数据：这一行平时收着，登录过就放出来。
    /// 走元数据而不是塞进状态文案，是因为它有自己的显示语义（只读、悬停看细节），
    /// 也不该混进用户能改的那一列键值对里。
    /// </summary>
    private void ReportToken(ModuleContext context, string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;
        context.MutateMeta("tokenState", meta =>
        {
            meta.Visible = true;
            meta.Hint = note;
        });
        context.Submit("tokenState", note.Length > 28 ? note[..28] + "…" : note);
    }

    /// <summary>
    /// 状态机本体（同步、可测）：判据 → 迟滞 → 放警 / 收警。
    /// </summary>
    public void ApplySnapshot(UpsSnapshot snapshot, DateTime now, ModuleContext context)
    {
        var devices = Filter(snapshot.Devices);

        if (!snapshot.Reachable)
        {
            SetStatus($"监视中断：{Truncate(snapshot.Error ?? "接口不可达")}");
            // 判据不明：既不确认断电也不确认恢复，迟滞计时器一起清零，免得恢复后立刻误收
            _outageSince = _recoverSince = null;
            if (Bool("alertOnWatchdog")) RaiseWatchdog(snapshot.Error);
            return;
        }
        if (devices.Count == 0)
        {
            SetStatus("接口可达但没有匹配的设备");
            return;
        }

        double floor = Number("outageVoltBelow", 120);
        var words = String("alarmWords", "").Split(',', '，').Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
        var down = devices.Where(d => Outage(d, floor, words)).ToList();
        bool outage = down.Count > 0;

        var hold = TimeSpan.FromSeconds(Math.Clamp(Number("outageHoldSeconds", 5), 0, 300));
        var back = TimeSpan.FromSeconds(Math.Clamp(Number("recoverHoldSeconds", 15), 0, 600));
        var first = devices[0];
        SetStatus(outage
            ? $"断电判据成立：{string.Join("、", down.Select(d => $"{d.Name} 输入 {Text(d.InputVolts)}V"))}"
            : $"市电正常：{first.Name} 输入 {Text(first.InputVolts)}V · 负载 {Text(first.LoadPercent)}% · 后备 {first.BackupMinutes ?? 0} 分");

        if (outage)
        {
            _recoverSince = null;
            _outageSince ??= now;
            if (Bool("alertOnOutage") && !_outageAlerted && now - _outageSince.Value >= hold)
            {
                _outageAlerted = true;
                var detail = string.Join("、", down.Select(d => $"{d.Name} 输入 {Text(d.InputVolts)}V"));
                _sink.Raise($"-s emergency 9 1 1 -border on 60 30 1 -lable on 30 紧急 · 供电中断 {detail} 负载 {Text(first.LoadPercent)}% 后备 {first.BackupMinutes ?? 0} 分钟 -id {OutageTag} -replay 20");
                context.Log($"断电告警已上屏（tag {OutageTag}）");
            }
            return;
        }

        // 市电正常：清断电计时，攒够恢复确认时长才收警
        _outageSince = null;
        if (_outageAlerted)
        {
            _recoverSince ??= now;
            if (now - _recoverSince.Value >= back)
            {
                _outageAlerted = false;
                _recoverSince = null;
                _sink.Clear(OutageTag);
                _sink.Clear(BatteryTag);
                context.Log($"市电恢复，已收掉 {OutageTag}");
            }
        }
        else _sink.Clear(WatchdogTag);

        // 电池低电位与断电警互不影响：市电正常时电池也可能拖快完了，两条通道各说各的事
        MaybeBattery(first);
    }

    /// <summary>断电判据：输入电压掉到阈值下、掉线、或告警文本命中关键词。</summary>
    private static bool Outage(UpsDevice device, double floor, IReadOnlyList<string> words)
    {
        if (!device.Online) return true;
        if (device.InputVolts is { } v && v < floor) return true;
        return device.Alarms.Any(a => words.Any(w => w.Length > 0 && a.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>电池低电位：黄档，单独开关，不参与「固定显示到恢复」那条通道。</summary>
    private void MaybeBattery(UpsDevice device)
    {
        if (!Bool("alertOnBatteryLow")) return;
        double floor = Number("batteryLowVolts", 24);
        if (device.BatteryVolts is not { } bv || bv >= floor) return;
        _sink.Raise($"-s warn 6 1 1 -border on 40 20 1 -lable on 26 电池低电位 {device.Name} {Text(bv)}V 低于 {Text(floor)}V -id {BatteryTag} -replay 60");
    }

    private void RaiseWatchdog(string? reason)
        => _sink.Raise($"-s warn 6 1 1 -lable on 26 UPS 监视中断 {Truncate(reason ?? "")} -id {WatchdogTag} -replay 120");

    private void TestAlert()
    {
        // 演练：一次就收（不带 -replay），走独立 tag，也不把状态机的「已弹」标志位抬起来——
        // 试完弹紧接着真断电还得能弹。
        _sink.Raise($"-s emergency 9 1 1 -border on 60 30 1 -lable on 30 演练 · 紧急警告测试（不会重播） -id {DrillTag}");
        _ctx?.Submit("testAlert", false);   // 复位；前端按下时本地已经 true，收不到复位也不卡
    }

    // ===== 参数：接收与钳制 =====

    public void OnValuesPushed(IReadOnlyDictionary<string, object?> values)
    {
        if (_ctx is not { } context) return;

        if (values.TryGetValue("testAlert", out var test) && Truthy(test)) TestAlert();
        if (values.TryGetValue("relogin", out var again) && Truthy(again))
        {
            _api.ForgetToken();
            _lastFetch = DateTime.MinValue;   // 下一轮 tick 立刻重取（会重新登录）
            context.Submit("relogin", false);
            context.Log("已要求重新登录");
        }

        Clamp(context, values, "pollSeconds", 5, 600);
        Clamp(context, values, "outageVoltBelow", 0, 300);
        Clamp(context, values, "batteryLowVolts", 0, 400);
        Clamp(context, values, "outageHoldSeconds", 0, 300);
        Clamp(context, values, "recoverHoldSeconds", 0, 600);
        if (values.TryGetValue("baseUrl", out var url) && url is string u && string.IsNullOrWhiteSpace(u))
            context.Submit("baseUrl", "https://localhost:9623");

        // 元数据随凭据变化：账号密码齐了才放开「强制重新登录」那一行
        bool canLogin = !string.IsNullOrWhiteSpace(String("username", "")) && !string.IsNullOrWhiteSpace(String("password", ""));
        context.MutateMeta("relogin", meta => meta.Visible = canLogin);
    }

    private static void Clamp(ModuleContext ctx, IReadOnlyDictionary<string, object?> values, string key, double min, double max)
    {
        if (!values.TryGetValue(key, out var raw)) return;
        double parsed = raw switch
        {
            double d => d,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => double.NaN,
        };
        if (double.IsNaN(parsed)) { ctx.Log($"{key} 不是数字，退回原值"); return; }
        double clamped = Math.Clamp(parsed, min, max);
        if (Math.Abs(clamped - parsed) > 1e-9)
        {
            ctx.Submit(key, clamped);
            ctx.Log($"{key} 钳制到 {clamped.ToString(CultureInfo.InvariantCulture)}（允许 {min}–{max}）");
        }
    }

    private List<UpsDevice> Filter(IReadOnlyList<UpsDevice> devices)
    {
        var id = String("deviceId", "");
        return string.IsNullOrWhiteSpace(id)
            ? devices.ToList()
            : devices.Where(d => d.Name.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SetStatus(string text)
    {
        var field = _fields.FirstOrDefault(f => f.Key == "status");
        if (field is not null) field.Value = text;
        _ctx?.Submit("status", text);
    }

    private object? Raw(string key) => _fields.FirstOrDefault(f => f.Key == key)?.Value;
    private bool Bool(string key) => Truthy(Raw(key));
    private string String(string key, string fallback) => Raw(key) as string ?? fallback;

    private double Number(string key, double fallback) => Raw(key) switch
    {
        double d => d,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => fallback,
    };

    private static bool Truthy(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var p) && p,
        _ => false,
    };

    private static string Text(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "—";

    private static string Truncate(string s) => s.Length <= 46 ? s : s[..46] + "…";
}

/// <summary>
/// WinPower G2 的取数与登录。手填 Bearer 优先；否则用账号密码 POST 登录路径换一枚令牌，
/// 并在令牌被判过期时自动重登一次再重试。自签证书只对<b>本机与私网</b>放行，公网地址照常校验。
/// </summary>
public sealed class WinPowerApi : IUpsApi
{
    private readonly IUpsTransport _transport;
    private string? _cachedToken;
    private string? _tokenNote;

    public WinPowerApi(IUpsTransport? transport = null) => _transport = transport ?? new HttpUpsTransport();

    public void ForgetToken() => _cachedToken = null;

    public async Task<UpsOutcome> Fetch(UpsCredentials cred)
    {
        var token = FirstNonBlank(cred.Token, _cachedToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            var firstLogin = await TryLogin(cred);
            if (!firstLogin.Ok)
                return new UpsOutcome(new UpsSnapshot(false, firstLogin.Note ?? "没有可用凭据：填 Bearer 或账号密码",
                    Array.Empty<UpsDevice>()), firstLogin.Note);
            token = _cachedToken;
        }

        var first = await Get(cred, token!);
        if (!IsAuthFailure(first)) return new UpsOutcome(first, _tokenNote);

        // 令牌过期不是故障：自己重登一次再试，成功就不打扰用户
        var again = await TryLogin(cred);
        if (!again.Ok)
            return new UpsOutcome(new UpsSnapshot(false, $"令牌已过期且重新登录失败：{again.Note}", Array.Empty<UpsDevice>()), again.Note);
        var second = await Get(cred, _cachedToken!);
        return new UpsOutcome(second, second.Reachable ? "令牌自动续期" : again.Note);
    }

    private async Task<UpsSnapshot> Get(UpsCredentials cred, string token)
    {
        string url = $"{cred.BaseUrl.TrimEnd('/')}/api/v1/deviceData/detail/list?current=1&pageSize=20"
            + "&areaId=00000000-0000-0000-0000-000000000000&includeSubArea=true&pageNum=1&deviceType=1";
        var (status, body) = await _transport.Send("GET", url, null, token);
        if (status is < 200 or >= 300) return new UpsSnapshot(false, $"HTTP {status}", Array.Empty<UpsDevice>());
        return Parse(body);
    }

    private async Task<(bool Ok, string? Note)> TryLogin(UpsCredentials cred)
    {
        if (string.IsNullOrWhiteSpace(cred.Username) || string.IsNullOrWhiteSpace(cred.Password))
            return (false, "没填账号或密码，也没有可用的 Bearer 令牌");
        string url = $"{cred.BaseUrl.TrimEnd('/')}/{cred.LoginPath.Trim('/')}";
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["username"] = cred.Username, ["password"] = cred.Password,
        });
        try
        {
            var (status, body) = await _transport.Send("POST", url, payload, null);
            if (status is < 200 or >= 300) return (false, $"登录 HTTP {status}");
            string? token = TokenOf(body);
            if (token is null) return (false, "登录响应里找不到令牌字段（把登录路径或响应结构贴过来即可适配）");
            _cachedToken = token;
            _tokenNote = $"账号登录换令牌 {DateTime.Now:HH:mm:ss}";
            return (true, _tokenNote);
        }
        catch (Exception error)
        {
            return (false, error.GetBaseException().Message);
        }
    }

    /// <summary>各家网关的令牌字段名不统一，按几个常见位置找一遍；找不到就返回 null 让上层报清楚。</summary>
    private static string? TokenOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (ErrorOf(root) is { } code && code != "000000") return null;
            string[] paths = ["data.token", "data.accessToken", "data.access_token", "token", "accessToken", "data"];
            foreach (string path in paths)
            {
                var node = path.Split('.').Aggregate((JsonElement?)root,
                    (current, key) => current is { } c && c.ValueKind == JsonValueKind.Object && c.TryGetProperty(key, out var next) ? next : null);
                if (node is { ValueKind: JsonValueKind.String } leaf && !string.IsNullOrWhiteSpace(leaf.GetString())) return leaf.GetString();
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>解析设备列表响应；字段名以真机为准，缺字段一律按 null 处理而不是抛。</summary>
    public static UpsSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // 鉴权失败也回 HTTP 200，真结果在业务码里：不看这一层就会把「令牌过期」当成「没设备」
        if (ErrorOf(doc.RootElement) is { } code && code != "000000")
        {
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : code;
            return new UpsSnapshot(false, $"业务码 {code}：{message}", Array.Empty<UpsDevice>());
        }
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new UpsSnapshot(true, null, Array.Empty<UpsDevice>());

        var devices = new List<UpsDevice>();
        foreach (var item in data.EnumerateArray())
        {
            var asset = item.TryGetProperty("assetDevice", out var a) ? a : default;
            var real = item.TryGetProperty("realtime", out var r) ? r : default;
            var alarms = new List<string>();
            if (item.TryGetProperty("activeAlarms", out var al) && al.ValueKind == JsonValueKind.Array)
                foreach (var alarm in al.EnumerateArray())
                    alarms.Add(alarm.ValueKind == JsonValueKind.String ? alarm.GetString() ?? "" : alarm.ToString());
            devices.Add(new UpsDevice(
                Str(asset, "alias") ?? Str(asset, "serialNumber") ?? "未命名设备",
                item.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
                Num(real, "inputVolt1"), Num(real, "outputVolt1"), Num(real, "loadPercent"),
                Num(real, "batVoltP"), (int?)Num(real, "batRemainTime"), Num(real, "upsTemperature"),
                alarms));
        }
        return new UpsSnapshot(true, null, devices);
    }

    private static bool IsAuthFailure(UpsSnapshot snapshot)
        => !snapshot.Reachable && snapshot.Error is { } e && e.Contains("401");

    private static string? ErrorOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

/// <summary>默认传输实现。这站要 POST 一个 JSON 体，所以直接一层 HttpClient。</summary>
public sealed class HttpUpsTransport : IUpsTransport
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None || IsLocal(request?.RequestUri?.Host),
    });

    public async Task<(int Status, string Body)> Send(string method, string url, string? jsonBody, string? authorization)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(authorization))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.Trim());
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// 只对「本机与私网」放行自签证书：这站默认只听 localhost，部署到内网后会是 10/8、
    /// 172.16–172.31、192.168/16 这几段；公网地址一律照常校验，不因图方便而全局关验证。
    /// </summary>
    private static bool IsLocal(string? host) =>
        host is null || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var ip) && (IPAddress.IsLoopback(ip) || IsPrivate(ip));

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length switch
        {
            4 => b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31),
            16 => ip.IsIPv6SiteLocal,
            _ => false,
        };
    }
}

/// <summary>默认告警出口：进程内直接进播放队列（语法与 xa 完全一致，便于两边互换）。</summary>
public sealed class EffectQueueAlertSink : IUpsAlertSink
{
    public void Raise(string commandLine)
    {
        if (!EffectCommand.TryParse(commandLine, out var command)) return;
        EffectQueue.Shared.Submit(command);
    }

    public void Clear(string tag) => EffectQueue.Shared.Kill(null, tag, null);
}
