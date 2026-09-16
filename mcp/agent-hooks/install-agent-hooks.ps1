<#
install-agent-hooks.ps1 —— 一键给多家 AI 编码代理装上「会话状态 → xa 全屏提醒」的 Hooks

    .\install-agent-hooks.ps1 -List                          # 看平台表与验证状态
    .\install-agent-hooks.ps1 -Platform qoder                # 装（Qoder CN：已实测）
    .\install-agent-hooks.ps1 -Platform trae -DryRun         # 只看会写什么（Trae：schema 待校准）
    .\install-agent-hooks.ps1 -Platform trae -ProjectPath D:\repo   # 装到项目级 .trae\hooks.json
    .\install-agent-hooks.ps1 -Platform qoder -Remove        # 拆（只摘指向 agent-status.ps1 的条目）
    .\install-agent-hooks.ps1 -Platform qoder -Root $env:TEMP\hooktest   # 装到别的根（自测）

两件要知道的事：
  1. 只增删指向 agent-status.ps1 的条目，用户自己挂的别的 handler 原样保留；覆盖前备份 .bak。
  2. 两家的落地形态不同：Claude Code / Qoder 是 settings.json 里的 hooks 节点，
     Trae 是独立的 hooks.json 文件——本脚本按目标文件**已有的结构**自适应（见 Resolve-Layout）。
#>
param(
    [string]$Platform,
    [string]$Root,
    [string]$ProjectPath,
    [switch]$Remove,
    [switch]$DryRun,
    [switch]$Force,
    [switch]$List
)
$ErrorActionPreference = 'Stop'

$scriptSrc = Join-Path $PSScriptRoot 'agent-status.ps1'
$scriptName = 'agent-status.ps1'
# 与 agent-status.ps1 的分诊分支一一对应；这里挂多了没用，挂少了会漏档
$events = @('Notification', 'PermissionRequest', 'PostToolUseFailure', 'Stop')

$Platforms = [ordered]@{
    'qoder'      = @{ Label = 'Qoder CN（桌面版 / IDE）'; Kind = 'settings-hooks'; File = 'settings.json'; Rel = ''; State = '已实测（QoderComputerUse 接管遮罩 + 12 事件）' }
    'claude'     = @{ Label = 'Claude Code'; Kind = 'settings-hooks'; File = 'settings.json'; Rel = ''; State = '协议源头，结构同 Qoder，未本机实测' }
    'trae'       = @{ Label = 'Trae CN'; Kind = 'hooks-file'; File = 'hooks.json'; Rel = ''; State = '路径与事件名已确证，schema 待用 Trae 的 Hooks 面板核对' }
    'trae-intl'  = @{ Label = 'Trae（国际版）'; Kind = 'hooks-file'; File = 'hooks.json'; Rel = ''; State = '同 trae' }
    'cursor'     = @{ Label = 'Cursor'; Kind = 'unknown'; File = 'hooks.json'; Rel = ''; State = '待校准：先照官方 hooks 文档在 UI 里配一条，再回来装' }
    'windsurf'   = @{ Label = 'Windsurf（Cascade）'; Kind = 'unknown'; File = 'hooks.json'; Rel = ''; State = '待校准' }
    'codex'      = @{ Label = 'Codex CLI'; Kind = 'unknown'; File = 'hooks.json'; Rel = ''; State = '待校准' }
}
$DefaultRoots = @{
    'qoder'     = Join-Path $env:USERPROFILE '.qoder-cn'
    'claude'    = Join-Path $env:USERPROFILE '.claude'
    'trae'      = Join-Path $env:USERPROFILE '.trae-cn'
    'trae-intl' = Join-Path $env:USERPROFILE '.trae'
    'cursor'    = Join-Path $env:USERPROFILE '.cursor'
    'windsurf'  = Join-Path $env:USERPROFILE '.codeium\windsurf'
    'codex'     = Join-Path $env:USERPROFILE '.codex'
}

