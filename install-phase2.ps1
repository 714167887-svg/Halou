#requires -Version 5.1
<#
.SYNOPSIS
    Phase 2 Halou (HalouHost + Payload 热重载架构) 一键部署脚本。

.DESCRIPTION
    在新机器（或者从 OLD host 迁移到 NEW host）上一键完成：
      1. 关闭所有 acad
      2. 备份现有 OLD host (JsqClipboardCadPlugin.dll，如果存在)
      3. 复制 HalouHost.dll + HalouContract.dll + manifest 到 %LOCALAPPDATA%\HalouSuite\bin\
      4. 复制最新 HalouPayload.X.X.X.dll 到 %LOCALAPPDATA%\HalouSuite\payloads\
      5. 注册表写入 AutoCAD 自启动条目，LOADER 指向 HalouHost.dll

    所有源 DLL/manifest 来自 ../halou-release/release/（默认）。

.PARAMETER ReleaseDir
    halou-release/release/ 的本地路径。默认 ../halou-release/release（相对脚本位置）

.PARAMETER AcadVersion
    AutoCAD 版本号。默认 R24.0（acad 2021）
    其他常见：R24.1 (2022), R24.2 (2023), R24.3 (2024), R25.0 (2025), R25.1 (2026)

.PARAMETER AcadLanguage
    AutoCAD 语言后缀。默认 ACAD-4101:804（简中 2021）
    脚本会自动遍历该 AcadVersion 下所有 ACAD-* 子键，无需精确指定

.PARAMETER Uninstall
    卸载：删注册表条目 + 删 bin/payloads 目录

.EXAMPLE
    .\install-phase2.ps1
    # 默认部署 NEW host + 最新 Payload

.EXAMPLE
    .\install-phase2.ps1 -Uninstall
    # 清理所有 Halou 相关条目和文件
#>
param(
    [string]$ReleaseDir = "",
    [string]$AcadVersion = "R24.0",
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    # 默认假设 halou-release 与 Halou 同级
    $ReleaseDir = Join-Path $here "..\halou-release\release"
}
$ReleaseDir = (Resolve-Path $ReleaseDir -ErrorAction SilentlyContinue)
if (-not $ReleaseDir -or -not (Test-Path $ReleaseDir)) {
    Write-Host "❌ 找不到 halou-release/release 目录" -ForegroundColor Red
    Write-Host "   请确认 halou-release 已 clone，并用 -ReleaseDir 指定路径" -ForegroundColor Yellow
    exit 1
}

$bin = "$env:LOCALAPPDATA\HalouSuite\bin"
$payloads = "$env:LOCALAPPDATA\HalouSuite\payloads"

Write-Host ""
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host "  Halou Phase 2 一键部署" -ForegroundColor Cyan
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "源目录：$ReleaseDir"
Write-Host "目标 bin：$bin"
Write-Host "目标 payloads：$payloads"
Write-Host "AutoCAD 版本：$AcadVersion"
Write-Host ""

# ---- 1. 关闭所有 acad ----
Write-Host "==> 1. 检查并关闭所有 AutoCAD" -ForegroundColor Cyan
$acad = Get-Process acad -ErrorAction SilentlyContinue
if ($acad) {
    Write-Host "   发现 $($acad.Count) 个 acad 进程，关闭中..." -ForegroundColor Yellow
    $acad | Stop-Process -Force
    Start-Sleep -Seconds 3
    if (Get-Process acad -ErrorAction SilentlyContinue) {
        Write-Host "❌ acad 仍在运行，请手动关闭后重试" -ForegroundColor Red
        exit 1
    }
}
Write-Host "   ✓ 无运行的 acad" -ForegroundColor Green

