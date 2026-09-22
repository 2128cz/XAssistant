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

Check 'PermissionRequest 正常退出' ((Fire '{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo"}') -eq 0)
$e = LastEffect
Check 'PermissionRequest → 黄档 + 授权(工具) + 项目名' (($e -match '-s warn') -and ($e -match '授权\(Bash\)') -and ($e -match 'repo')) $e
Check '命令行带 -from 来源标记（消息栈徽章靠它）' ($e -match '-from trae') $e

Fire '{"hook_event_name":"Warning","warning_type":"token-limit","message":"输出达到 token 上限","cwd":"D:\\repo","session_title":"检查 DSH 通知"}' 1 'dsh' | Out-Null
$e = LastEffect
Check 'DSH Warning → 黄档 + 警告 + 会话标题' (($e -match '-s warn') -and ($e -match '警告') -and ($e -match '检查 DSH 通知')) $e
Check 'DSH 命令带 -from dsh（图标徽章）' ($e -match '-from dsh') $e

Fire '{"hook_event_name":"StopFailure","error_type":"ratelimit","cwd":"D:\\repo"}' | Out-Null
Check 'StopFailure（限流/配额打断）→ 红档 + 中断 + 错误型' (((LastEffect) -match '-s error') -and ((LastEffect) -match '中断') -and ((LastEffect) -match 'ratelimit')) (LastEffect)

Fire '{"hook_event_name":"Notification","notification_' | Out-Null
Check '截断的事件 JSON → 红档「事件数据不完整」而非静默吞' (((LastEffect) -match '-s error') -and ((LastEffect) -match '事件数据不完整')) (LastEffect)

Fire '{"hook_event_name":"PostToolUseFailure","error":"boom","cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
Check 'PostToolUseFailure → 红档 + 故障 + 错误摘要' (($e -match '-s error') -and ($e -match '故障') -and ($e -match 'boom')) $e

# 真机回归：子进程按 GBK 写 stderr、上层按 UTF-8 解码时，错误原文只剩 U+FFFD 这类不可逆替换符。
# 这种文本读不懂也晒不得——丢掉整段，退回工具名，卡片上不再成串出现乱码。
$garbled = 'Out-File : ' + [string][char]0xFFFD + [char]0xFFFD + [char]0xFFFD + ' '
Fire ('{"hook_event_name":"PostToolUseFailure","error":"' + $garbled + '","tool_name":"Bash","cwd":"D:\\repo"}') | Out-Null
$e = LastEffect
Check '乱码错误 → 退回工具名，不晒替换符' (($e -match 'Bash') -and ($e -notmatch 'Out-File')) $e

# 主对话 / 子代理：Qoder 事件带 agent_id 就是子代理，档案（IDE 落在会话目录 subagents/agent-<id>.meta.json，
# 该目录与同名 jsonl 平级）给 invocationName 与 description；没带字段的主对话必须明说「主对话」，
# 否则并行跑子代理时两张卡分不出该回哪一路
$projDir = Join-Path $root 'projects\g--demo'
New-Item -ItemType Directory -Force -Path (Join-Path $projDir 'sess-1\subagents') | Out-Null
'{"agentType":"Explore","toolUseId":"call_1","description":"盘点前端数据需求","invocationName":"Explore","color":"cyan"}' |
    Set-Content (Join-Path $projDir 'sess-1\subagents\agent-aExplore-123.meta.json') -Encoding UTF8
$tp = ($projDir -replace '\\', '\\') + '\\sess-1.jsonl'
Fire ('{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo","agent_id":"aExplore-123","agent_type":"Explore","transcript_path":"' + $tp + '"}') 1 'qoder' | Out-Null
$e = LastEffect
Check '子代理 → 标出子代理与调用名' (($e -match '子代理 Explore') -and ($e -match '盘点前端数据需求')) $e
Check '子代理事件不误标主对话' (-not ($e -match '主对话')) $e
Fire ('{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo"}') 1 'qoder' | Out-Null
Check '主对话 → 明标主对话' ((LastEffect) -match '主对话') (LastEffect)
Fire ('{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo","agent_id":"aMissing-999","agent_type":"general-purpose"}') 1 'qoder' | Out-Null
$e = LastEffect
Check '档案读不到 → 退回 agent_type，不崩' (($e -match '子代理 general-purpose') -and -not ($e -match '盘点')) $e