if ($List -or -not $Platform) {
    Write-Host "`n可装的平台：" -ForegroundColor Cyan
    foreach ($k in $Platforms.Keys) {
        $p = $Platforms[$k]
        Write-Host ("  {0,-10} {1,-24} 根目录 {2}" -f $k, $p.Label, $DefaultRoots[$k])
        Write-Host ("             {0}｜{1}" -f $p.Kind, $p.State) -ForegroundColor DarkGray
    }
    Write-Host "`n用法：.\install-agent-hooks.ps1 -Platform <名字> [-ProjectPath <仓库>] [-Remove] [-DryRun]`n"
    exit 0
}

if (-not $Platforms.Contains($Platform)) {
    throw "不认识的平台「$Platform」。用 -List 看可用名字。"
}
$meta = $Platforms[$Platform]
$kind = $meta.Kind

# 没实测过的平台默认不写：宁可让你照官方模板手动配一条，也不猜着写坏你的配置
if ($kind -eq 'unknown' -and -not $Force -and -not $Remove) {
    throw @"
平台「$($meta.Label)」的 hooks 落地形态还没校准（$($meta.State)）。
先在它的官方 UI / 文档里配一条指向任意命令的 hook，把生成的文件与结构确认下来，
再回来跑 -Root 指到那个目录；确认无误后可以加 -Force 直接写。
"@
}

# 目标文件：项目级（Trae 的 .trae/hooks.json）优先于全局
if ($ProjectPath) {
    $targetDir = Join-Path $ProjectPath '.trae'
    $target = Join-Path $targetDir $meta.File
    $scriptDir = Join-Path $targetDir 'hooks'
} else {
    $targetDir = if ($Root) { $Root } else { $DefaultRoots[$Platform] }
    $target = Join-Path $targetDir $meta.File
    $scriptDir = Join-Path $targetDir 'hooks'
}
$scriptDst = Join-Path $scriptDir $scriptName
$logDir = Join-Path $env:LOCALAPPDATA 'XAssistant\agent-hooks'
$command = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$scriptDst`" -Platform $Platform"

function Read-JsonFile([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $raw = [System.IO.File]::ReadAllText($path)
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return ($raw | ConvertFrom-Json)
}

function Write-JsonFile([string]$path, $obj, [bool]$dry) {
    # 无 BOM 写回：与这些工具自己的写法一致，也让别的 JSON 工具读得动
    $json = $obj | ConvertTo-Json -Depth 12
    if ($dry) { Write-Host "`n--- 将写入 $path ---" -ForegroundColor Cyan; Write-Host $json; return }
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding $false))
}

# 从现有文件里认出它的布局：settings.json 型把 hooks 挂在 "hooks" 键下；
# Trae 的独立 hooks.json 有两种可能——同样带一层 "hooks"，或者顶层直接就是事件名。
# 已经有用户自己的条目时按它的样子来（这就是 schema 校准的落地方式），空的才用默认。
function Resolve-Layout($obj) {
    if ($null -eq $obj) { return 'nested' }
    if ($obj.PSObject.Properties['hooks']) { return 'nested' }
    foreach ($ev in $events) { if ($obj.PSObject.Properties[$ev]) { return 'flat' } }
    if ($kind -eq 'hooks-file') { return 'nested' }
    return 'nested'
}

function Get-HookBag($obj, [string]$layout) {
    if ($layout -eq 'flat') { return $obj }
    if (-not $obj.PSObject.Properties['hooks']) {
        $obj | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{})
    }
    return $obj.hooks
}

# 摘掉所有指向 agent-status.ps1 / 旧 qoder-status.ps1 的条目，用户自己的 handler 不动。
# 注意：函数只返回一个元素时 PowerShell 会把数组解包成那个元素本身，而解包后的
# PSCustomObject 没有 Count（是 $null）——调用处必须再包一层 @()，否则会误判成「没剩下」
# 而把用户自己挂的同事件条目一起删掉。
function Strip-Ours($arr) {
    @($arr | Where-Object {
        ($_ | ConvertTo-Json -Depth 12 -Compress) -notmatch 'agent-status\.ps1|qoder-status\.ps1'
    })
}