# ---- 卸载模式 ----
if ($Uninstall) {
    Write-Host ""
    Write-Host "==> 卸载模式" -ForegroundColor Yellow

    # 删注册表
    $regBase = "HKCU:\Software\Autodesk\AutoCAD"
    if (Test-Path $regBase) {
        Get-ChildItem $regBase -ErrorAction SilentlyContinue | ForEach-Object {
            Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                $halouKey = Join-Path $_.PSPath "Applications\HalouSuite"
                if (Test-Path $halouKey) {
                    Remove-Item $halouKey -Force -Recurse
                    Write-Host "   删除：$halouKey" -ForegroundColor Yellow
                }
            }
        }
    }

    # 删文件
    if (Test-Path "$env:LOCALAPPDATA\HalouSuite") {
        Remove-Item "$env:LOCALAPPDATA\HalouSuite" -Recurse -Force
        Write-Host "   删除：$env:LOCALAPPDATA\HalouSuite" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "✓ 卸载完成" -ForegroundColor Green
    exit 0
}

# ---- 2. 创建目录 ----
Write-Host ""
Write-Host "==> 2. 创建目标目录" -ForegroundColor Cyan
New-Item $bin -ItemType Directory -Force | Out-Null
New-Item $payloads -ItemType Directory -Force | Out-Null
New-Item "$env:LOCALAPPDATA\HalouSuite\state" -ItemType Directory -Force | Out-Null
Write-Host "   ✓ 目录就绪" -ForegroundColor Green

# ---- 3. 备份并删除现有 OLD host（防止混合加载）----
$oldHost = Join-Path $bin "JsqClipboardCadPlugin.dll"
if (Test-Path $oldHost) {
    Write-Host ""
    Write-Host "==> 3. 备份并删除现有 OLD host" -ForegroundColor Cyan
    $backupDir = Join-Path $bin "backup-old-host"
    New-Item $backupDir -ItemType Directory -Force | Out-Null
    $stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
    Copy-Item $oldHost (Join-Path $backupDir "JsqClipboardCadPlugin.$stamp.dll") -Force
    Remove-Item $oldHost -Force
    Write-Host "   ✓ 已备份并删除：$oldHost" -ForegroundColor Green
    Write-Host "     （留 OLD DLL 在 bin 会导致 acad 同时加载 OLD+NEW，热更新触发 FileLoadException）" -ForegroundColor DarkYellow
}

# ---- 3b. 清理 payloads 目录下旧 HalouPayload.*.dll（避免 NEW host 误加载旧 Payload）----
$stalePayloads = Get-ChildItem $payloads -Filter "HalouPayload.*.dll" -ErrorAction SilentlyContinue
if ($stalePayloads.Count -gt 0) {
    Write-Host ""
    Write-Host "==> 3b. 清理 payloads 目录下旧 HalouPayload.*.dll" -ForegroundColor Cyan
    $payloadBackup = Join-Path $payloads "backup-stale"
    New-Item $payloadBackup -ItemType Directory -Force | Out-Null
    foreach ($p in $stalePayloads) {
        Move-Item $p.FullName (Join-Path $payloadBackup $p.Name) -Force
        Write-Host "   移出：$($p.Name)" -ForegroundColor Yellow
    }
    Write-Host "   ✓ 已备份到 $payloadBackup" -ForegroundColor Green
}

# ---- 4. 部署 NEW host ----
Write-Host ""
Write-Host "==> 4. 部署 NEW host (HalouHost + Contract)" -ForegroundColor Cyan
$hostDll     = Join-Path $ReleaseDir "HalouHost.dll"
$contractDll = Join-Path $ReleaseDir "HalouContract.dll"
$manifest    = Join-Path $ReleaseDir "halou-plugin-manifest.json"

foreach ($f in @($hostDll, $contractDll)) {
    if (-not (Test-Path $f)) {
        Write-Host "❌ 缺少文件：$f" -ForegroundColor Red
        exit 1
    }
}

