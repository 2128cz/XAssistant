# agent-status.ps1 —— AI 编码代理的会话状态 → xa 全屏提醒（多平台通用）
#
# 由各平台的 hooks 在事件触发时调用，事件 JSON 从 stdin 传入。
# 各平台（Claude Code / Qoder CN / Trae …）用的都是同一套 PascalCase 事件名与
# 「stdin 收 JSON + exit code 表达决定」协议，分诊逻辑因此完全一致：
# 一份脚本服务所有平台，平台差异只剩日志里那一列与安装器写的配置路径。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File agent-status.ps1 -Platform qoder
#
# 分档（与 qoder-status.ps1 的文案一脉相承，改了会连累已装机的用户习惯）：
#   PostToolUseFailure               红：报错打断，需要人去查看
#   PermissionRequest                黄：等授权，对话卡在人这一侧
#   Notification(permission_prompt)  黄：同上（提问 / 接管）
#   Stop                             普通：这轮回复完成（stop_hook_active 时静默，防死循环）
# 铁律：永远 exit 0 —— 提醒脚本再坏也不许把对话流阻断。
param(
    [string]$Platform = 'generic',
    [string]$LogDir = (Join-Path $env:LOCALAPPDATA 'XAssistant\agent-hooks'),
    [switch]$NoEffect          # 自测用：只记日志，不拉 xa
)

$ErrorActionPreference = 'Continue'
$log = Join-Path $LogDir 'agent-status.log'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $evt = $raw | ConvertFrom-Json
    $name = [string]$evt.hook_event_name

    # 留一份原始 JSON：各平台字段名以文档协议为准，跑一轮后按这份日志校准。
    # 前缀里带脚本所在目录名（`Platform@根目录`）：同一平台可能往多个候选位置装过配置，
    # 这一列能直接告诉你是哪个位置的配置被读到、进而被执行的。
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $origin = Split-Path (Split-Path $PSScriptRoot -Parent) -Leaf
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [$Platform@$origin] [$name] $(($raw -replace '[\r\n\t]+', ' ').Trim())" |
        Add-Content -Path $log -Encoding UTF8

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

    # 事件带来的上下文：cwd 叶名是项目，transcript 首条用户消息就是对话标题（流式只读前几行）
    $project = if ($evt.cwd) { Split-Path $evt.cwd -Leaf } else { '' }
    function Get-SessionTitle([string]$path) {
        if (-not $path -or -not (Test-Path $path)) { return '' }
        try {
            foreach ($line in (Get-Content $path -TotalCount 12 -Encoding UTF8)) {
                $o = $line | ConvertFrom-Json
                if ($o.type -eq 'user' -and $o.message.content -is [string]) {
                    return (Cut ([string]$o.message.content) 24)   # 标题只取一小截，认得出是哪场对话就够
                }
            }
        } catch { }
        return ''
    }
    $dialog = Get-SessionTitle ([string]$evt.transcript_path)

    # 文案格式固定为「事件词 · 请求人类介入：项目 · “对话标题” · 细节」，缺哪段省哪段
    $who = @()
    if ($project) { $who += $project }
    if ($dialog) { $who += ('“' + $dialog + '”') }
    $ctx = if ($who.Count -gt 0) { '：' + ($who -join ' · ') } else { '' }

    function Show-Effect([string]$argLine) {
        if ($NoEffect) {
            "$(Get-Date -Format 'HH:mm:ss')  [dry-run] $xa $argLine" | Add-Content -Path $log -Encoding UTF8
            return
        }
        # Start-Process 不等 xa：hook 脚本毫秒级交差，动画与消息栈由 XAssistant 自己放
        Start-Process -FilePath $xa -ArgumentList $argLine -WindowStyle Hidden
    }

    switch ($name) {
        'PostToolUseFailure' {
            $why = Cut $evt.error 48
            $lead = if ($evt.is_interrupt) { '中断' } else { '故障' }
            $detail = if ($why) { ' · ' + $why } else { '' }
            Show-Effect "-s error 8 1 1 -border on 60 30 1 -lable on 26 `"$lead · 请求人类介入$ctx$detail`""
        }
        'PermissionRequest' {
            $tool = Cut $evt.tool_name 30
            $head = if ($tool) { "授权($tool)" } else { '授权' }
            Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"$head · 请求人类介入$ctx`""
        }
        'Notification' {
            # title/message 带的是机器码（如 AskUserQuestion），映射到事件词；permission_prompt 才提醒
            if ($evt.notification_type -eq 'permission_prompt') {
                $t = (([string]$evt.title) + ([string]$evt.message))
                if ($t -match 'ask.?user.?question') {
                    Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"提问 · 请求人类介入$ctx`""
                } else {
                    Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"接管 · 请求人类介入$ctx`""
                }
            }
        }
        'Stop' {
            # 本轮 Stop 若正是这个 hook 自己引发的，必须静默——否则 Stop→xa→Stop 无限循环
            if (-not $evt.stop_hook_active) {
                Show-Effect "-s info 5 1 1 -border on 40 20 1 -lable on 22 `"回复 · 已完成$ctx`""
            }
        }
    }
} catch {
    # 提醒失败只留日志，不惊动对话
    try { "ERROR $(Get-Date -Format 'HH:mm:ss') $($_.Exception.Message)" | Add-Content -Path $log -Encoding UTF8 } catch { }
}

exit 0
