param(
    [string]$PayloadVersion = '2.0.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "=== [1/3] 构建 Contract ===" -ForegroundColor Cyan
& (Join-Path $root 'Contract\build.ps1')

Write-Host "=== [2/3] 构建 Host ===" -ForegroundColor Cyan
& (Join-Path $root 'Host\build.ps1')

Write-Host "=== [3/3] 构建 Payload v$PayloadVersion ===" -ForegroundColor Cyan
& (Join-Path $root 'Payload\build.ps1') -Version $PayloadVersion

Write-Host ""
Write-Host "=== 构建完成 ===" -ForegroundColor Green
Write-Host "  Contract: $(Join-Path $root 'Contract\dist\HalouContract.dll')"
Write-Host "  Host    : $(Join-Path $root 'Host\dist\HalouHost.dll')"
Write-Host "  Payload : $(Join-Path $root ('Payload\dist\HalouPayload.' + $PayloadVersion + '.dll'))"
