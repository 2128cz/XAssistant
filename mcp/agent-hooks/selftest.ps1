# selftest.ps1 —— agent-hooks 的离屏自测：不碰真实 IDE 配置，全部在临时目录里跑
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File selftest.ps1
#
# 覆盖：装（两种落地形态）、幂等、flat 自适应与「不删用户自己的条目」、
# 拆（纯我们的文件整份删、混合文件只摘我们的）、未校准平台拒写、以及
# agent-status.ps1 的事件分诊（颜色档 / 文案 / stop_hook_active 静默 / 永远 exit 0）。
# 断言失败即以非 0 退出。

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP 'xassistant-agent-hooks-selftest'
$installer = Join-Path $PSScriptRoot 'install-agent-hooks.ps1'
$status = Join-Path $PSScriptRoot 'agent-status.ps1'
$dshPluginTest = Join-Path $PSScriptRoot 'dsh-status-plugin.selftest.mjs'
$fail = 0
$pass = 0

function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "  [OK]   $what" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  [FAIL] $what$(if ($detail) { " —— $detail" })" -ForegroundColor Red }
}

function Run-Installer([string[]]$a) {
    # 安装器对未校准的平台是 throw + 非 0 退出，它的 stderr 会被 PowerShell 记成错误；
    # 这里只要退出码，所以临时放宽错误偏好，别让自测被打断。
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $installer @a 2>&1 | Out-Null
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
}

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $root | Out-Null

Write-Host "`n== 1. settings-hooks 形态（Qoder / Claude Code）=="
$q = Join-Path $root 'qoder'
Run-Installer @('-Platform', 'qoder', '-Root', $q) | Out-Null
$j = Get-Content (Join-Path $q 'settings.json') -Raw | ConvertFrom-Json
Check '装了 5 个事件' (@($j.hooks.PSObject.Properties.Name).Count -eq 5) "实际 $(@($j.hooks.PSObject.Properties.Name).Count)"
Check 'hooks 挂在 hooks 节点下' ($null -ne $j.hooks.Stop)
Check '挂了 StopFailure（API 打断通道）' ($null -ne $j.hooks.StopFailure)
Check '指向 agent-status.ps1' ((Get-Content (Join-Path $q 'settings.json') -Raw) -match 'agent-status\.ps1')
$bom = [System.IO.File]::ReadAllBytes((Join-Path $q 'hooks\agent-status.ps1'))[0..2] | ForEach-Object { $_.ToString('X2') }
Check '落盘脚本带 UTF8-BOM' (($bom -join ' ') -eq 'EF BB BF') ($bom -join ' ')

Write-Host "`n== 2. 幂等：重复安装不重复挂 =="
Run-Installer @('-Platform', 'qoder', '-Root', $q) | Out-Null
$j = Get-Content (Join-Path $q 'settings.json') -Raw | ConvertFrom-Json
Check 'Stop 仍只有 1 条' (@($j.hooks.Stop).Count -eq 1) "实际 $(@($j.hooks.Stop).Count)"

Write-Host "`n== 3. hooks-file 形态（Trae：独立 hooks.json，只挂它真实存在的 3 个事件）=="
$t = Join-Path $root 'trae'
Run-Installer @('-Platform', 'trae', '-Root', $t) | Out-Null
$j = Get-Content (Join-Path $t 'hooks.json') -Raw | ConvertFrom-Json
Check '写在独立 hooks.json 里' ($null -ne $j.hooks)
Check '带官方要求的 version: 1 字段' ([int]$j.version -eq 1) "实得 $($j.version)"
Check '装了 3 个事件' (@($j.hooks.PSObject.Properties.Name).Count -eq 3) "实际 $(@($j.hooks.PSObject.Properties.Name).Count)"
Check '挂了 PostToolUse（Trae 的报错通道）' ($null -ne $j.hooks.PostToolUse)
Check '没挂 Trae 不存在的 PermissionRequest' ($null -eq $j.hooks.PermissionRequest)

Write-Host "`n== 4. flat 自适应：顶层直接是事件名，且用户自己的条目必须留着 =="
$flat = Join-Path $root 'flat'
New-Item -ItemType Directory -Force -Path $flat | Out-Null
'{ "Stop": [ { "hooks": [ { "type": "command", "command": "echo mine" } ] } ] }' |
    Set-Content (Join-Path $flat 'hooks.json') -Encoding UTF8
