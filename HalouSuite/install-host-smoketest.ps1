# Halou Host+Payload 骨架烟测脚本
# 作用：把当前 1.1.74 单 DLL 加载方式临时切换成 Host+Payload，验证热重载链路。
# 完全可逆：所有改动写入 backup\registry-<时间戳>.json，rollback-smoketest.ps1 一键还原。

param(
    [switch]$Force  # 跳过 acad.exe 进程检查
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $PSCommandPath

# ===== 路径定义 =====
$HostDll      = Join-Path $ScriptDir 'Host\dist\HalouHost.dll'
$ContractDll  = Join-Path $ScriptDir 'Contract\dist\HalouContract.dll'
$PayloadDll   = Join-Path $ScriptDir 'Payload\dist\HalouPayload.2.0.0.dll'

$BackupDir    = Join-Path $env:LOCALAPPDATA 'HalouSuite\backup'
$PayloadDir   = Join-Path $env:LOCALAPPDATA 'HalouSuite\payloads'

# ===== 前置检查 =====
foreach ($f in @($HostDll, $ContractDll, $PayloadDll)) {
    if (-not (Test-Path $f)) { throw "缺少构建产物: $f`n请先运行 .\build-all.ps1" }
}

if (-not $Force) {
    $running = Get-Process acad -ErrorAction SilentlyContinue
    if ($running) {
        throw "检测到 acad.exe 正在运行，请先全部退出再装。强制装请加 -Force（不建议）。"
    }
}

# ===== 1. 备份当前注册表 LOADER 值 =====
Write-Host "`n=== [1/3] 备份当前注册表 ===" -ForegroundColor Cyan
$rootPath = 'HKCU:\Software\Autodesk\AutoCAD'
if (-not (Test-Path $rootPath)) { throw "未找到 HKCU\\Software\\Autodesk\\AutoCAD" }

$backup = @{
    Timestamp  = (Get-Date).ToString('yyyy-MM-dd_HH-mm-ss')
    BackupTime = (Get-Date).ToString('o')
    Entries    = @()
}

Get-ChildItem $rootPath -ErrorAction SilentlyContinue | ForEach-Object {
    Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'ACAD-*' } | ForEach-Object {
        $entry = Join-Path (Join-Path $_.PSPath 'Applications') 'HalouSuite'
        if (Test-Path $entry) {
            $props = Get-ItemProperty -Path $entry
            $backup.Entries += @{
                RegPath     = $entry
                LOADER      = $props.LOADER
                DESCRIPTION = $props.DESCRIPTION
                LOADCTRLS   = $props.LOADCTRLS
                MANAGED     = $props.MANAGED
            }
            Write-Host "  备份: $entry"
            Write-Host "    旧 LOADER = $($props.LOADER)" -ForegroundColor Gray
        }
    }
}

if ($backup.Entries.Count -eq 0) {
    throw "没有找到任何已注册的 HalouSuite 入口。请先用旧 install.ps1 装一次 1.1.74。"
}

New-Item -ItemType Directory -Force $BackupDir | Out-Null
$backupFile = Join-Path $BackupDir "registry-$($backup.Timestamp).json"
$backup | ConvertTo-Json -Depth 4 | Set-Content -Path $backupFile -Encoding UTF8
Write-Host "  → 备份文件: $backupFile" -ForegroundColor Green

# 同时维护一个 latest.json 供 rollback 默认读取
Copy-Item $backupFile (Join-Path $BackupDir 'latest.json') -Force

# ===== 2. 部署 payload 到 %LOCALAPPDATA%\HalouSuite\payloads\ =====
Write-Host "`n=== [2/3] 部署 Payload ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $PayloadDir | Out-Null
$payloadTarget = Join-Path $PayloadDir (Split-Path -Leaf $PayloadDll)
Copy-Item $PayloadDll $payloadTarget -Force
Write-Host "  → $payloadTarget" -ForegroundColor Green

# Contract.dll 已经由 Host\build.ps1 同步到 Host\dist\，保险起见再确认一次
$contractInHostDir = Join-Path (Split-Path -Parent $HostDll) 'HalouContract.dll'
if (-not (Test-Path $contractInHostDir)) {
    Copy-Item $ContractDll $contractInHostDir -Force
    Write-Host "  → 补齐 Contract.dll 到 Host 目录" -ForegroundColor Yellow
}

# ===== 3. 改写注册表 LOADER 指向 HalouHost.dll =====
Write-Host "`n=== [3/3] 切换 LOADER -> HalouHost.dll ===" -ForegroundColor Cyan
foreach ($e in $backup.Entries) {
    Set-ItemProperty -Path $e.RegPath -Name 'LOADER' -Value $HostDll -Type String
    Set-ItemProperty -Path $e.RegPath -Name 'DESCRIPTION' -Value 'Halou 插件集合 (烟测 host)' -Type String
    Set-ItemProperty -Path $e.RegPath -Name 'MANAGED' -Value 1 -Type DWord
    Set-ItemProperty -Path $e.RegPath -Name 'LOADCTRLS' -Value 2 -Type DWord
    Write-Host "  $($e.RegPath)"
    Write-Host "    新 LOADER = $HostDll" -ForegroundColor Green
}

Write-Host "`n=== 烟测部署完成 ===" -ForegroundColor Green
Write-Host ""
Write-Host "下一步：" -ForegroundColor Yellow
Write-Host "  1. 启动 AutoCAD 2021"
Write-Host "  2. 命令行应出现：Halou Host v2.0.0 已加载，Payload v2.0.0-stub"
Write-Host "  3. 输入 HALOU → 应见 [HalouPayload stub] 收到 HALOU"
Write-Host "  4. 输入 HALOURELOAD → 应见 Payload 已切换..."
Write-Host ""
Write-Host "回滚：" -ForegroundColor Yellow
Write-Host "  退出 acad → 运行  .\rollback-smoketest.ps1"
Write-Host ""
Write-Host "Host 路径： $HostDll"
Write-Host "Payload 目录：$PayloadDir"
Write-Host "备份文件： $backupFile"
