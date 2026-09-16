# register-xa.ps1 — 把 xa 命令注册进当前用户 PATH，供 cmd / AI 工具直接调用
<#
.SYNOPSIS
    把 xa 命令注册进当前用户 PATH：之后在 cmd / PowerShell 里直接写
    xa -s info 5 1 1 -border on 50 30 1 -lable on 24 "AI 接管中" 就能放全屏效果。

.DESCRIPTION
    1. 在 %LOCALAPPDATA%\XAssistant\bin 生成 xa.cmd 包装脚本（内容就一行：把参数转给 XAssistant.exe）
    2. 把该目录追加进当前用户 PATH（只动 HKCU，不需要管理员）
    3. 可重复执行：只刷新 xa.cmd 内容与指向，不重复加 PATH

.NOTES
    参数：
      -ExePath   XAssistant.exe 的完整路径。默认自动找：先找部署目录 <系统盘>\XAssistant，
                 再找项目的 bin\Debug\net8.0-windows，都没有就报错要求显式指定。
      -Remove    注销：删掉 xa.cmd 并把目录从用户 PATH 里撤下。

    注册后需要新开一个终端窗口（PATH 只在进程启动时读一次）。
    xa 的语法与效果参数见 README「给脚本与 AI 用」一节。
#>
param(
    [string]$ExePath,
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$shimDir = Join-Path $env:LOCALAPPDATA "XAssistant\bin"
$cmdFile = Join-Path $shimDir "xa.cmd"
$pathKey = "HKCU:\Environment"

# PATH 走注册表读写而不是 [Environment]::GetEnvironmentVariable：
# 后者会把 %USERPROFILE% 这类引用展开成字面值，写回时就把别人的动态段清成死路径了
function Get-UserPathRaw {
    $key = Get-Item $pathKey -ErrorAction SilentlyContinue
    if (-not $key) { return "" }
    return [string]$key.GetValue("Path", "", "DoNotExpandEnvironmentNames")
}

function Get-UserPathKind {
    $key = Get-Item $pathKey -ErrorAction SilentlyContinue
    if (-not $key) { return "String" }
    try { return $key.GetValueKind("Path") } catch { return "String" }
}

function Test-SameDir([string]$left, [string]$right) {
    return $left.Trim().TrimEnd('\') -ieq $right.Trim().TrimEnd('\')
}

if ($Remove) {
    if (Test-Path $cmdFile) { Remove-Item $cmdFile -Force }
    $raw = Get-UserPathRaw
    $kept = @($raw -split ';' | Where-Object { $_ -and -not (Test-SameDir $_ $shimDir) })
    $kind = Get-UserPathKind
    Set-ItemProperty -Path $pathKey -Name "Path" -Value ($kept -join ';') -Type $kind
    Write-Host "已注销 xa 命令：xa.cmd 已删除，用户 PATH 已撤下 $shimDir" -ForegroundColor Green
    return
}

# ---------- 1. 定住要指向的 exe ----------
if (-not $ExePath) {
    $candidates = @(
        (Join-Path $env:SystemDrive "XAssistant\XAssistant.exe"),
        (Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\XAssistant.exe")
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "没找到 XAssistant.exe，请用 -ExePath 指定完整路径。"
}
$ExePath = (Resolve-Path $ExePath).Path

# ---------- 2. 生成 xa.cmd ----------
New-Item -ItemType Directory -Force -Path $shimDir | Out-Null
$content = "@echo off`r`n`"$ExePath`" %*`r`n"
# 批处理由 cmd 按 ANSI 代码页读，写 ANSI 才不会把路径里的中文读坏
[System.IO.File]::WriteAllText($cmdFile, $content, [System.Text.Encoding]::Default)

# ---------- 3. 入用户 PATH（只追加一次，不动系统 PATH） ----------
$raw = Get-UserPathRaw
$already = @($raw -split ';' | Where-Object { $_ -and (Test-SameDir $_ $shimDir) }).Count -gt 0
if ($already) {
    Write-Host "用户 PATH 里已有 $shimDir，不重复添加。" -ForegroundColor Green
} else {
    $newPath = if ([string]::IsNullOrWhiteSpace($raw)) { $shimDir } else { $raw.TrimEnd(';') + ";" + $shimDir }
    $kind = Get-UserPathKind
    Set-ItemProperty -Path $pathKey -Name "Path" -Value $newPath -Type $kind
    Write-Host "已把 $shimDir 追加进用户 PATH。" -ForegroundColor Green
}

Write-Host "xa 已指向: $ExePath" -ForegroundColor Cyan
Write-Host "测试（这个窗口里就已经能用）:" -ForegroundColor Cyan
Write-Host "  & `"$cmdFile`" -s info 5 1 1 -border on 50 30 1 -lable on 24 `"AI 接管中`"" -ForegroundColor DarkGray
Write-Host "新开一个 cmd 或 PowerShell 窗口后，直接写:" -ForegroundColor Cyan
Write-Host "  xa -s info 5 1 1 -border on 50 30 1 -lable on 24 `"AI 接管中`"" -ForegroundColor White