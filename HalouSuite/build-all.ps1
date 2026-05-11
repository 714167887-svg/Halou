<#
.SYNOPSIS
    构建 Phase 2 三件套（Contract / Host / Payload）。

.PARAMETER PayloadVersion
    Payload 版本号，如 2.0.10。

.PARAMETER ArxSdks
    可选的 "ARX 标签 -> ObjectARX/AutoCAD 安装目录" 字典，传入后会按每个 SDK 编一份带后缀的 DLL。
    不传则按默认 SDK 单编一份 HalouHost.dll / HalouPayload.<Ver>.dll（向后兼容）。

    例：@{ arx24 = 'C:\Program Files\Autodesk\AutoCAD 2021'; arx25 = 'C:\ObjectARX\2024' }
    输出：
      Host\dist\HalouHost.arx24.dll       Host\dist\HalouHost.arx25.dll
      Payload\dist\HalouPayload.2.0.10.arx24.dll   Payload\dist\HalouPayload.2.0.10.arx25.dll
#>
param(
    [string]$PayloadVersion = '2.0.0',
    [hashtable]$ArxSdks
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "=== [1/3] 构建 Contract ===" -ForegroundColor Cyan
& (Join-Path $root 'Contract\build.ps1')

if ($null -eq $ArxSdks -or $ArxSdks.Count -eq 0) {
    # 单 SDK 模式（默认）：与历史行为完全一致，输出无后缀文件名
    Write-Host "=== [2/3] 构建 Host (default SDK) ===" -ForegroundColor Cyan
    & (Join-Path $root 'Host\build.ps1')

    Write-Host "=== [3/3] 构建 Payload v$PayloadVersion (default SDK) ===" -ForegroundColor Cyan
    & (Join-Path $root 'Payload\build.ps1') -Version $PayloadVersion

    Write-Host ""
    Write-Host "=== 构建完成 ===" -ForegroundColor Green
    Write-Host "  Contract: $(Join-Path $root 'Contract\dist\HalouContract.dll')"
    Write-Host "  Host    : $(Join-Path $root 'Host\dist\HalouHost.dll')"
    Write-Host "  Payload : $(Join-Path $root ('Payload\dist\HalouPayload.' + $PayloadVersion + '.dll'))"
} else {
    # 多 SDK 模式：每个 SDK 编一份带 ArxTag 后缀的 DLL
    $i = 2
    $total = 1 + $ArxSdks.Count * 2
    foreach ($tag in $ArxSdks.Keys) {
        $sdk = $ArxSdks[$tag]
        Write-Host "=== [$i/$total] 构建 Host ($tag <- $sdk) ===" -ForegroundColor Cyan
        & (Join-Path $root 'Host\build.ps1') -AutocadDir $sdk -ArxTag $tag
        $i++
        Write-Host "=== [$i/$total] 构建 Payload v$PayloadVersion ($tag <- $sdk) ===" -ForegroundColor Cyan
        & (Join-Path $root 'Payload\build.ps1') -Version $PayloadVersion -AutocadDir $sdk -ArxTag $tag
        $i++
    }

    Write-Host ""
    Write-Host "=== 构建完成（多 SDK） ===" -ForegroundColor Green
    Write-Host "  Contract: $(Join-Path $root 'Contract\dist\HalouContract.dll')"
    foreach ($tag in $ArxSdks.Keys) {
        Write-Host "  Host[$tag]    : $(Join-Path $root ('Host\dist\HalouHost.' + $tag + '.dll'))"
        Write-Host "  Payload[$tag] : $(Join-Path $root ('Payload\dist\HalouPayload.' + $PayloadVersion + '.' + $tag + '.dll'))"
    }
}
