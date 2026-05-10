param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$out = Join-Path $projectRoot 'dist\HalouContract.dll'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) { throw "C# 编译器不存在: $csc" }

$srcFiles = @(Get-ChildItem -Path $projectRoot -Filter *.cs -File | ForEach-Object { $_.FullName })
if ($srcFiles.Count -eq 0) { throw "Contract 没有 .cs 源文件" }

New-Item -ItemType Directory -Force (Join-Path $projectRoot 'dist') | Out-Null

& $csc /nologo /target:library /platform:anycpu /optimize+ /out:$out `
    /r:System.dll `
    @srcFiles

if ($LASTEXITCODE -ne 0) { throw "Contract 构建失败 ($LASTEXITCODE)" }
Write-Host "Built: $out" -ForegroundColor Green