Run-Installer @('-Platform', 'trae', '-Root', $flat) | Out-Null
$j = Get-Content (Join-Path $flat 'hooks.json') -Raw | ConvertFrom-Json
Check '沿用 flat 布局（没有多出一层 hooks）' ($null -eq $j.hooks)
Check '用户自己的 Stop 条目还在' ((Get-Content (Join-Path $flat 'hooks.json') -Raw) -match 'echo mine')
Check 'Stop 现在是 2 条（用户的 + 我们的）' (@($j.Stop).Count -eq 2) "实际 $(@($j.Stop).Count)"
Check '其余 2 个事件也挂上了' (@($j.PSObject.Properties.Name | Where-Object { $_ -ne 'version' }).Count -eq 3) "实得 $(@($j.PSObject.Properties.Name | Where-Object { $_ -ne 'version' }).Count)"

Write-Host "`n== 5. 拆卸 =="
Run-Installer @('-Platform', 'qoder', '-Root', $q, '-Remove') | Out-Null
Check '纯我们装的文件被整份删掉' (-not (Test-Path (Join-Path $q 'settings.json')))
Run-Installer @('-Platform', 'trae', '-Root', $flat, '-Remove') | Out-Null
$j = Get-Content (Join-Path $flat 'hooks.json') -Raw | ConvertFrom-Json
Check '混合文件保留下来' (Test-Path (Join-Path $flat 'hooks.json'))
Check '只摘掉我们的条目（剩用户那 1 条）' (@($j.Stop).Count -eq 1) "实际 $(@($j.Stop).Count)"
Check '用户的条目原样保留' ((Get-Content (Join-Path $flat 'hooks.json') -Raw) -match 'echo mine')
Check '我们挂的其余事件也摘干净了' (@($j.PSObject.Properties.Name | Where-Object { $_ -notin 'version', 'hooks' }).Count -eq 1) "实得 $(@($j.PSObject.Properties.Name | Where-Object { $_ -notin 'version', 'hooks' }).Count)"

Write-Host "`n== 6. DSH 原生 Cordis 插件：保留用户 YAML、幂等安装与拆卸 =="
$dsh = Join-Path $root 'dsh-home'
$dshProfile = Join-Path $dsh 'profiles\web'
New-Item -ItemType Directory -Force -Path $dshProfile | Out-Null
@'
- id: user-row
  disabled: true
'@ | Set-Content (Join-Path $dshProfile 'cordis.patch.yml') -Encoding UTF8
Run-Installer @('-Platform', 'dsh', '-Root', $dsh, '-Profile', 'web') | Out-Null
$dshPatch = Get-Content (Join-Path $dshProfile 'cordis.patch.yml') -Raw
Check 'DSH patch 保留用户原有条目' ($dshPatch -match 'id: user-row')
Check 'DSH patch 装入带标记的原生插件行' (($dshPatch -match 'xassistant-agent-hooks:dsh begin') -and ($dshPatch -match 'id: xassistant-dsh-status')) $dshPatch
Check 'DSH 插件与状态脚本复制到 profile' ((Test-Path (Join-Path $dshProfile 'xassistant-agent-hooks\dsh-status-plugin.mjs')) -and (Test-Path (Join-Path $dshProfile 'xassistant-agent-hooks\agent-status.ps1')))
Run-Installer @('-Platform', 'dsh', '-Root', $dsh, '-Profile', 'web') | Out-Null
$dshPatch = Get-Content (Join-Path $dshProfile 'cordis.patch.yml') -Raw
Check 'DSH 重复安装只有一个托管块' (([regex]::Matches($dshPatch, 'xassistant-agent-hooks:dsh begin')).Count -eq 1)
Run-Installer @('-Platform', 'dsh', '-Root', $dsh, '-Profile', 'web', '-Remove') | Out-Null
$dshPatch = Get-Content (Join-Path $dshProfile 'cordis.patch.yml') -Raw
Check 'DSH 拆卸只摘托管块并保留用户 YAML' (($dshPatch -match 'id: user-row') -and ($dshPatch -notmatch 'xassistant-dsh-status')) $dshPatch
Check 'DSH 拆卸删除复制的插件文件' (-not (Test-Path (Join-Path $dshProfile 'xassistant-agent-hooks\dsh-status-plugin.mjs')))

