<#
install-takeover-hooks.ps1 —— 一键给 Qoder CN 装上「对话状态 → xa 全屏提醒」的 Hooks

    .\install-takeover-hooks.ps1                     # 装（默认目标 ~\.qoder-cn）
    .\install-takeover-hooks.ps1 -Remove             # 拆（只动指向本 hook 的条目，别的 hooks 原样保留）
    .\install-takeover-hooks.ps1 -SettingsRoot $env:TEMP\hooktest   # 装到别的根目录（自测用）

只增删 settings.json 的 hooks 节点，其余配置（enabledPlugins 等）原样保留；
覆盖前先备份 settings.json.bak。Qoder Hooks 不支持热重载：装完/拆完要重启 IDE 生效。
#>
param(
    [switch]$Remove,
    [string]$SettingsRoot = (Join-Path $env:USERPROFILE '.qoder-cn')
)
$ErrorActionPreference = 'Stop'

$scriptSrc = Join-Path $PSScriptRoot 'qoder-status.ps1'
$hooksDir  = Join-Path $SettingsRoot 'hooks'
$scriptDst = Join-Path $hooksDir 'qoder-status.ps1'
$settings  = Join-Path $SettingsRoot 'settings.json'
$events    = @('Notification', 'PermissionRequest', 'PostToolUseFailure', 'Stop')
# hook 条目长这样（协议 §3.4）：事件 → [ { hooks: [ { type: "command", command: "..." } ] } ]
$command   = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$scriptDst`""

function Read-Settings {
    if (Test-Path $settings) {
        $raw = [System.IO.File]::ReadAllText($settings)
        if (-not [string]::IsNullOrWhiteSpace($raw)) { return $raw | ConvertFrom-Json }
    }
    return [pscustomobject]@{}
}

function Write-Settings($obj) {
    # 不带 BOM 写回：与原文件一致，也让别的 JSON 工具读得动
    $json = $obj | ConvertTo-Json -Depth 12
    New-Item -ItemType Directory -Force -Path $SettingsRoot | Out-Null
    [System.IO.File]::WriteAllText($settings, $json, (New-Object System.Text.UTF8Encoding $false))
}

# 从一个事件数组里摘掉所有指向 qoder-status.ps1 的条目；用户自己挂的别的 handler 不动
function Strip-Ours($arr) {
    @($arr | Where-Object {
        ($_ | ConvertTo-Json -Depth 12 -Compress) -notmatch 'qoder-status\.ps1'
    })
}

if (-not $Remove -and -not (Test-Path $scriptSrc)) {
    throw "找不到 $scriptSrc（要和它放在一起）"
}

$obj = Read-Settings
if (-not $obj.PSObject.Properties['hooks']) {
    $obj | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{})
}
$hooks = $obj.hooks

# 第一步永远是摘旧条目：装两次不会重复挂（幂等），拆的时候它就是卸载
foreach ($ev in $events) {
    if (-not $hooks.PSObject.Properties[$ev]) { continue }
    $kept = Strip-Ours $hooks.$ev
    if ($kept.Count -gt 0) { $hooks.$ev = $kept }
    else { [void]$hooks.PSObject.Properties.Remove($ev) }
}

if ($Remove) {
    # 注意用 Properties 集合本身计数：没有成员时 .Name 是 $null，@($null).Count 会是 1
    if (@($hooks.PSObject.Properties).Count -eq 0) {
        [void]$obj.PSObject.Properties.Remove('hooks')
    }
    Write-Settings $obj
    if (Test-Path $scriptDst) { Remove-Item $scriptDst }
    Write-Host "已拆下 takeover hooks（日志与 .bak 留在 $hooksDir，可手动清）。重启 IDE 生效。" -ForegroundColor Green
    exit 0
}

# 装：拷脚本 → 备份 settings.json → 四个事件各挂一条标准条目
New-Item -ItemType Directory -Force -Path $hooksDir | Out-Null
Copy-Item $scriptSrc $scriptDst -Force
if (Test-Path $settings) { Copy-Item $settings "$settings.bak" -Force }

foreach ($ev in $events) {
    $entry = [pscustomobject]@{
        hooks = @([pscustomobject]@{ type = 'command'; command = $command })
    }
    $new = @($entry)
    if ($hooks.PSObject.Properties[$ev]) { $new = @($hooks.$ev) + $entry }
    else { $hooks | Add-Member -NotePropertyName $ev -NotePropertyValue $new }
    $hooks.$ev = $new
}
Write-Settings $obj

Write-Host "已安装 takeover hooks：" -ForegroundColor Green
foreach ($ev in $events) { Write-Host "  $ev -> $command" -ForegroundColor DarkGray }
Write-Host "脚本在 $scriptDst（每次触发往同目录 qoder-status.log 记一行原始事件）。"
Write-Host "注意：Qoder Hooks 不支持热重载 —— 重启 IDE 后才生效。" -ForegroundColor Yellow