Fire '{"hook_event_name":"PostToolUseFailure","is_interrupt":true,"cwd":"D:\\repo"}' | Out-Null
Check '中断的失败 → 文案写「中断」' ((LastEffect) -match '中断') (LastEffect)

Fire '{"hook_event_name":"Notification","notification_type":"permission_prompt","title":"AskUserQuestion"}' | Out-Null
Check 'Notification(提问) → 黄档 + 提问' ((LastEffect) -match '提问') (LastEffect)
Fire '{"hook_event_name":"Notification","notification_type":"permission_prompt","title":"other"}' | Out-Null
Check 'Notification(接管) → 黄档 + 接管' ((LastEffect) -match '接管') (LastEffect)

# Trae 官方只发 idle_prompt（等待确认与任务完成都是它）：按平台语义分流
Fire '{"hook_event_name":"Notification","notification_type":"idle_prompt","message":"智能体已完成任务"}' 0 | Out-Null
Check 'Trae 的 idle_prompt 静默（Stop 已报完成，不重复）' ((LineCount) -eq 0) "写到 $(LineCount) 行"
Fire '{"hook_event_name":"Notification","notification_type":"idle_prompt"}' 1 'claude' | Out-Null
Check 'Claude 的 idle_prompt → 接管黄档（空闲等人输入）' ((LastEffect) -match '接管') (LastEffect)
Fire '{"hook_event_name":"Notification","notification_type":"weird_type"}' | Out-Null
Check '未知通知类型 → 宁可多报不漏接管' ((LastEffect) -match '接管') (LastEffect)
Fire '{"hook_event_name":"Notification","notification_type":"auth_success"}' 0 | Out-Null
Check 'auth_success 不打扰' ((LineCount) -eq 0) "写到 $(LineCount) 行"

# Trae 没有 PostToolUseFailure：报错从 PostToolUse 的结果字段判
Fire '{"hook_event_name":"PostToolUse","tool_name":"Shell","exit_code":1,"cwd":"D:\\repo"}' | Out-Null
Check 'PostToolUse 退出码非 0 → 红档 + 故障' (((LastEffect) -match '-s error') -and ((LastEffect) -match '故障')) (LastEffect)
Fire '{"hook_event_name":"PostToolUse","tool_response":{"is_error":true,"content":"boom-ish"}}' | Out-Null
Check 'PostToolUse is_error → 红档带摘要' (((LastEffect) -match '-s error') -and ((LastEffect) -match 'boom-ish')) (LastEffect)
Fire '{"hook_event_name":"PostToolUse","tool_response":{"content":"all good"}}' 0 | Out-Null
Check 'PostToolUse 结果正常 → 静默' ((LineCount) -eq 0) "写到 $(LineCount) 行"

Fire '{"hook_event_name":"Stop","cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
Check 'Stop → info 档 + 已完成' (($e -match '-s info') -and ($e -match '已完成')) $e

# 真实回归：Qoder 的 Stop 带中文 last_assistant_message，GBK/UTF-8 错配时解析挂→误报红档；修好后必须走 info，
# 且标题只认 vscdb 任务名——回复原文不得被当标题晒上屏
Fire '{"hook_event_name":"Stop","cwd":"D:\\repo","last_assistant_message":"没有需要提交的内容，徽章已全部上屏。"}' | Out-Null
$e = LastEffect
Check '含中文长回复的 Stop → info 档、不晒回复原文' (($e -match '-s info') -and ($e -notmatch '数据不完整') -and ($e -notmatch '没有需要提交')) $e

Fire '{"hook_event_name":"Stop","stop_hook_active":true}' 0 | Out-Null
Check 'stop_hook_active 的 Stop 静默（防死循环）' ((LineCount) -eq 0) "写到 $(LineCount) 行"

Check '空 stdin 不报错' ((Fire '' 0) -eq 0)
Check '坏 JSON 也不阻断（永远 exit 0）' ((Fire 'not json at all' 0) -eq 0)
Check '记了原始事件日志' (Test-Path (Join-Path $logDir 'agent-status.log'))

$nodeOutput = & node $dshPluginTest 2>&1
Check 'DSH 原生插件事件映射自测通过' ($LASTEXITCODE -eq 0) ($nodeOutput -join ' ')

Remove-Item Env:\XASSISTANT_XA -ErrorAction SilentlyContinue
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n通过 $pass 项，失败 $fail 项。" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
