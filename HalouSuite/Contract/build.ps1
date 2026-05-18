param(
    [string]$Configuration = 'Release',
    [string]$ArxTag = '',
    [ValidateSet('net48', 'net8.0-windows')]
    [string]$TargetFramework = 'net48'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$dist = Join-Path $projectRoot 'dist'
$outName = if ($ArxTag) { "HalouContract.$ArxTag.dll" } else { 'HalouContract.dll' }
$out = Join-Path $dist $outName

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

$srcFiles = @(Get-ChildItem -Path $projectRoot -Filter *.cs -File | ForEach-Object { $_.FullName })
if ($srcFiles.Count -eq 0) { throw "Contract 没有 .cs 源文件" }

New-Item -ItemType Directory -Force $dist | Out-Null

if ($TargetFramework -eq 'net8.0-windows') {
    $assets = Ensure-Net8BuildAssets
    # tagged 文件只是发布名；程序集名必须仍是 HalouContract，否则部署为 HalouContract.dll 后依赖解析会失败。
    $compileDir = if ($ArxTag) { Join-Path $dist ("_build\" + $ArxTag) } else { $dist }
    New-Item -ItemType Directory -Force $compileDir | Out-Null
    $compileOut = Join-Path $compileDir 'HalouContract.dll'
    $refs = @(Get-ChildItem $assets.CoreRef -Filter '*.dll' | ForEach-Object { "/r:$($_.FullName)" })
    & $assets.Compiler /nologo /noconfig /nostdlib /target:library /platform:anycpu /optimize+ /define:HALOU_NET8,NET8_0_OR_GREATER "/out:$compileOut" @refs @srcFiles
    if ($LASTEXITCODE -ne 0) { throw "Contract 构建失败 ($LASTEXITCODE)" }
    if ($compileOut -ne $out) { Copy-Item $compileOut $out -Force }
} else {
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "C# 编译器不存在: $csc" }
    & $csc /nologo /target:library /platform:anycpu /optimize+ /out:$out `
        /r:System.dll `
        @srcFiles
}

if ($LASTEXITCODE -ne 0) { throw "Contract 构建失败 ($LASTEXITCODE)" }
Write-Host "Built: $out" -ForegroundColor Green
