<#
.SYNOPSIS
    构建 XAssistant 并部署到 <系统盘>\XAssistant（可用 -TargetDir 覆盖）
.DESCRIPTION
    1. 使用 Release 配置发布项目到 bin\deploy\publish
    2. 若目标应用正在运行，强制结束进程
    3. 备份目标目录下的旧文件（如有）
    4. 将发布输出复制到目标目录
    5. 启动应用
    6. 注册 xa 命令（把 <目标目录>\XAssistant.exe 指向给 cmd / AI 用；-SkipXa 跳过）
.NOTES
    脚本需在项目根目录（XAssistant.csproj 所在目录）以管理员身份运行，
    因为系统盘根目录下的部署目录可能需要管理员权限。
#>

param(
    # 部署目标目录。默认跟着 SystemDrive 走，不写死盘符
    [string]$TargetDir = (Join-Path $env:SystemDrive "XAssistant"),
    # 跳过第 6 步的 xa 命令注册（只想部署不想动用户 PATH 时用）
    [switch]$SkipXa
)

$ErrorActionPreference = "Stop"

# ---------- 配置 ----------
$scriptPath   = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile  = Join-Path $scriptPath "XAssistant.csproj"
$targetDir    = $TargetDir
$processName  = "XAssistant"
# 发布输出直接指定目录，不再按 bin\Release\<目标框架>\win-x64\publish 去猜：
# 目标框架从 net9 改回 net8 那次，猜出来的路径就不存在了，第 4 步复制直接失败
$publishDir   = Join-Path $scriptPath "bin\deploy\publish"

# ---------- 1. 构建发布 ----------
Write-Host "[1/6] 正在发布项目 (Release)..." -ForegroundColor Cyan
# 先清干净：第 4 步是把这个目录里的所有东西拷到部署目录，残留的旧文件会跟着过去
if (Test-Path $publishDir) {
    Remove-Item "$publishDir\*" -Recurse -Force -ErrorAction SilentlyContinue
}
dotnet publish $projectFile -r win-x64 -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "发布失败，请检查错误信息。"
}
Write-Host "    发布完成: $publishDir" -ForegroundColor Green

# ---------- 2. 停止正在运行的实例 ----------
Write-Host "[2/6] 检查是否正在运行..." -ForegroundColor Cyan
$runningProcess = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($runningProcess) {
    Write-Host "    发现运行中的进程，正在停止..." -ForegroundColor Yellow
    Stop-Process -Name $processName -Force
    Start-Sleep -Seconds 2
    Write-Host "    已停止。" -ForegroundColor Green
} else {
    Write-Host "    未检测到运行实例。" -ForegroundColor Green
}

# ---------- 3. 备份旧版本 ----------
Write-Host "[3/6] 备份旧版本..." -ForegroundColor Cyan
if (Test-Path $targetDir) {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupDir = "$($targetDir.TrimEnd('\','/'))_Backup_$timestamp"
    Write-Host "    正在备份到 $backupDir ..." -ForegroundColor Yellow
    Copy-Item -Path $targetDir -Destination $backupDir -Recurse -Force
    Write-Host "    备份完成。" -ForegroundColor Green
} else {
    Write-Host "    目标目录不存在，无需备份。" -ForegroundColor Green
}

# ---------- 4. 复制新文件 ----------
Write-Host "[4/6] 复制新文件到 $targetDir ..." -ForegroundColor Cyan
# 确保目标目录存在
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# 清空目标目录（避免残留旧文件）
Remove-Item "$targetDir\*" -Recurse -Force -ErrorAction SilentlyContinue

# 复制所有发布文件
Copy-Item -Path "$publishDir\*" -Destination $targetDir -Recurse -Force
Write-Host "    复制完成。" -ForegroundColor Green

# ---------- 5. 启动应用 ----------
Write-Host "[5/6] 启动 XAssistant..." -ForegroundColor Cyan
$appExe = Join-Path $targetDir "XAssistant.exe"
if (Test-Path $appExe) {
    Start-Process $appExe
    Write-Host "    应用已启动。" -ForegroundColor Green
} else {
    throw "未找到 $appExe ，部署可能不完整。"
}

# ---------- 6. 注册 xa 命令 ----------
if (-not $SkipXa) {
    Write-Host "[6/6] 注册 xa 命令（cmd / AI 可直接调用）..." -ForegroundColor Cyan
    $registerScript = Join-Path $scriptPath "register-xa.ps1"
    if (Test-Path $registerScript) {
        try {
            & $registerScript -ExePath $appExe
        } catch {
            Write-Host "    xa 注册失败（不影响主程序使用）: $_" -ForegroundColor Yellow
        }
    } else {
        Write-Host "    未找到 register-xa.ps1，跳过（不影响主程序）。" -ForegroundColor Yellow
    }
} else {
    Write-Host "[6/6] 已跳过 xa 注册（-SkipXa）。" -ForegroundColor DarkGray
}

Write-Host "`n部署成功！" -ForegroundColor Green

# hooks 不随部署自动装：改 IDE 全局配置需要显式意愿（决策见 CLAUDE.md），这里只提示一行
$hooksScript = Join-Path $scriptPath "mcp\agent-hooks\install-agent-hooks.ps1"
if (Test-Path $hooksScript) {
    Write-Host "提示：要让 Qoder / Trae 等 IDE 的对话状态弹全屏提醒，跑一次 $hooksScript -Platform qoder（装完重启 IDE）" -ForegroundColor Cyan
}