$existing = Read-JsonFile $target
$layout = Resolve-Layout $existing
$obj = if ($existing) { $existing } else { [pscustomobject]@{} }
$bag = Get-HookBag $obj $layout

# 第一步永远是摘旧条目：装两次不会重复挂（幂等），拆的时候它就是卸载
foreach ($ev in $events) {
    if (-not $bag.PSObject.Properties[$ev]) { continue }
    $kept = @(Strip-Ours $bag.$ev)
    if ($kept.Count -gt 0) { $bag.$ev = $kept } else { [void]$bag.PSObject.Properties.Remove($ev) }
}

if ($Remove) {
    # 摘完只剩一个空 hooks 节点时连节点一起收掉（别留 "hooks": {} 让工具去较真）。
    # 这里不能用 -eq 比对象：PSCustomObject 比较不出引用相等。
    if ($layout -eq 'nested' -and $obj.PSObject.Properties['hooks'] -and @($bag.PSObject.Properties).Count -eq 0) {
        [void]$obj.PSObject.Properties.Remove('hooks')
    }
    if (@($obj.PSObject.Properties).Count -eq 0) {
        # 文件里本来只挂着我们的东西：整份删掉，别留个空 {} 让工具报错
        if (Test-Path $target) {
            if ($DryRun) { Write-Host "[dry-run] 将删除 $target" } else { Remove-Item $target -Force }
        }
    } else {
        Write-JsonFile $target $obj $DryRun
    }
    if (Test-Path $scriptDst) {
        if ($DryRun) { Write-Host "[dry-run] 将删除 $scriptDst" } else { Remove-Item $scriptDst -Force }
    }
    Write-Host "已拆下 $($meta.Label) 的 hooks（日志在 $logDir）。" -ForegroundColor Green
    if ($kind -eq 'settings-hooks') { Write-Host "注意：这类 IDE 的 hooks 不支持热重载，重启后生效。" -ForegroundColor Yellow }
    exit 0
}

if (-not (Test-Path $scriptSrc)) { throw "找不到 $scriptSrc（要和它放在一起）" }

if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $scriptDir | Out-Null
    Copy-Item $scriptSrc $scriptDst -Force
    # 中文注释的 ps1 在 Windows PowerShell 5.1 下必须带 BOM，否则注释与文案会花屏
    $text = [System.IO.File]::ReadAllText($scriptDst, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($scriptDst, $text, (New-Object System.Text.UTF8Encoding $true))
    if (Test-Path $target) { Copy-Item $target "$target.bak" -Force }
}

foreach ($ev in $events) {
    $entry = [pscustomobject]@{
        hooks = @([pscustomobject]@{ type = 'command'; command = $command })
    }
    $new = @($entry)
    if ($bag.PSObject.Properties[$ev]) { $new = @($bag.$ev) + $entry }
    else { $bag | Add-Member -NotePropertyName $ev -NotePropertyValue $new }
    $bag.$ev = $new
}
Write-JsonFile $target $obj $DryRun

if ($DryRun) {
    Write-Host "`n[dry-run] 目标 $target（布局 $layout）；脚本会装到 $scriptDst" -ForegroundColor Yellow
    exit 0
}

Write-Host "已安装 $($meta.Label) 的接管提醒 hooks：" -ForegroundColor Green
foreach ($ev in $events) { Write-Host "  $ev -> $command" -ForegroundColor DarkGray }
Write-Host "脚本 $scriptDst；每次触发往 $logDir\agent-status.log 记一行原始事件。"
if ($meta.State -notmatch '已实测') { Write-Host "提醒：$($meta.State)" -ForegroundColor Yellow }
if ($kind -eq 'settings-hooks') { Write-Host "Qoder / Claude Code 的 hooks 不支持热重载 —— 重启 IDE 后才生效。" -ForegroundColor Yellow }
