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
$autocadDir = $AutocadDir

if (-not (Test-Path (Join-Path $autocadDir 'acmgd.dll'))) { throw "AutoCAD 托管库未找到: $autocadDir" }

function Get-AcadManagedMajor {
    param([string]$Dir)
    try { return [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $Dir 'acmgd.dll')).Version.Major } catch { return 0 }
}

function Ensure-Net8BuildAssets {
    $compiler = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.net.compilers.toolset\4.8.0\tasks\net472\csc.exe'
    $coreRefRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netcore.app.ref'
    $coreRef = Get-ChildItem $coreRefRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ((-not (Test-Path $compiler)) -or (-not $coreRef)) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw "缺少 dotnet，无法自动还原 .NET 8 引用包 / Roslyn 编译器。"
        }
        $tmp = Join-Path $env:TEMP 'halou-net8-build-assets'
        New-Item -ItemType Directory -Force $tmp | Out-Null
        $proj = Join-Path $tmp 'assets.csproj'
        Set-Content -Path $proj -Encoding UTF8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><EnableWindowsTargeting>true</EnableWindowsTargeting></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.Net.Compilers.Toolset" Version="4.8.0" PrivateAssets="all" /></ItemGroup></Project>'
        dotnet restore $proj -v:minimal | Write-Host
    }
    if (-not (Test-Path $compiler)) { throw "Roslyn 编译器不存在: $compiler" }
    $coreRef = Get-ChildItem $coreRefRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $coreRef) { throw "缺少 Microsoft.NETCore.App.Ref .NET 8 引用包。" }
    return @{ Compiler = $compiler; CoreRef = (Join-Path $coreRef.FullName 'ref\net8.0') }
}

$targetFramework = if ((Get-AcadManagedMajor $autocadDir) -ge 25) { 'net8.0-windows' } else { 'net48' }

# 先确保 Contract.dll 已构建
$contractDll = if ($ArxTag) { Join-Path $contractRoot "dist\HalouContract.$ArxTag.dll" } else { Join-Path $contractRoot 'dist\HalouContract.dll' }
if (-not (Test-Path $contractDll)) {
    Write-Host "Contract 未构建，先执行 Contract\build.ps1 ..." -ForegroundColor Yellow
    if ($ArxTag) { & (Join-Path $contractRoot 'build.ps1') -ArxTag $ArxTag -TargetFramework $targetFramework }
    else { & (Join-Path $contractRoot 'build.ps1') -TargetFramework $targetFramework }
}

$srcFiles = @(Get-ChildItem -Path $projectRoot -Filter *.cs -File | ForEach-Object { $_.FullName })
if ($srcFiles.Count -eq 0) { throw "Host 没有 .cs 源文件" }

New-Item -ItemType Directory -Force (Join-Path $projectRoot 'dist') | Out-Null

if ($targetFramework -eq 'net8.0-windows') {
    $assets = Ensure-Net8BuildAssets
    $refs = @(Get-ChildItem $assets.CoreRef -Filter '*.dll' | ForEach-Object { "/r:$($_.FullName)" })
    & $assets.Compiler /nologo /noconfig /nostdlib /target:library /platform:x64 /optimize+ /define:HALOU_NET8,NET8_0_OR_GREATER "/out:$out" `
        "/r:$contractDll" `
        "/r:$autocadDir\acdbmgd.dll" `
        "/r:$autocadDir\acmgd.dll" `
        "/r:$autocadDir\accoremgd.dll" `
        @refs `
        @srcFiles
} else {
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "C# 编译器不存在: $csc" }
    & $csc /nologo /target:library /platform:x64 /optimize+ /out:$out `
        /r:"$contractDll" `
        /r:"$autocadDir\acdbmgd.dll" `
        /r:"$autocadDir\acmgd.dll" `
        /r:"$autocadDir\accoremgd.dll" `
        /r:System.dll `
        /r:System.Core.dll `
        @srcFiles
}

if ($LASTEXITCODE -ne 0) { throw "Host 构建失败 ($LASTEXITCODE)" }

# Contract.dll 一并拷贝到 Host\dist 方便测试。tagged Contract 程序集名仍为 HalouContract。
$contractDistName = if ($ArxTag) { "dist\HalouContract.$ArxTag.dll" } else { 'dist\HalouContract.dll' }
Copy-Item $contractDll (Join-Path $projectRoot $contractDistName) -Force
Write-Host "Built: $out" -ForegroundColor Green
