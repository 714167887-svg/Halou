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
    AutoCAD 版本号。不传时自动扫描 HKCU 已安装 AutoCAD 版本并选择最高版本。
    默认自动识别；可显式指定：R24.0（2021）、R24.1（2022）、R24.2（2023）、R24.3（2024）、R25.0（2025）、R25.1（2026）
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
    [string]$AcadVersion = "",
    # 多 ARX SDK 发布模式：指定部署哪个 tag。不传则根据 $AcadVersion 推断默认值。
    # AutoCAD 2021/2022/2023 (R24.0/R24.1/R24.2) -> arx24
    # AutoCAD 2024+      (R24.3/R25.x)         -> arx25
    [string]$ArxTag = "",
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

function Resolve-ArxTag {
    param([string]$acadVer)
    if ([string]::IsNullOrWhiteSpace($acadVer)) { return $null }
    if ($acadVer -match '^R(\d+)\.(\d+)$') {
        $major = [int]$Matches[1]; $minor = [int]$Matches[2]
        # R24.0 / R24.1 / R24.2 = acad 2021/2022/2023 -> ARX 24
        if ($major -eq 24 -and $minor -le 2) { return 'arx24' }
        # R24.3 = acad 2024 -> ARX 25；R25.* = acad 2025+ -> ARX 25
        if (($major -eq 24 -and $minor -ge 3) -or $major -ge 25) { return 'arx25' }
    }
    return $null
}

function Get-InstalledAcadVersions {
    $regBase = "HKCU:\Software\Autodesk\AutoCAD"
    if (-not (Test-Path $regBase)) { return @() }
    @(Get-ChildItem $regBase -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^R\d+\.\d+$' } |
        ForEach-Object {
            $name = $_.PSChildName
            $score = 0
            if ($name -match '^R(\d+)\.(\d+)$') { $score = [int]$Matches[1] * 100 + [int]$Matches[2] }
            [pscustomobject]@{ Name = $name; Score = $score }
        } |
        Sort-Object Score -Descending)
}

