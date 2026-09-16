# qoder-status.ps1 —— Qoder CN 对话状态 → xa 全屏提醒（由 IDE Hooks 在事件触发时调用）
# 事件 JSON 从 stdin 传入（Claude Code hooks 协议兼容）。三级分档：
#   PostToolUseFailure        红：报错打断，需要人去查看
#   PermissionRequest         黄：等授权/补充信息，对话卡在人这一侧
#   Notification(permission_prompt)  黄：同上，通知型
#   Stop                      普通：这轮回复完成（stop_hook_active 时跳过，防死循环）
# 铁律：永远 exit 0——提醒脚本再坏也不许把对话流阻断。

$ErrorActionPreference = 'Continue'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $evt = $raw | ConvertFrom-Json
    $name = [string]$evt.hook_event_name

    # 留一份原始 JSON：首版字段名以文档协议为准，真实 IDE 跑一轮后按这份日志校准
    $hookDir = Join-Path $env:USERPROFILE '.qoder-cn\hooks'
    New-Item -ItemType Directory -Force -Path $hookDir | Out-Null
    $log = Join-Path $hookDir 'qoder-status.log'
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [$name] $(($raw -replace '[\r\n\t]+', ' ').Trim())" |
        Add-Content -Path $log -Encoding UTF8

    # 找 xa：注册垫片 → PATH → 仓库 Debug 产物（register-xa.ps1 维护的那条链）
    $xa = Join-Path $env:LOCALAPPDATA 'XAssistant\bin\xa.cmd'
    if (-not (Test-Path $xa)) {
        $cmd = Get-Command xa -ErrorAction SilentlyContinue
        if ($cmd) {
            $xa = $cmd.Source
        } else {
            $xa = Join-Path $PSScriptRoot '..\..\bin\Debug\net8.0-windows\XAssistant.exe'
        }
    }

    # 控制字符与引会打乱命令行，统一清掉；超长截断
    function Clean([string]$s) {
        if ([string]::IsNullOrWhiteSpace($s)) { return '' }
        ($s -replace '[\r\n\t]+', ' ' -replace '["“”]', '').Trim()
    }
    function Cut([string]$s, [int]$n) {
        $s = Clean $s
        if ($s.Length -gt $n) { $s.Substring(0, $n) + '…' } else { $s }
    }
    function Show-Effect([string]$argLine) {
        # Start-Process 不等 xa：hook 脚本毫秒级交差，动画与消息栈由 XAssistant 自己放
        Start-Process -FilePath $xa -ArgumentList $argLine -WindowStyle Hidden
    }

    switch ($name) {
        'PostToolUseFailure' {
            $why = Cut $evt.error 60
            if (-not $why) { $why = Cut $evt.tool_name 40 }
            $tail = if ($evt.is_interrupt) { '（已被打断）' } else { '' }
            Show-Effect "-s error 8 1 1 -border on 60 30 1 -lable on 26 `"[红] 工具执行失败$tail : $why`""
        }
        'PermissionRequest' {
            Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"[黄] 等待授权: $(Cut $evt.tool_name 40)`""
        }
        'Notification' {
            # 通知有多种类型，只有 permission_prompt 意味着「AI 在人这一侧等」
            if ($evt.notification_type -eq 'permission_prompt') {
                $msg = Cut $evt.message 48
                Show-Effect "-s warn 8 1 1 -border on 60 30 1 -lable on 26 `"[黄] AI 等待人工接管 $msg`""
            }
        }
        'Stop' {
            # 本轮 Stop 若正是这个 hook 自己引发的，必须静默——否则 Stop→xa→Stop 无限循环
            if (-not $evt.stop_hook_active) {
                Show-Effect "-s info 5 1 1 -border on 40 20 1 -lable on 22 `"对话完成，可回到 IDE`""
            }
        }
    }
} catch {
    # 提醒失败只留日志，不惊动对话
    try { "ERROR $(Get-Date -Format 'HH:mm:ss') $($_.Exception.Message)" | Add-Content -Path $log -Encoding UTF8 } catch { }
}

exit 0
