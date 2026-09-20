<#
extract-ide-icons.ps1 —— 从各 IDE 的 exe 里抠出**最大尺寸**的图标帧，落成 Assets\IdeIcons\<平台>.png

    .\extract-ide-icons.ps1                                   # 抽本机全部已知平台
    .\extract-ide-icons.ps1 -Platform qoder -ExePath C:\...   # 指定单个
    .\extract-ide-icons.ps1 -OutDir <目录>                    # 换输出（默认仓库 Assets\IdeIcons）

Icon.ExtractAssociatedIcon 只会给 32×32，这里用 ExtractIconEx 枚举 exe 的全部图标尺寸、
取最大的那一帧（现代 IDE 图标一般带 256×256），转 PNG 存盘。
消息栈的来源徽章用它：有图标的平台贴图，没图标的平台自动退回字母徽章。
#>
param(
    [string]$Platform,
    [string]$ExePath,
    [string]$OutDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'Assets\IdeIcons')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('Probe' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Probe {
    // 实测（本机新版 Windows）：PrivateExtractIconsW 在 user32，ExtractIconExW 反而在 shell32——两家都可能改挂点，
    // 用 GetProcAddress 对照验过。ExtractIconExW 只给 32px 槽位；PrivateExtractIconsW 能指定尺寸拿 256px PNG 帧
    [DllImport("user32.dll", EntryPoint="PrivateExtractIconsW", CharSet=CharSet.Unicode, ExactSpelling=true)]
    public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint max, uint flags);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool DestroyIcon(IntPtr h);
}
"@
}

# 平台 → 本机 exe 候选（装在哪由启动器决定，进程路径最可靠，安装目录兜底）
function Find-Exe([string[]]$names, [string[]]$dirHints) {
    foreach ($n in $names) {
        $p = Get-Process -Name $n -ErrorAction SilentlyContinue | Where-Object { $_.MainModule } | Select-Object -First 1
        if ($p) { try { return $p.MainModule.FileName } catch { } }
    }
    foreach ($d in $dirHints) { if (Test-Path $d) { return $d } }
    return $null
}

$Platforms = [ordered]@{
    'qoder'      = @{ Names = @('Qoder CN IDE', 'QoderCN'); Dirs = @("$env:LOCALAPPDATA\Programs\Qoder CN\Qoder CN.exe") }
    'trae'       = @{ Names = @('Trae CN', 'Trae'); Dirs = @("$env:LOCALAPPDATA\Programs\Trae CN\Trae CN.exe") }
    'dsh'        = @{ Names = @('DeepSeek Harness', 'deepseek-harness'); Dirs = @() }
    'xassistant' = @{ Names = @('XAssistant'); Dirs = @() }
}

function Extract-BestIcon([string]$exe, [string]$png) {
    # 先探总数，再按 256×256 取主图标帧（拿不到 256 时系统会回退到最接近的尺寸）
    $total = [Probe]::PrivateExtractIcons($exe, -1, 0, 0, $null, $null, 0, 0)
    if ($total -eq 0) { throw "$exe 里没有图标资源" }
    $handles = New-Object IntPtr[] 1
    # puiIconID 出参允许 NULL（PS 5.1 又不认 uint[] 类型字面量，干脆不取）
    $got = [Probe]::PrivateExtractIcons($exe, 0, 256, 256, $handles, $null, 1, 0)
    if ($got -eq 0 -or $handles[0] -eq [IntPtr]::Zero) { throw "$exe 抽不到图标帧" }
    try {
        $ico = [System.Drawing.Icon]::FromHandle($handles[0])
        $bmp = $ico.ToBitmap()   # 主帧若是 Vista+ 的 PNG 容器，这里拿到的是全尺寸
        $size = $bmp.Width
        $dir = Split-Path $png -Parent
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    } finally { [void][Probe]::DestroyIcon($handles[0]) }
    return $size
}

$targets = if ($Platform) { @{ $Platform = @{ Direct = $ExePath } } } else { $Platforms }
foreach ($k in $targets.Keys) {
    $exe = if ($Platform) { $ExePath } else { Find-Exe $targets[$k].Names $targets[$k].Dirs }
    if (-not $exe -or -not (Test-Path $exe)) { Write-Host "跳过 $k：找不到 exe" -ForegroundColor Yellow; continue }
    $png = Join-Path $OutDir "$k.png"
    try {
        $size = Extract-BestIcon $exe $png
        Write-Host "  $k  <- $exe（主帧 ${size}px）→ $png" -ForegroundColor Green
    } catch { Write-Host "  $k 抽取失败：$_" -ForegroundColor Red }
}
