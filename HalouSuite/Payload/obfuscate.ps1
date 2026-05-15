#requires -Version 5.1
param(
    [string]$InputDll,
    [string]$AutocadDir = 'C:\Program Files\Autodesk\AutoCAD 2021',
    [switch]$InPlace
)

$ErrorActionPreference = 'Stop'
$payloadRoot = $PSScriptRoot
$suiteRoot = Split-Path -Parent $payloadRoot
$repoRoot = Split-Path -Parent $suiteRoot

if ([string]::IsNullOrWhiteSpace($InputDll)) {
    $InputDll = Get-ChildItem (Join-Path $payloadRoot 'dist') -Filter 'HalouPayload.*.dll' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $InputDll -or -not (Test-Path $InputDll)) { throw "Input DLL not found: $InputDll" }
$InputDll = [System.IO.Path]::GetFullPath($InputDll)
$inputDir = Split-Path -Parent $InputDll
$outDir = Join-Path $inputDir 'obfuscated'
$configPath = Join-Path $inputDir 'obfuscar.generated.xml'
$contractDir = Join-Path $suiteRoot 'Contract\dist'
$frameworkDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$toolDir = Join-Path $repoRoot '.tools'

function XmlEscape([string]$s) {
    return [System.Security.SecurityElement]::Escape($s)
}

function Resolve-ObfuscarRunner {
    if (-not (Test-Path $toolDir)) {
        dotnet tool install --tool-path $toolDir obfuscar.globaltool | Out-Host
    }

    $net10 = Get-ChildItem $toolDir -Recurse -Filter 'GlobalTools.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\tools\net10.0\any\GlobalTools.dll' } |
        Select-Object -First 1 -ExpandProperty FullName
    if ($net10) { return @{ File = 'dotnet'; ArgsPrefix = @($net10) } }

    $exe = Join-Path $toolDir 'obfuscar.console.exe'
    if (Test-Path $exe) { return @{ File = $exe; ArgsPrefix = @() } }

    throw 'Obfuscar runner not found after tool install.'
}

New-Item -ItemType Directory -Force $outDir | Out-Null

$assemblySearchPaths = @($inputDir, $contractDir, $frameworkDir, $AutocadDir) |
    Where-Object { $_ -and (Test-Path $_) } |
    ForEach-Object { [System.IO.Path]::GetFullPath($_) }

$searchXml = ($assemblySearchPaths | ForEach-Object { '  <AssemblySearchPath path="' + (XmlEscape $_) + '" />' }) -join "`r`n"
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<Obfuscator>
  <Var name="InPath" value="$(XmlEscape $inputDir)" />
  <Var name="OutPath" value="$(XmlEscape $outDir)" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="ReuseNames" value="false" />
  <Var name="SkipGenerated" value="true" />
  <Var name="SkipSpecialName" value="true" />
$searchXml
  <Module file="$(XmlEscape $InputDll)">
    <SkipType name="HalouSuite.Payload.PayloadEntry" skipMethods="true" skipFields="true" skipProperties="true" skipEvents="true" />
  </Module>
</Obfuscator>
"@
$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
[System.IO.File]::WriteAllText($configPath, $xml, $utf8NoBom)

$runner = Resolve-ObfuscarRunner
$toolArgs = @($runner.ArgsPrefix) + @($configPath)
& $runner.File @toolArgs
if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed: $LASTEXITCODE" }

$outDll = Join-Path $outDir (Split-Path -Leaf $InputDll)
if (-not (Test-Path $outDll)) { throw "Obfuscated DLL was not created: $outDll" }

if ($InPlace) {
    $backup = $InputDll + '.preobfuscar'
    Copy-Item $InputDll $backup -Force
    Copy-Item $outDll $InputDll -Force
    Write-Host "Obfuscated in place: $InputDll"
    Write-Host "Backup: $backup"
} else {
    Write-Host "Obfuscated DLL: $outDll"
}
