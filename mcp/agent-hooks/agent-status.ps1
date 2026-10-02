# agent-status.ps1 —— AI 编码代理的会话状态 → xa 全屏提醒（多平台通用）
#
# 由各平台的 hooks 在事件触发时调用，事件 JSON 从 stdin 传入。
# 各平台（Claude Code / Qoder CN / Trae …）用的都是同一套 PascalCase 事件名与
# 「stdin 收 JSON + exit code 表达决定」协议，分诊逻辑因此完全一致：
# 一份脚本服务所有平台，平台差异只剩日志里那一列与安装器写的配置路径。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File agent-status.ps1 -Platform qoder
#
# 分档（颜色由 -s 段表达，正文不写颜色词）：
#   PostToolUseFailure / PostToolUse(结果异常)   黄：工具挂了但对话还在跑，不需要按红色惊动
#   StopFailure / is_interrupt                    红：整轮对话被打断（限流、配额、人中断），必须人回来
#   PermissionRequest / Notification              黄：等人授权或回答
#   Stop                                          普通：这轮回复完成（stop_hook_active 时静默，防死循环）
# 正文四段固定：机械回复 · 现况 · 来源 · 信息（缺哪段省哪段，' · ' 只作段间分隔，信息段内部用 ' / '）
#   机械回复＝提醒/警告/故障/询问/回复，是别的系统分流用的**固定领词**，不许现场造词；
#   现况说清是哪一环：主对话 工具调用失败/意外中断对话/对话回合结束/等待授权/请求人类介入，
#     子实例换成 子代理失败/子代理成功（Qoder 事件带 agent_id/agent_type 即子实例，主对话没这两个字段；
#     名字与「派下去干什么」进信息段，取自 IDE 落盘的 <会话>\subagents\agent-<id>.meta.json）；
#   来源＝这场对话的标题（IDE 的 vscdb 任务名 / 事件自带 session_title），拿不到退回项目目录名；
#   信息＝定位用的额外内容（错误码 / 工具名 / 退出码 / 要人回的那个问题）。
# 每条消息再挂一段标签：-tag <来源>,<main|subagent>,<类型>（进命令行，不进正文）
#   面板的播放闸门按类型逐个开关，`xa -k -tag ask` 也按它收；类型词表固定 6 个，由调用点显式传：
#     ask 等人（请求人类介入/等待授权）· done 回合结束 · tool-fail 工具挂了 · interrupt 整轮被打断
#     error API/配额那类告警 · notice 其它仍要报的（缺省）
#   标签只说「这是哪一类」，别说「这是同一件事」：**不写 -id**（归并身份是另一码事，别混用）。
# **对话内容一律不上屏**：last_assistant_message 那种原文既读不到重点又泄上下文。
# 铁律：永远 exit 0 —— 提醒脚本再坏也不许把对话流阻断。
param(
    [string]$Platform = 'generic',
    [string]$LogDir = (Join-Path $env:LOCALAPPDATA 'XAssistant\agent-hooks'),
    [switch]$NoEffect          # 自测用：只记日志，不拉 xa
)

$ErrorActionPreference = 'Continue'
$log = Join-Path $LogDir 'agent-status.log'

