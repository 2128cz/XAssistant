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
Check '其余 2 个事件也挂上了' (@($j.PSObject.Properties.Name).Count -eq 3) "实际 $(@($j.PSObject.Properties.Name).Count)"

Write-Host "`n== 5. 拆卸 =="
Run-Installer @('-Platform', 'qoder', '-Root', $q, '-Remove') | Out-Null
Check '纯我们装的文件被整份删掉' (-not (Test-Path (Join-Path $q 'settings.json')))
Run-Installer @('-Platform', 'trae', '-Root', $flat, '-Remove') | Out-Null
$j = Get-Content (Join-Path $flat 'hooks.json') -Raw | ConvertFrom-Json
Check '混合文件保留下来' (Test-Path (Join-Path $flat 'hooks.json'))
Check '只摘掉我们的条目（剩用户那 1 条）' (@($j.Stop).Count -eq 1) "实际 $(@($j.Stop).Count)"
Check '用户的条目原样保留' ((Get-Content (Join-Path $flat 'hooks.json') -Raw) -match 'echo mine')
Check '我们挂的其余事件也摘干净了' (@($j.PSObject.Properties.Name).Count -eq 1)

Write-Host "`n== 6. 未校准的平台默认拒写 =="
$code = Run-Installer @('-Platform', 'cursor', '-Root', (Join-Path $root 'cursor'))
Check '拒绝并以非 0 退出' ($code -ne 0) "退出码 $code"
Check '没写出任何文件' (-not (Test-Path (Join-Path $root 'cursor')))

Write-Host "`n== 7. agent-status.ps1 的事件分诊（假 xa 记录参数）=="
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
    $json | powershell -NoProfile -ExecutionPolicy Bypass -File $status -Platform $plat -LogDir $logDir | Out-Null
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

Fire '{"hook_event_name":"StopFailure","error_type":"ratelimit","cwd":"D:\\repo"}' | Out-Null
Check 'StopFailure（限流/配额打断）→ 红档 + 中断 + 错误型' (((LastEffect) -match '-s error') -and ((LastEffect) -match '中断') -and ((LastEffect) -match 'ratelimit')) (LastEffect)

Fire '{"hook_event_name":"Notification","notification_' | Out-Null
Check '截断的事件 JSON → 红档「事件数据不完整」而非静默吞' (((LastEffect) -match '-s error') -and ((LastEffect) -match '事件数据不完整')) (LastEffect)

Fire '{"hook_event_name":"PostToolUseFailure","error":"boom","cwd":"D:\\repo"}' | Out-Null
$e = LastEffect
Check 'PostToolUseFailure → 红档 + 故障 + 错误摘要' (($e -match '-s error') -and ($e -match '故障') -and ($e -match 'boom')) $e

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

Fire '{"hook_event_name":"Stop","stop_hook_active":true}' 0 | Out-Null
Check 'stop_hook_active 的 Stop 静默（防死循环）' ((LineCount) -eq 0) "写到 $(LineCount) 行"

Check '空 stdin 不报错' ((Fire '' 0) -eq 0)
Check '坏 JSON 也不阻断（永远 exit 0）' ((Fire 'not json at all' 0) -eq 0)
Check '记了原始事件日志' (Test-Path (Join-Path $logDir 'agent-status.log'))

Remove-Item Env:\XASSISTANT_XA -ErrorAction SilentlyContinue
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n通过 $pass 项，失败 $fail 项。" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