Copy-Item $hostDll     (Join-Path $bin "HalouHost.dll")     -Force
Copy-Item $contractDll (Join-Path $bin "HalouContract.dll") -Force
if (Test-Path $manifest) {
    Copy-Item $manifest (Join-Path $bin "halou-plugin-manifest.json") -Force
}
Write-Host "   ✓ HalouHost.dll + HalouContract.dll 已部署" -ForegroundColor Green

# ---- 5. 部署最新 Payload ----
Write-Host ""
Write-Host "==> 5. 部署最新 Payload" -ForegroundColor Cyan
$payloadFiles = Get-ChildItem $ReleaseDir -Filter "HalouPayload.*.dll" | Sort-Object Name
if ($payloadFiles.Count -eq 0) {
    Write-Host "❌ halou-release/release/ 下没有 HalouPayload.*.dll" -ForegroundColor Red
    exit 1
}
# 按版本号排序取最新（简单字典序，对 X.Y.Z 三位数版本号够用）
$latest = $payloadFiles | Sort-Object {
    if ($_.Name -match 'HalouPayload\.(\d+)\.(\d+)\.(\d+)\.dll') {
        [int]$matches[1]*1000000 + [int]$matches[2]*1000 + [int]$matches[3]
    } else { 0 }
} | Select-Object -Last 1

Copy-Item $latest.FullName (Join-Path $payloads $latest.Name) -Force
Write-Host "   ✓ $($latest.Name) 已部署" -ForegroundColor Green

# 写 lkg
$lkg = Join-Path $payloads $latest.Name
Set-Content -Path "$env:LOCALAPPDATA\HalouSuite\state\lkg.txt" -Value $lkg -Encoding ASCII
Write-Host "   ✓ lkg.txt -> $($latest.Name)" -ForegroundColor Green

# ---- 6. 注册表 ----
Write-Host ""
Write-Host "==> 6. 注册 AutoCAD 自启动" -ForegroundColor Cyan
$regBase = "HKCU:\Software\Autodesk\AutoCAD\$AcadVersion"
if (-not (Test-Path $regBase)) {
    Write-Host "   ⚠ 未找到 $regBase" -ForegroundColor Yellow
    Write-Host "   AutoCAD $AcadVersion 似乎未安装，跳过注册表写入" -ForegroundColor Yellow
    Write-Host "   可用版本：" -ForegroundColor Yellow
    Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "     - $($_.PSChildName)" -ForegroundColor Yellow
    }
} else {
    $regCount = 0
    Get-ChildItem $regBase -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
        $appKey = Join-Path $_.PSPath "Applications\HalouSuite"
        New-Item $appKey -Force | Out-Null
        Set-ItemProperty $appKey -Name LOADER -Value (Join-Path $bin "HalouHost.dll")
        Set-ItemProperty $appKey -Name LOADCTRLS -Value 2 -Type DWord
        Set-ItemProperty $appKey -Name MANAGED -Value 1 -Type DWord
        Set-ItemProperty $appKey -Name DESCRIPTION -Value "Halou 插件集合"
        Write-Host "   ✓ 写入 $($_.PSChildName)\Applications\HalouSuite" -ForegroundColor Green
        $regCount++
    }
    if ($regCount -eq 0) {
        Write-Host "   ⚠ $regBase 下没有 ACAD-* 子键，跳过" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "===========================================" -ForegroundColor Green
Write-Host "  ✓ 部署完成" -ForegroundColor Green
Write-Host "===========================================" -ForegroundColor Green
Write-Host ""
Write-Host "下次启动 AutoCAD 将自动加载 NEW host + Payload。"
Write-Host "验证命令："
Write-Host "  HALOU         - 打开面板"
Write-Host "  HALOUSTATUS   - 查看版本状态"
Write-Host "  HALOURELOAD   - 热重载 Payload"
Write-Host "  HALOULKG      - 回退到上一个可用版本"
Write-Host ""
Write-Host "面板「下载新版本」按钮可直接热更新到最新 Payload，无需关闭 AutoCAD。"
Write-Host ""