function Get-HalouTrustedPaths {
    $temp = $env:TEMP
    if ([string]::IsNullOrWhiteSpace($temp)) { $temp = [System.IO.Path]::GetTempPath() }
    $runtime = Join-Path $temp 'HalouSuite\runtime'
    @(
        "$env:LOCALAPPDATA\HalouSuite",
        "$env:LOCALAPPDATA\HalouSuite\bin",
        "$env:LOCALAPPDATA\HalouSuite\payloads",
        "$env:LOCALAPPDATA\HalouSuite\...",
        $runtime,
        (Join-Path $temp 'HalouSuite\...')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Add-TrustedPathsToProfile {
    param(
        [string]$VariablesKey,
        [string[]]$Paths
    )

    New-Item $VariablesKey -Force | Out-Null
    $existing = ''
    try { $existing = (Get-ItemProperty $VariablesKey -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS } catch { }
    $items = @()
    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        $items += ($existing -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    foreach ($path in $Paths) {
        if ($items -notcontains $path) { $items += $path }
    }
    Set-ItemProperty $VariablesKey -Name TRUSTEDPATHS -Value ($items -join ';')
}

function Register-HalouTrustedPaths {
    param([string]$AcadRoot)

    if (-not (Test-Path $AcadRoot)) { return 0 }
    $paths = @(Get-HalouTrustedPaths)
    $count = 0
    Get-ChildItem $AcadRoot -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'ACAD-*' } | ForEach-Object {
        $profilesKey = Join-Path $_.PSPath 'Profiles'
        if (Test-Path $profilesKey) {
            Get-ChildItem $profilesKey -ErrorAction SilentlyContinue | ForEach-Object {
                $variablesKey = Join-Path $_.PSPath 'Variables'
                Add-TrustedPathsToProfile -VariablesKey $variablesKey -Paths $paths
                Write-Host "   ✓ TRUSTEDPATHS：$($_.PSChildName)" -ForegroundColor Green
                $script:trustedProfileCount++
            }
        }
    }
    return $count
}

$here = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    # 平铺客户包：install.ps1 与 HalouHost/HalouPayload 同目录，优先直接使用当前目录。
    # 开发仓库：再回退到 halou-release 与 Halou 同级的布局。
    if ((Test-Path (Join-Path $here 'HalouContract.dll')) -and
        ((Get-ChildItem $here -Filter 'HalouHost*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1) -ne $null) -and
        ((Get-ChildItem $here -Filter 'HalouPayload*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1) -ne $null)) {
        $ReleaseDir = $here
    } else {
        $ReleaseDir = Join-Path $here "..\halou-release\release"
    }
}
$ReleaseDir = (Resolve-Path $ReleaseDir -ErrorAction SilentlyContinue)
if (-not $ReleaseDir -or -not (Test-Path $ReleaseDir)) {
    Write-Host "❌ 找不到 halou-release/release 目录" -ForegroundColor Red
    Write-Host "   请确认 halou-release 已 clone，并用 -ReleaseDir 指定路径" -ForegroundColor Yellow
    exit 1
}

$bin = "$env:LOCALAPPDATA\HalouSuite\bin"
$payloads = "$env:LOCALAPPDATA\HalouSuite\payloads"

if ([string]::IsNullOrWhiteSpace($AcadVersion)) {
    $installedAcadVersions = @(Get-InstalledAcadVersions)
    if ($installedAcadVersions.Count -gt 0) {
        $AcadVersion = $installedAcadVersions[0].Name
        Write-Host "自动识别 AutoCAD 版本：$AcadVersion（如需指定，可加 -AcadVersion R25.0）" -ForegroundColor Cyan
    } else {
        $AcadVersion = 'R24.0'
        Write-Host "未在 HKCU 识别到 AutoCAD 版本，临时回退到 $AcadVersion；如是 2025 请用 -AcadVersion R25.0。" -ForegroundColor Yellow
    }
}

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

# 推断 ArxTag：未显式指定则按 AcadVersion 自动推导
if ([string]::IsNullOrWhiteSpace($ArxTag)) {
    $ArxTag = Resolve-ArxTag $AcadVersion
}

# 选 host DLL：优先用 HalouHost.<tag>.dll（多 SDK release），回退到 HalouHost.dll（单 SDK release）
$hostDll = $null
if (-not [string]::IsNullOrWhiteSpace($ArxTag)) {
    $tagged = Join-Path $ReleaseDir ("HalouHost." + $ArxTag + ".dll")
    if (Test-Path $tagged) {
        $hostDll = $tagged
        Write-Host "   选择 host: HalouHost.$ArxTag.dll (按 $AcadVersion 推断)" -ForegroundColor Cyan
    }
}
if (-not $hostDll) {
    $legacy = Join-Path $ReleaseDir "HalouHost.dll"
    if (Test-Path $legacy) {
        $hostDll = $legacy
        if (-not [string]::IsNullOrWhiteSpace($ArxTag)) {
            Write-Host "   release 中无 HalouHost.$ArxTag.dll，回退到 HalouHost.dll" -ForegroundColor DarkYellow
        }
    }
}
# 选 Contract DLL：AutoCAD 2025 的 Host/Payload 是 .NET 8，必须配套 HalouContract.arx25.dll；部署时仍重命名为 HalouContract.dll。
$contractDll = $null
if (-not [string]::IsNullOrWhiteSpace($ArxTag)) {
    $taggedContract = Join-Path $ReleaseDir ("HalouContract." + $ArxTag + ".dll")
    if (Test-Path $taggedContract) {
        $contractDll = $taggedContract
        Write-Host "   选择 contract: HalouContract.$ArxTag.dll" -ForegroundColor Cyan
    }
}
if (-not $contractDll) { $contractDll = Join-Path $ReleaseDir "HalouContract.dll" }
$manifest    = Join-Path $ReleaseDir "halou-plugin-manifest.json"

foreach ($f in @($hostDll, $contractDll)) {
    if (-not $f -or -not (Test-Path $f)) {
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

# 优先找 HalouPayload.<ver>.<tag>.dll；回退到无 tag 后缀的 HalouPayload.<ver>.dll
$candidates = @()
if (-not [string]::IsNullOrWhiteSpace($ArxTag)) {
    $candidates = Get-ChildItem $ReleaseDir -Filter ("HalouPayload.*." + $ArxTag + ".dll") -ErrorAction SilentlyContinue
    if ($candidates.Count -gt 0) {
        Write-Host "   选择 payload 调用 $ArxTag 版本" -ForegroundColor Cyan
    }
}
if ($candidates.Count -eq 0) {
    # 单 SDK release 或 release 里没有该 tag 的产物：退回无 tag 后缀的 payload
    $candidates = Get-ChildItem $ReleaseDir -Filter "HalouPayload.*.dll" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^HalouPayload\.[\d\.]+(?:-[A-Za-z0-9\.]+)?\.dll$' }
    if (-not [string]::IsNullOrWhiteSpace($ArxTag) -and $candidates.Count -gt 0) {
        Write-Host "   release 中无 .$ArxTag.dll 后缀产物，回退到无 tag 的 HalouPayload.<ver>.dll" -ForegroundColor DarkYellow
    }
}
if ($candidates.Count -eq 0) {
    Write-Host "❌ halou-release/release/ 下没有可用的 HalouPayload.*.dll" -ForegroundColor Red
    exit 1
}

# 按版本号排序取最新（X.Y.Z 三位数字字典序够用）
$latest = $candidates | Sort-Object {
    if ($_.Name -match 'HalouPayload\.(\d+)\.(\d+)\.(\d+)') {
        [int]$matches[1]*1000000 + [int]$matches[2]*1000 + [int]$matches[3]
    } else { 0 }
} | Select-Object -Last 1

# 部署名：HalouHost 加载时不认带 ArxTag 后缀的名字，需要重命名为 HalouPayload.<ver>.dll
if ($latest.Name -match '^HalouPayload\.(\d+\.\d+\.\d+(?:-[A-Za-z0-9\.]+)?)\.[A-Za-z0-9]+\.dll$') {
    $deployName = "HalouPayload.$($Matches[1]).dll"
} else {
    $deployName = $latest.Name
}
Copy-Item $latest.FullName (Join-Path $payloads $deployName) -Force
Write-Host "   ✓ $($latest.Name) → $deployName 已部署" -ForegroundColor Green

# 写 lkg
$lkg = Join-Path $payloads $deployName
Set-Content -Path "$env:LOCALAPPDATA\HalouSuite\state\lkg.txt" -Value $lkg -Encoding ASCII
Write-Host "   ✓ lkg.txt -> $deployName" -ForegroundColor Green

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

# ---- 7. 加入 AutoCAD 受信任路径 ----
Write-Host ""
Write-Host "==> 7. 写入 AutoCAD 受信任路径" -ForegroundColor Cyan
$script:trustedProfileCount = 0
[void](Register-HalouTrustedPaths -AcadRoot $regBase)
if ($script:trustedProfileCount -gt 0) {
    Write-Host "   ✓ 已把 HalouSuite bin/payloads/runtime 加入 TRUSTEDPATHS" -ForegroundColor Green
    Write-Host "     保持 SECURELOAD 开启也不会再弹 HalouHost.dll / LSP 加载确认。" -ForegroundColor DarkYellow
} else {
    Write-Host "   ⚠ 未找到 AutoCAD Profiles，未能自动写 TRUSTEDPATHS" -ForegroundColor Yellow
    Write-Host "     可在 AutoCAD 里执行 TRUSTEDPATHS，手工加入：$env:LOCALAPPDATA\HalouSuite\..." -ForegroundColor Yellow
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
