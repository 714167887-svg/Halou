# 还原烟测前的注册表 LOADER 值，回到 1.1.74 单 DLL 模式。
# 默认读 backup\latest.json；可用 -BackupFile 指定具体备份。

param(
    [string]$BackupFile,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$BackupDir = Join-Path $env:LOCALAPPDATA 'HalouSuite\backup'

if (-not $BackupFile) {
    $BackupFile = Join-Path $BackupDir 'latest.json'
}

if (-not (Test-Path $BackupFile)) {
    throw "未找到备份文件: $BackupFile"
}

if (-not $Force) {
    $running = Get-Process acad -ErrorAction SilentlyContinue
    if ($running) { throw "检测到 acad.exe 正在运行，请先全部退出。" }
}

$backup = Get-Content $BackupFile -Raw | ConvertFrom-Json
Write-Host "`n=== 还原备份 ===" -ForegroundColor Cyan
Write-Host "备份时间: $($backup.BackupTime)"
Write-Host ""

foreach ($e in $backup.Entries) {
    if (-not (Test-Path $e.RegPath)) {
        Write-Host "  [跳过] 注册表项已不存在: $($e.RegPath)" -ForegroundColor Yellow
        continue
    }
    Set-ItemProperty -Path $e.RegPath -Name 'LOADER'      -Value $e.LOADER      -Type String
    Set-ItemProperty -Path $e.RegPath -Name 'DESCRIPTION' -Value $e.DESCRIPTION -Type String
    Set-ItemProperty -Path $e.RegPath -Name 'LOADCTRLS'   -Value $e.LOADCTRLS   -Type DWord
    Set-ItemProperty -Path $e.RegPath -Name 'MANAGED'     -Value $e.MANAGED     -Type DWord
    Write-Host "  还原: $($e.RegPath)"
    Write-Host "    LOADER = $($e.LOADER)" -ForegroundColor Green
}

Write-Host "`n=== 还原完成 ===" -ForegroundColor Green
Write-Host "下次启动 AutoCAD 将加载原 1.1.74 DLL。"