try {
    # 用 UTF-8 直读 stdin 字节流：[Console]::In 会按系统码页（GBK）解码 IDE 写来的 UTF-8，
    # 事件里的中文（如 Stop 的 last_assistant_message）一花，JSON 结构就被咬断、解析必挂——
    # 这条实踩过的坑：当时误判成「IDE 写截断」，其实是我自己读坏
    $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $raw = $reader.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    # 留一份原始 JSON：各平台字段名以文档协议为准，跑一轮后按这份日志校准。
    # 前缀里带平台与脚本所在目录（`Platform@根目录`）：同一平台可能往多个候选位置装过配置，
    # 这一列能直接告诉你是哪个位置的配置被读到、进而被执行的。
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $origin = Split-Path (Split-Path $PSScriptRoot -Parent) -Leaf
    $evt = $null
    $name = '残缺事件'
    try { $evt = $raw | ConvertFrom-Json; $name = [string]$evt.hook_event_name } catch { }
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [$Platform@$origin] [$name] $(($raw -replace '[\r\n\t]+', ' ').Trim())" |
        Add-Content -Path $log -Encoding UTF8
    if (-not $evt) {
        # 事件 JSON 被拦腰截断（IDE 某些版本写 hook stdin 的新行为）：正则捞回事件名与 cwd，
        # 报一条红提醒而不是静默吞掉——截断本身就是一种「对话意外中断」
        $rescued = if ($raw -match '"hook_event_name"\s*:\s*"([^"]+)"') { $Matches[1] } else { '未知' }
        $evt = [pscustomobject]@{ hook_event_name = 'BrokenEvent' }
        if ($raw -match '"cwd"\s*:\s*"((?:[^"\\]|\\.)*)"') {
            $cwd = $Matches[1] -replace '\\\\', '\'
            $evt | Add-Member -NotePropertyName cwd -NotePropertyValue $cwd
        }
        $evt | Add-Member -NotePropertyName error_type -NotePropertyValue "数据截断($rescued)"
        $name = 'BrokenEvent'
    }

    # 找 xa：环境变量（自测/便携） → 注册垫片 → PATH → 仓库 Debug 产物
    $xa = $env:XASSISTANT_XA
    if (-not $xa -or -not (Test-Path $xa)) {
        $candidate = Join-Path $env:LOCALAPPDATA 'XAssistant\bin\xa.cmd'
        if (Test-Path $candidate) {
            $xa = $candidate
        } else {
            $cmd = Get-Command xa -ErrorAction SilentlyContinue
            if ($cmd) {
                $xa = $cmd.Source
            } else {
                $xa = Join-Path $PSScriptRoot '..\..\bin\Debug\net8.0-windows\XAssistant.exe'
            }
        }
    }

    # 控制字符与引号会打乱命令行，统一清掉；超长截断
    function Clean([string]$s) {
        if ([string]::IsNullOrWhiteSpace($s)) { return '' }
        ($s -replace '[\r\n\t]+', ' ' -replace '["“”]', '').Trim()
    }
    function Cut([string]$s, [int]$n) {
        $s = Clean $s
        if ($s.Length -gt $n) { $s.Substring(0, $n) + '…' } else { $s }
    }
    # 子进程按系统码页（GBK）写 stderr、上层按 UTF-8 解码时，错误原文里会留下 U+FFFD 这类不可逆
    # 替换符（实踩：PowerShell 中文报错上屏成一片乱码）。这种文本读不懂也晒不得：出现两个以上
    # 就当整段不可信，丢掉后由调用方退回错误码 / 工具名，卡片上不再成串晒乱码。
    function Trustworthy([string]$s) {
        if ([string]::IsNullOrEmpty($s)) { return '' }
        if (([regex]::Matches($s, '\uFFFD')).Count -ge 2) { return '' }
        return ($s -replace '\uFFFD', '')
    }

    # 主对话还是子代理：Qoder / Claude 系在事件里带 agent_id + agent_type，**主对话没有这两个字段**
    # （本机 1.1.57 实测：agent_type 见到 general-purpose / Explore）。agent_id 还能直接对上 IDE 落盘的
    # 子代理档案 `agent-<agent_id>.meta.json`，里面有 invocationName（这次调用叫什么）、description
    # （派下去干什么）、color（IDE 给这个子代理配的颜色，内置类型才有）。
    # 实测布局：<projects>\<slug>\<会话>.jsonl 与同名目录 <projects>\<slug>\<会话>\subagents\ 平级，
    # 所以两个候选位置都要试（transcript_path 指到会话目录本身的版本也吃得下）。
    function Get-AgentMeta([string]$agentId, [string]$transcript) {
        if (-not $agentId -or -not $transcript) { return $null }
        try {
            $dir = Split-Path $transcript -Parent
            $leaf = Split-Path $transcript -Leaf
            $file = if ($leaf -like '*.jsonl') {
                # Split-Path -LeafBase 是 PS 6+ 才有的，Windows PowerShell 5.1 只能用 .NET 取；
                # 命令模式下方法调用必须整体加括号，否则被当成两个错位参数（异常会被下面的 catch 静默吞掉）
                $sessionDir = Join-Path $dir ([IO.Path]::GetFileNameWithoutExtension($leaf))
                Join-Path (Join-Path $sessionDir 'subagents') "agent-$agentId.meta.json"
            } else {
                Join-Path $dir "subagents\agent-$agentId.meta.json"
            }
            if (-not (Test-Path $file)) {
                $alt = Join-Path $dir "subagents\agent-$agentId.meta.json"
                if (-not (Test-Path $alt)) { return $null }
                $file = $alt
            }
            return (Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json)
        } catch { return $null }   # 档案读不到只少一段说明，不影响提醒
    }

    # 事件带来的上下文：cwd 叶名是项目；对话标题读 IDE 的 state.vscdb（tasks 里 id→name 的真实任务名，
    # 不是首句对话内容——拿内容当标题会把聊天原文晒到屏幕上，用户明确不要）。
    # vscdb 被 IDE 独占，用 FileShare.ReadWrite 开流拷出来查；Latin1 字节↔字符 1:1，IndexOf 秒级
    $project = if ($evt.cwd) { Split-Path $evt.cwd -Leaf } else { '' }
    # 装机目录名各家版本飘过好几个写法（本机实测 QoderCN，社区也见 Trae SOLO CN），候选全列——
    # 拿不到 vscdb 只是标题缺失，不影响提醒本身
    $DbAppDirs = @{
        'qoder'      = @('QoderCN', 'Qoder CN')
        'trae'       = @('Trae CN', 'TRAE SOLO CN')
        'trae-intl'  = @('Trae')
    }
    function Get-TaskTitle([string]$sessionId, [string[]]$appDirs) {
        if (-not $sessionId) { return '' }
        $latin = $null
        foreach ($app in $appDirs) {
            $db = Join-Path $env:APPDATA (Join-Path $app 'User\globalStorage\state.vscdb')
            if (-not (Test-Path $db)) { continue }
            try {
                $fs = [IO.File]::Open($db, 'Open', 'Read', 'ReadWrite')
                $bytes = New-Object byte[] $fs.Length
                [void]$fs.Read($bytes, 0, $bytes.Length)
                $fs.Dispose()
            } catch { continue }
            # Latin1 字节↔字符 1:1，IndexOf 秒级；中文值后面还原字节再 UTF8 解码
            $latin = [Text.Encoding]::GetEncoding('ISO-8859-1').GetString($bytes)
            break
        }
        if (-not $latin) { return '' }
        $taskId = $sessionId -replace '\.session\..*$', ''
        $anchor = '"' + $taskId + '"'
        $i = $latin.IndexOf($anchor)
        while ($i -ge 0) {
            # 同一个任务对象里 id 在前、name/title 在后；窗口 900 字节够跨到
            $seg = $latin.Substring($i, [Math]::Min(900, $latin.Length - $i))
            $m = [regex]::Match($seg, '"(?:name|title)":"([^"]{1,80})"')
            if ($m.Success) {
                $vb = [Text.Encoding]::GetEncoding('ISO-8859-1').GetBytes($m.Groups[1].Value)
                $title = ([Text.Encoding]::UTF8.GetString($vb) -replace '[\r\n\t]+', ' ').Trim()
                if ($title) { return $title }
            }
            $i = $latin.IndexOf($anchor, $i + 1)
        }
        return ''
    }
    # 来源段只许是「这场对话的名字」，顺序：事件自带 session_title（DSH 真给）→ IDE 的 vscdb 真实任务名
    # → 项目目录名 → 都没有就整段省略。
    # 这里曾经排过一条捷径 `parent_business_info.name`，注释还写着"就是这轮任务名"——那是错的：
    # 本机 2056 条真事件回放出来，它装的是**用户自己打的那句话被截断**（"要，继续"、"unity我关了，你"、
    # "优化spline的e"、"ArgumentOu"），而且它排在 vscdb 之前，把真任务名整个盖掉，
    # 屏幕上就又变回了"拿对话内容当消息"——用户明确禁止过的那件事。
    $dialog = Cut ([string]$evt.session_title) 80
    if (-not $dialog) { $dialog = Get-TaskTitle ([string]$evt.session_id) $DbAppDirs[$Platform] }

    # ---- 四段机械文案：机械回复 · 现况 · 来源 · 信息 ------------------------------------
    # 机械回复是**固定领词**（提醒 / 警告 / 故障 / 询问 / 回复）：颜色与分流按它走，别的系统也在认它，
    #   不许现场造词（旧写法把工具名塞进领词变成「授权(Bash)」就是个反例，现在工具名进信息段）。
    # 现况说清「是哪一环出的事」：主对话用 工具调用失败 / 意外中断对话 / 对话回合结束 / 请求人类介入 /
    #   等待授权；子实例换成 子代理失败 / 子代理成功——一眼分得清是人该回的还是机器自己挂的。
    # 来源只有**这场对话的名字**（标题 → 退回项目目录名 → 都没有就省这一段）。对话内容一律不上屏：
    #   last_assistant_message 那种原文晒出去既读不到重点又泄上下文。
    # 信息是定位用的额外内容：错误码 / 工具名 / 退出码 / 子代理在干什么 / 要人回的那个问题。
    # 缺哪段省哪段，段间统一 ' · '。
    $agentId = [string]$evt.agent_id
    $agentType = Cut ([string]$evt.agent_type) 24
    $subTag = ''
    if ($agentId -or $agentType) {
        # 带 agent 字段就是子实例。名字优先 IDE 的 invocationName（同类型并发也分得开），退回类型；
        # description 是「派下去干什么」，比工具名更能指认是哪一路——它进信息段，不占来源段。
        $meta = Get-AgentMeta $agentId ([string]$evt.transcript_path)
        $label = Cut ([string]$meta.invocationName) 24
        if (-not $label) { $label = $agentType }
        $job = Cut ([string]$meta.description) 30
        $subTag = if ($label) { "子代理 $label" } else { '子代理' }
        if ($job) { $subTag += "：$job" }
    }
    $isSub = $subTag -ne ''
    $source = if ($dialog) { '“' + $dialog + '”' } elseif ($project) { $project } else { '' }

    function Notice([string]$lead, [string]$state, [string]$info) {
        $bits = @($lead, $state)
        if ($source) { $bits += $source }
        if ($info) { $bits += (Cut $info 60) }
        return ($bits -join ' · ')
    }

    # 现况措辞按身份换：带 agent 字段就是子实例（Qoder 1.1.57 实测主对话没这两个字段，别的安全平台不拿缺字段当证据）
    function StateFail([string]$main) { if ($isSub) { '子代理失败' } else { $main } }
    function StateDone([string]$main) { if ($isSub) { '子代理成功' } else { $main } }

    # 信息段内部用 ' / ' 分隔：' · ' 只当四段之间那一个分隔用，正文按它拆才拆得出恰好四段
    function InfoOf($bits) { (@(@($bits) | Where-Object { $_ }) -join ' / ') }

    # AI 抛回来等人回的那个问题：Qoder 的确认通知把问句放在 details.input.questions[].question，
    # 授权请求放在 tool_input.questions，DSH 直接给 message。真正的问句优先——
    # "Tool AskUserQuestion requires confirmation" 这种确认 boilerplate 上屏等于什么都没说（实测事件如此）。
    function AskOf($o) {
        $qs = @()
        if ($o.details.input.questions) { $qs = @($o.details.input.questions) }
        elseif ($o.tool_input.questions) { $qs = @($o.tool_input.questions) }
        if ($qs.Count -eq 0) { return '' }
        # 先 Clean：问句里带引号会把后面的命令行拆坏
        $q = Clean (Trustworthy ([string]$qs[0].question))
        if (-not $q) { $q = Clean (Trustworthy ([string]$qs[0].header)) }
        if (-not $q) { return '' }
        if ($qs.Count -gt 1) {
            # 「共 N 问」才是要人知道的重点，不能被外层 60 字截断截掉：先给问句瘦身再挂尾巴
            $tail = " / 共 $($qs.Count) 问"
            $room = 60 - $tail.Length
            if ($q.Length -gt $room) { $q = $q.Substring(0, [Math]::Max(4, $room - 1)) + '…' + $tail }
            else { $q += $tail }
        }
        return $q
    }

    # 每条发出去的消息都挂三段标签：-tag <来源>,<身份>,<类型>（面板的按类型闸门与 `xa -k -tag ask` 都读它）
    #   来源＝-from 那个词，同一个词小写；身份＝事件带 agent 字段就是 subagent，否则 main；
    #   类型＝调用点显式传进来的那档（缺省 notice），词表固定 6 个：
    #     ask 等人（请求人类介入/等待授权）· done 这轮成了（对话回合结束/子代理成功）
    #     tool-fail 工具挂了但没被中断 · interrupt 整轮被打断（StopFailure / is_interrupt / 数据截断）
    #     error API/配额那类告警 · notice 其余仍要报的
    # 类型按**事件**取，不按现况措辞取：子代理挂了工具，正文写「子代理失败」，类型仍是 tool-fail——
    #   "是子代理"这件事已经由身份那一段说了，两段各管一件事，别在类型里重复编码。
    # 只分类，**不写 -id**：归并身份是另一码事（两场对话正文撞车也不许互相吃掉，那是 -id 的活）。
    function Show-Effect([string]$argLine, [string]$Kind = 'notice') {
        # 词表外的类型不许漏到命令行（面板按词表逐个开关，多一个词就永远关不掉）：退回 notice
        if ('ask', 'done', 'tool-fail', 'interrupt', 'error', 'notice' -notcontains $Kind) { $Kind = 'notice' }
        # 来源标记进命令行：消息栈徽章拿它贴 IDE 图标；generic 不挂（没得认的就保持素条）
        if ($Platform -and $Platform -ne 'generic') { $argLine += " -from $Platform" }
        $tagSrc = if ($Platform) { $Platform.ToLowerInvariant() } else { 'generic' }
        $argLine += " -tag $tagSrc,$(if ($isSub) { 'subagent' } else { 'main' }),$Kind"
        if ($NoEffect) {
            "$(Get-Date -Format 'HH:mm:ss')  [dry-run] $xa $argLine" | Add-Content -Path $log -Encoding UTF8
            return
        }
        # Start-Process 不等 xa：hook 脚本毫秒级交差，动画与消息栈由 XAssistant 自己放
        Start-Process -FilePath $xa -ArgumentList $argLine -WindowStyle Hidden
    }

    switch ($name) {
        'BrokenEvent' {
            $detail = Cut ([string]$evt.error_type) 30
            # 截断本身就是一种「对话意外中断」→ interrupt
            Show-Effect "-s error 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '提醒' '事件数据不完整' (InfoOf @($subTag, $detail)))`"" 'interrupt'
        }
        'Warning' {
            # DSH 原生插件把 token 上限、策略阻断等非崩溃异常归到黄档，信息段只留短摘要。
            $why = Cut (Trustworthy ([string]$evt.message)) 48
            if (-not $why) { $why = Cut ([string]$evt.warning_type) 30 }
            # token 上限 / 策略阻断这类是 API·配额口径的告警，不是等人也不是整轮断了 → error
            Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '警告' '运行告警' (InfoOf @($subTag, $why)))`"" 'error'
        }
        'StopFailure' {
            # 整轮回复被 API 错误打断（限流、配额溢出、过载…）：不是工具报错，是对话直接断了，归红
            $why = Cut $evt.error_type 30
            if (-not $why) { $why = Cut (Trustworthy ([string]$evt.error)) 40 }
            Show-Effect "-s error 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '故障' (StateFail '意外中断对话') (InfoOf @($subTag, $why)))`"" 'interrupt'
        }
        'PostToolUseFailure' {
            # 信息段只留定位信息：code = NNNNN 优先（IDE 错误都带这个码），其次错误首句，再退回工具名——
            # 回复/详情原文不上屏。人被打断是故障（红），工具自己挂了是警告（黄：对话还在跑，不必按红色惊动）
            $errText = Trustworthy (Clean ([string]$evt.error))
            $code = if ($errText -match 'code\s*=\s*(\d+)') { 'code ' + $Matches[1] } elseif ($errText) { Cut $errText 40 } else { '' }
            if (-not $code) { $code = Cut $evt.tool_name 30 }
            $stop = [bool]$evt.is_interrupt
            $sev = if ($stop) { 'error' } else { 'warn' }
            $lead = if ($stop) { '故障' } else { '警告' }
            $state = if ($stop) { StateFail '意外中断对话' } else { StateFail '工具调用失败' }
            # 类型跟分档同源：被中断＝整轮断了（interrupt），否则只是工具自己挂了（tool-fail）
            $kind = if ($stop) { 'interrupt' } else { 'tool-fail' }
            Show-Effect "-s $sev 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice $lead $state (InfoOf @($subTag, $code)))`"" $kind
        }
        'PermissionRequest' {
            # 工具名进信息段：领词得是固定的「询问」，不能现场变成「授权(Bash)」那种半截词。
            # 要授权的对象如果是提问工具，这条其实是「等我回话」而不是「等我点同意」：
            # 现况换成请求人类介入，信息段放真正的问题（实测 Qoder 写在 tool_input.questions 里）。
            $ask = Cut (AskOf $evt) 60
            $tool = Cut $evt.tool_name 30
            if ($ask) {
                Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '询问' '请求人类介入' (InfoOf @($subTag, $ask)))`"" 'ask'
            } else {
                Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '询问' '等待授权' (InfoOf @($subTag, $tool)))`"" 'ask'
            }
        }
        'Notification' {
            # notification_type 的取值集合各家不同（实踩的坑）：Claude/Qoder 给确认发 permission_prompt，
            # Trae 官方只有 idle_prompt（「等待确认」与「任务完成」都走它）。因此不拿类型等值硬筛：
            # 先认提问词，确认类关键词归黄；idle_prompt 按平台语义分流；未知类型宁可多报不漏接管
            $type = [string]$evt.notification_type
            $t = $type + ' ' + ([string]$evt.title) + ' ' + ([string]$evt.message)
            $note = Cut (Trustworthy ([string]$evt.message)) 48
            if (-not $note) { $note = Cut (Trustworthy ([string]$evt.title)) 40 }
            if ($t -match 'ask.?user.?question') {
                # 提问：信息段就是要人回的那句话（这是「该回什么」，不是对话原文）
                $ask = Cut (AskOf $evt) 60
                if (-not $ask) { $ask = Cut (Trustworthy ([string]$evt.message)) 60 }
                if (-not $ask) { $ask = Cut (Trustworthy ([string]$evt.title)) 60 }
                Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '询问' '请求人类介入' (InfoOf @($subTag, $ask)))`"" 'ask'
            } elseif ($type -match 'auth_success') {
                # 认证成功不是「需要人」，不打扰
            } elseif ($type -eq 'idle_prompt' -and $Platform -like 'trae*') {
                # Trae 的 idle_prompt 是「任务完成」，Stop 已经报过，不重复
            } elseif ($type -match 'permission|confirm|await|elicitation|needs|input|prompt') {
                Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '询问' '请求人类介入' (InfoOf @($subTag, $note)))`"" 'ask'
            } else {
                # 类型认不出来，但正文摆的是「请求人类介入」：标签跟着正文走，
                # 否则面板关了 ask 却还在收「要你回话」的卡——正是这次要修的毛病
                Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice '询问' '请求人类介入' (InfoOf @($subTag, $note)))`"" 'ask'
            }
        }
        'PostToolUse' {
            # Trae 没有 PostToolUseFailure：报错走这个事件的结果字段（error/exit_code/tool_response.is_error），
            # 结果正常就静默；挂了此事件的其他平台也不会误报
            $why = Cut (Trustworthy ([string]$evt.error)) 48
            if (-not $why -and $null -ne $evt.tool_response) {
                $tr = $evt.tool_response
                if ($tr.PSObject.Properties['error']) { $why = Cut (Trustworthy ([string]$tr.error)) 48 }
                elseif ($tr.PSObject.Properties['is_error'] -and $tr.is_error) { $why = Cut (Trustworthy ([string]$tr.content)) 40 }
            }
            $code = 0
            if ($evt.PSObject.Properties['exit_code']) { $code = [int]$evt.exit_code }
            if ($why -or $code -ne 0) {
                $stop = [bool]$evt.is_interrupt
                $sev = if ($stop) { 'error' } else { 'warn' }
                $lead = if ($stop) { '故障' } else { '警告' }
                $state = if ($stop) { StateFail '意外中断对话' } else { StateFail '工具调用失败' }
                $info = if ($why) { $why } elseif ($code -ne 0) { "退出码 $code" } else { '' }
                $kind = if ($stop) { 'interrupt' } else { 'tool-fail' }
                Show-Effect "-s $sev 8 1 1 -border on 60 30 1 -lable on 26 `"$(Notice $lead $state (InfoOf @($subTag, $info)))`"" $kind
            }
        }
        'Stop' {
            # 本轮 Stop 若正是这个 hook 自己引发的，必须静默——否则 Stop→xa→Stop 无限循环
            if (-not $evt.stop_hook_active) {
                Show-Effect "-s info 5 1 1 -border on 40 20 1 -lable on 22 `"$(Notice '回复' (StateDone '对话回合结束') $subTag)`"" 'done'
            }
        }
    }
} catch {
    # 提醒失败只留日志，不惊动对话
    try { "ERROR $(Get-Date -Format 'HH:mm:ss') $($_.Exception.Message)" | Add-Content -Path $log -Encoding UTF8 } catch { }
}

exit 0
