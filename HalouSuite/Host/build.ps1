param(
    [string]$Configuration = 'Release',
    # 指定 ObjectARX/AutoCAD 安装目录（必须包含 acmgd.dll/acdbmgd.dll/accoremgd.dll）
    [string]$AutocadDir = 'C:\Program Files\Autodesk\AutoCAD 2021',
    # 可选的 ARX 标签：传入后输出文件名变成 HalouHost.<ArxTag>.dll；不传则保持 HalouHost.dll（向后兼容）
    [string]$ArxTag = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$contractRoot = Join-Path (Split-Path -Parent $projectRoot) 'Contract'
$outName = if ($ArxTag) { "HalouHost.$ArxTag.dll" } else { 'HalouHost.dll' }
$out = Join-Path $projectRoot "dist\$outName"
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$autocadDir = $AutocadDir

if (-not (Test-Path $csc)) { throw "C# 编译器不存在: $csc" }
if (-not (Test-Path (Join-Path $autocadDir 'acmgd.dll'))) { throw "AutoCAD 托管库未找到: $autocadDir" }

# 先确保 Contract.dll 已构建
$contractDll = Join-Path $contractRoot 'dist\HalouContract.dll'
if (-not (Test-Path $contractDll)) {
    Write-Host "Contract 未构建，先执行 Contract\build.ps1 ..." -ForegroundColor Yellow
    & (Join-Path $contractRoot 'build.ps1')
}

$srcFiles = @(Get-ChildItem -Path $projectRoot -Filter *.cs -File | ForEach-Object { $_.FullName })
if ($srcFiles.Count -eq 0) { throw "Host 没有 .cs 源文件" }

New-Item -ItemType Directory -Force (Join-Path $projectRoot 'dist') | Out-Null

& $csc /nologo /target:library /platform:x64 /optimize+ /out:$out `
    /r:"$contractDll" `
    /r:"$autocadDir\acdbmgd.dll" `
    /r:"$autocadDir\acmgd.dll" `
    /r:"$autocadDir\accoremgd.dll" `
    /r:System.dll `
    /r:System.Core.dll `
    @srcFiles

if ($LASTEXITCODE -ne 0) { throw "Host 构建失败 ($LASTEXITCODE)" }

# Contract.dll 一并拷贝到 Host\dist 方便测试
Copy-Item $contractDll (Join-Path $projectRoot 'dist\HalouContract.dll') -Force
Write-Host "Built: $out" -ForegroundColor Green