Write-Host "`n== 7. 未校准的平台默认拒写 =="
$code = Run-Installer @('-Platform', 'cursor', '-Root', (Join-Path $root 'cursor'))
Check '拒绝并以非 0 退出' ($code -ne 0) "退出码 $code"
Check '没写出任何文件' (-not (Test-Path (Join-Path $root 'cursor')))

Write-Host "`n== 8. agent-status.ps1 的事件分诊（假 xa 记录参数）=="
$logDir = Join-Path $root 'statuslog'
$fake = Join-Path $root 'fake-xa.cmd'
$fakeLog = Join-Path $root 'fake-xa.log'
'@echo off', "echo %* >> `"$fakeLog`"" | Set-Content $fake -Encoding ASCII
$env:XASSISTANT_XA = $fake

function LineCount {
    if (Test-Path $fakeLog) { return @(Get-Content $fakeLog).Count }
    return 0
}
function LastEffect {
    $n = LineCount
    if ($n -eq 0) { return '' }
    return (@(Get-Content $fakeLog))[$n - 1]
}
# xa 是 Start-Process 异步拉起的，cmd 冷启动要几百毫秒才落盘。
# 只等「行数变多」会被上一条迟到的写入骗过（提前放行，读到上一条的效果），
# 所以每条都先等上一批彻底安静下来，再清空日志，只认本条自己写出来的行。
function Wait-Quiet([int]$quietMs = 400, [int]$maxMs = 6000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $last = -1; $stableAt = 0
    while ($sw.ElapsedMilliseconds -lt $maxMs) {
        $n = LineCount
        if ($n -ne $last) { $last = $n; $stableAt = $sw.ElapsedMilliseconds }
        elseif (($sw.ElapsedMilliseconds - $stableAt) -ge $quietMs) { return }
        Start-Sleep -Milliseconds 40
    }
}
function Fire([string]$json, [int]$expect = 1, [string]$plat = 'trae') {
    Wait-Quiet
    Remove-Item $fakeLog -Force -ErrorAction SilentlyContinue
    # 管道给原生命令默认走 $OutputEncoding（控制台码页 GBK）：脚本现在按 UTF-8 直读 stdin，
    # 这里必须同步喂 UTF-8 字节，否则中文事件在自测里反而是坏的
    $prevOut = $global:OutputEncoding
    $global:OutputEncoding = New-Object System.Text.UTF8Encoding $false
    try { $json | powershell -NoProfile -ExecutionPolicy Bypass -File $status -Platform $plat -LogDir $logDir | Out-Null }
    finally { $global:OutputEncoding = $prevOut }
    $code = $LASTEXITCODE
    if ($expect -gt 0) {
        $deadline = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $deadline) {
            if ((LineCount) -ge $expect) { break }
            Start-Sleep -Milliseconds 40
        }
    } else {
        # 判「静默」：给足它本该写入的时间，仍然一行没有才算数
        Start-Sleep -Milliseconds 1500
    }
    return $code
}

# ---- 四段机械文案的结构性判据：机械回复 · 现况 · 来源 · 信息 -------------------------------
# 只匹配关键字在是假门：正文里出现「故障」两个字不代表领词就是它。这里直接按 ' · ' 拆段，
# 逐段问「第 N 段是不是它」，并钉住领词只能来自固定词表（别的系统按领词分流，现场造词就断了）。
$Leads = '提醒', '警告', '故障', '询问', '回复'
$States = '工具调用失败', '意外中断对话', '对话回合结束', '请求人类介入', '等待授权',
         '子代理失败', '子代理成功', '事件数据不完整', '运行告警'

function BodyText([string]$line) {
    $m = [regex]::Match($line, '-lable on \d+ "([^"]*)"')
    return $m.Groups[1].Value
}
function BodyParts([string]$line) { @((BodyText $line) -split ' · ') }

# 断言第 i 段（从 0 数）正好等于给定值；缺段/错段都把整行吐出来好定位
function CheckPart([string]$label, [string]$line, [int]$at, [string]$want) {
    $parts = BodyParts $line
    $got = if ($at -lt $parts.Count) { $parts[$at] } else { '<缺段>' }
    Check ("$label 第 $($at + 1) 段＝$want") ($got -eq $want) "实得「$got」  整行：$line"
}
function CheckShape([string]$label, [string]$line, [string]$lead, [string]$state, [int]$count) {
    $parts = BodyParts $line
    Check ("$label 四段结构齐（实得 $($parts.Count) 段）") ($parts.Count -eq $count) "整行：$line"
    Check ("$label 领词在固定词表里") ($Leads -contains $parts[0]) "实得「$($parts[0])」  整行：$line"
    Check ("$label 现况在固定词表里") ($States -contains $parts[1]) "实得「$($parts[1])」  整行：$line"
    CheckPart $label $line 0 $lead
    CheckPart $label $line 1 $state
}

Check 'PermissionRequest 正常退出' ((Fire '{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo"}') -eq 0)
$e = LastEffect
# 四段：领词固定是「询问」，工具名不再挤进领词（旧写法「授权(Bash)」），来源段这里退回项目目录名
CheckShape 'PermissionRequest' $e '询问' '等待授权' 4
CheckPart 'PermissionRequest' $e 2 'repo'
CheckPart 'PermissionRequest' $e 3 'Bash'
Check 'PermissionRequest → 黄档' ($e -match '-s warn') $e
Check '命令行带 -from 来源标记（消息栈徽章靠它）' ($e -match '-from trae') $e

Fire '{"hook_event_name":"Warning","warning_type":"token-limit","message":"输出达到 token 上限","cwd":"D:\\repo","session_title":"检查 DSH 通知"}' 1 'dsh' | Out-Null
$e = LastEffect
CheckShape 'DSH Warning' $e '警告' '运行告警' 4
CheckPart 'DSH Warning 来源段用会话标题而不是项目名' $e 2 '“检查 DSH 通知”'
CheckPart 'DSH Warning' $e 3 '输出达到 token 上限'
Check 'DSH Warning → 黄档' ($e -match '-s warn') $e
Check 'DSH 命令带 -from dsh（图标徽章）' ($e -match '-from dsh') $e

Fire '{"hook_event_name":"StopFailure","error_type":"ratelimit","cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
Check 'StopFailure（限流/配额打断）→ 红档' ($e -match '-s error') $e
CheckShape 'StopFailure' $e '故障' '意外中断对话' 4
CheckPart 'StopFailure 错误型进信息段' $e 3 'ratelimit'

Fire '{"hook_event_name":"Notification","notification_' | Out-Null
$e = LastEffect
Check '截断的事件 JSON → 红档「事件数据不完整」而非静默吞' (($e -match '-s error') -and ((BodyText $e) -match '事件数据不完整')) $e
# 这条事件没有 cwd 也没有标题 → 来源段整段省掉，不能凭空凑一个（信息段仍带着截断原因）
CheckShape '截断 JSON（无来源可报）' $e '提醒' '事件数据不完整' 3
CheckPart '截断 JSON 的截断原因在信息段' $e 2 '数据截断(Notification)'

Fire '{"hook_event_name":"PostToolUseFailure","error":"boom","cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
# 工具挂了但对话还在跑 → 黄档「警告」（旧版一律红，和「对话被打断」分不开）
Check 'PostToolUseFailure → 黄档（不再与对话中断同色）' ($e -match '-s warn') $e
CheckShape 'PostToolUseFailure' $e '警告' '工具调用失败' 4
CheckPart 'PostToolUseFailure 错误摘要进信息段' $e 3 'boom'

# 真机回归：子进程按 GBK 写 stderr、上层按 UTF-8 解码时，错误原文只剩 U+FFFD 这类不可逆替换符。
# 这种文本读不懂也晒不得——丢掉整段，退回工具名，卡片上不再成串出现乱码。
$garbled = 'Out-File : ' + [string][char]0xFFFD + [char]0xFFFD + [char]0xFFFD + ' '
Fire ('{"hook_event_name":"PostToolUseFailure","error":"' + $garbled + '","tool_name":"Bash","cwd":"D:\\repo"}') | Out-Null
$e = LastEffect
CheckShape '乱码错误' $e '警告' '工具调用失败' 4
CheckPart '乱码错误 → 信息段退回工具名，不晒替换符' $e 3 'Bash'
Check '乱码不上屏（正文里没有 Out-File）' ($e -notmatch 'Out-File') $e

# 身份决定现况措辞：Qoder 事件带 agent_id 就是子实例，现况换成「子代理失败 / 子代理成功」，
# 名字与「派下去干什么」进信息段（档案落在会话目录 subagents/agent-<id>.meta.json，与同名 jsonl 平级）；
# 没带字段的主对话用另一套词——两类消息一眼分得清该回哪一路。
$projDir = Join-Path $root 'projects\g--demo'
New-Item -ItemType Directory -Force -Path (Join-Path $projDir 'sess-1\subagents') | Out-Null
'{"agentType":"Explore","toolUseId":"call_1","description":"盘点前端数据需求","invocationName":"Explore","color":"cyan"}' |
    Set-Content (Join-Path $projDir 'sess-1\subagents\agent-aExplore-123.meta.json') -Encoding UTF8
$tp = ($projDir -replace '\\', '\\') + '\\sess-1.jsonl'
$agentJson = '"agent_id":"aExplore-123","agent_type":"Explore","transcript_path":"' + $tp + '"'
Fire ('{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo",' + $agentJson + '}') 1 'qoder' | Out-Null
$e = LastEffect
CheckShape '子代理问话' $e '询问' '等待授权' 4
CheckPart '子代理 → 名字与干什么都在信息段' $e 3 '子代理 Explore：盘点前端数据需求 / Bash'
Fire ('{"hook_event_name":"Stop","cwd":"D:\\repo","last_assistant_message":"跑完了",' + $agentJson + '}') 1 'qoder' | Out-Null
$e = LastEffect
CheckShape '子代理跑完' $e '回复' '子代理成功' 4
Check '子代理成功不误写成对话回合结束' ($e -notmatch '对话回合结束') $e
Check '子代理完成卡不晒回复原文' ($e -notmatch '跑完了') $e
Fire ('{"hook_event_name":"PostToolUseFailure","error":"boom","cwd":"D:\\repo",' + $agentJson + '}') 1 'qoder' | Out-Null
$e = LastEffect
CheckShape '子代理挂工具' $e '警告' '子代理失败' 4
Check '子代理失败与主对话的工具调用失败分得开' ($e -notmatch '工具调用失败') $e

Fire ('{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo"}') 1 'qoder' | Out-Null
$e = LastEffect
Check '主对话 → 现况走主对话那套词、不挂子代理' (($e -match '等待授权') -and ($e -notmatch '子代理')) $e
Fire ('{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo","agent_id":"aMissing-999","agent_type":"general-purpose"}') 1 'qoder' | Out-Null
$e = LastEffect
CheckShape '档案读不到' $e '询问' '等待授权' 4
CheckPart '档案读不到 → 退回 agent_type，不崩' $e 3 '子代理 general-purpose / Bash'

Fire '{"hook_event_name":"PostToolUseFailure","is_interrupt":true,"cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
Check '人被中断 → 红档' ($e -match '-s error') $e
CheckShape '人被中断（与工具自己挂了分开）' $e '故障' '意外中断对话' 3
CheckPart '人被中断的来源段仍是项目名' $e 2 'repo'

# 提问通知：真正的问句在 details.input.questions[].question，message 只是「requires confirmation」
# 这种确认 boilerplate——要人回的是问句，所以问句优先，boilerplate 不许上屏
Fire '{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Tool AskUserQuestion requires confirmation","details":{"toolName":"AskUserQuestion","input":{"questions":[{"question":"先合 dev 还是先出补丁？"}]}},"cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
CheckShape 'Notification(提问) 四段齐' $e '询问' '请求人类介入' 4
CheckPart '提问的问题原文进信息段' $e 3 '先合 dev 还是先出补丁？'
Check '确认 boilerplate 不上屏' ($e -notmatch 'requires confirmation') $e

Fire '{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Tool AskUserQuestion requires confirmation","details":{"input":{"questions":[{"question":"第一个问题"},{"question":"第二个问题"}]}}}' | Out-Null
CheckPart '一次问好几句：只展首问并标注总数' (LastEffect) 2 '第一个问题 / 共 2 问'

# 首问本身超长按 60 截断时，「共 N 问」不能被截掉（实测真事件：截完只剩「共 2 …」，等于没告诉人要答几句）
$longQ = ('先' * 58)
Fire ('{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Tool AskUserQuestion requires confirmation","details":{"input":{"questions":[{"question":"' + $longQ + '"},{"question":"第二问"}]}}}') | Out-Null
$e = LastEffect
Check '首问过长时「共 N 问」仍在结尾' ((BodyParts $e)[-1] -match '共 2 问$') $e

# 授权请求的对象如果是提问工具，这条其实是「等我回话」，现况与问句都要跟着变
Fire '{"hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"这一步要不要删掉？"}]}}' | Out-Null
$e = LastEffect
CheckShape '提问工具的授权 → 现况是请求人类介入' $e '询问' '请求人类介入' 3
CheckPart '提问工具的授权也带问题原文' $e 2 '这一步要不要删掉？'
Fire '{"hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"ls"}}' | Out-Null
CheckShape '普通工具的授权仍是等待授权 + 工具名' (LastEffect) '询问' '等待授权' 3

Fire '{"hook_event_name":"Notification","notification_type":"permission_prompt","title":"AskUserQuestion"}' | Out-Null
CheckShape '提问但没给问句时退回标题，不空段' (LastEffect) '询问' '请求人类介入' 3
Fire '{"hook_event_name":"Notification","notification_type":"permission_prompt","title":"other"}' | Out-Null
CheckShape 'Notification(接管)' (LastEffect) '询问' '请求人类介入' 3

# Trae 官方只发 idle_prompt（等待确认与任务完成都是它）：按平台语义分流
Fire '{"hook_event_name":"Notification","notification_type":"idle_prompt","message":"智能体已完成任务"}' 0 | Out-Null
Check 'Trae 的 idle_prompt 静默（Stop 已报完成，不重复）' ((LineCount) -eq 0) "写到 $(LineCount) 行"
Fire '{"hook_event_name":"Notification","notification_type":"idle_prompt"}' 1 'claude' | Out-Null
CheckShape 'Claude 的 idle_prompt（空闲等人输入）' (LastEffect) '询问' '请求人类介入' 2
Fire '{"hook_event_name":"Notification","notification_type":"weird_type"}' | Out-Null
CheckShape '未知通知类型 → 宁可多报不漏' (LastEffect) '询问' '请求人类介入' 2
Fire '{"hook_event_name":"Notification","notification_type":"auth_success"}' 0 | Out-Null
Check 'auth_success 不打扰' ((LineCount) -eq 0) "写到 $(LineCount) 行"

# Trae 没有 PostToolUseFailure：报错从 PostToolUse 的结果字段判
Fire '{"hook_event_name":"PostToolUse","tool_name":"Shell","exit_code":1,"cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
CheckShape 'PostToolUse 退出码非 0' $e '警告' '工具调用失败' 4
CheckPart 'PostToolUse 退出码进信息段' $e 3 '退出码 1'
Check 'PostToolUse 退出码非 0 → 黄档（对话还在跑）' ($e -match '-s warn') $e
Fire '{"hook_event_name":"PostToolUse","tool_response":{"is_error":true,"content":"boom-ish"}}' | Out-Null
$e = LastEffect
CheckShape 'PostToolUse is_error' $e '警告' '工具调用失败' 3
CheckPart 'PostToolUse 摘要进信息段' $e 2 'boom-ish'
Fire '{"hook_event_name":"PostToolUse","tool_response":{"content":"all good"}}' 0 | Out-Null
Check 'PostToolUse 结果正常 → 静默' ((LineCount) -eq 0) "写到 $(LineCount) 行"

Fire '{"hook_event_name":"Stop","cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
Check 'Stop → info 档' ($e -match '-s info') $e
CheckShape 'Stop' $e '回复' '对话回合结束' 3
CheckPart 'Stop 的来源段' $e 2 'repo'

# 真实回归：Qoder 的 Stop 带中文 last_assistant_message，GBK/UTF-8 错配时解析挂→误报红档；修好后必须走 info，
# 且正文恰好三段（机械回复·现况·来源）——回复原文一个字都不许上屏
Fire '{"hook_event_name":"Stop","cwd":"D:\\repo","last_assistant_message":"没有需要提交的内容，徽章已全部上屏。"}' | Out-Null
$e = LastEffect
CheckShape '含中文长回复的 Stop' $e '回复' '对话回合结束' 3
Check '不晒回复原文，也没有误判成数据不完整' (($e -notmatch '没有需要提交') -and ($e -notmatch '数据不完整')) $e

Fire '{"hook_event_name":"Stop","stop_hook_active":true}' 0 | Out-Null
Check 'stop_hook_active 的 Stop 静默（防死循环）' ((LineCount) -eq 0) "写到 $(LineCount) 行"

# 真机回归（2026-09-29）：Qoder 的 parent_business_info.name 装的是**用户自己打的那句话被截断**
# （本机回放 2056 条真事件看到的："要，继续"、"unity我关了，你"、"优化spline的e"），不是任务名。
# 它一旦进来源，屏幕上就变回「拿对话内容当消息」——用户明确禁止过的那件事。
Fire '{"hook_event_name":"Stop","cwd":"D:\\repo","parent_business_info":{"name":"要，继续"},"last_assistant_message":"进度条做完了"}' | Out-Null
$e = LastEffect
Check '来源段不许用 parent_business_info.name（那是用户自己的话）' ($e -notmatch '要，继续') $e
Check '正文里也不许出现回复原文' ($e -notmatch '进度条做完了') $e
CheckPart '没有任务名时来源退回项目目录名' $e 2 'repo'

Check '空 stdin 不报错' ((Fire '' 0) -eq 0)
Check '坏 JSON 也不阻断（永远 exit 0）' ((Fire 'not json at all' 0) -eq 0)
Check '记了原始事件日志' (Test-Path (Join-Path $logDir 'agent-status.log'))

# ---- 每条消息都挂三段标签：来源,身份,类型（面板的按类型闸门与 xa -k -tag 都读它）----
# 类型词表固定六个，与 C# 那份（Services/MessageGate.Kinds）必须一致：面板按它渲染开关，
# 多一个词就有一个永远关不掉的开关，少一个词就有一类消息永远关不掉。
$Kinds = 'ask', 'done', 'tool-fail', 'interrupt', 'error', 'notice'
function TagOf([string]$line) {
    $m = [regex]::Match($line, '-tag (\S+)')
    return $m.Groups[1].Value
}
function CheckTag([string]$label, [string]$line, [string]$want) {
    $got = TagOf $line
    Check "$label 三段标签＝$want" ($got -eq $want) "实得「$got」  整行：$line"
}

Fire '{"hook_event_name":"Stop","cwd":"D:\\repo"}' 1 'qoder' | Out-Null
CheckTag 'Stop' (LastEffect) 'qoder,main,done'
Fire '{"hook_event_name":"PostToolUseFailure","tool_name":"Bash","error":"code = 1"}' 1 'qoder' | Out-Null
CheckTag '工具失败' (LastEffect) 'qoder,main,tool-fail'
Fire '{"hook_event_name":"PostToolUseFailure","tool_name":"Bash","is_interrupt":true}' 1 'qoder' | Out-Null
CheckTag '被中断的工具失败算整轮断了' (LastEffect) 'qoder,main,interrupt'
Fire '{"hook_event_name":"StopFailure","error_type":"rate_limit"}' 1 'qoder' | Out-Null
CheckTag '整轮被打断' (LastEffect) 'qoder,main,interrupt'
Fire '{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Tool AskUserQuestion requires confirmation"}' 1 'qoder' | Out-Null
CheckTag '提问' (LastEffect) 'qoder,main,ask'
Fire '{"hook_event_name":"PermissionRequest","tool_name":"Bash"}' 1 'qoder' | Out-Null
CheckTag '等授权也算等人' (LastEffect) 'qoder,main,ask'
Fire '{"hook_event_name":"Warning","message":"token limit"}' 1 'dsh' | Out-Null
CheckTag '运行告警（来源换成 dsh）' (LastEffect) 'dsh,main,error'
Fire '{"hook_event_name":"Stop","cwd":"D:\\repo","agent_id":"agent-abc","agent_type":"Explore"}' 1 'qoder' | Out-Null
CheckTag '子实例的身份段写 subagent' (LastEffect) 'qoder,subagent,done'
Fire '{"hook_event_name":"Notification","notification_type":"idle_prompt"}' 1 'claude' | Out-Null
CheckTag '别的平台照写自己的来源词' (LastEffect) 'claude,main,ask'
Check '标签第三段都在词表里（多写一个词＝面板上多一个永远关不掉的开关）' `
    ((@('qoder,main,done', 'qoder,main,tool-fail', 'qoder,main,interrupt', 'qoder,main,ask', 'dsh,main,error') |
        ForEach-Object { $Kinds -contains ($_ -split ',')[2] } | Where-Object { -not $_ }).Count -eq 0)

$nodeOutput = & node $dshPluginTest 2>&1
Check 'DSH 原生插件事件映射自测通过' ($LASTEXITCODE -eq 0) ($nodeOutput -join ' ')

Remove-Item Env:\XASSISTANT_XA -ErrorAction SilentlyContinue
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n通过 $pass 项，失败 $fail 项。" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
