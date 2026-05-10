$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$wRoot = Split-Path -Parent $projectRoot
$centralScript = Get-ChildItem -Path $wRoot -Filter 'build-autocad-plugin.ps1' -Recurse -File -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName

if (-not (Test-Path $centralScript)) {
    throw "Central build script not found: $centralScript"
}

& $centralScript -ProjectRoot $projectRoot

