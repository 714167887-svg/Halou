$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$wRoot = Split-Path -Parent $projectRoot
$centralBuild = Get-ChildItem -Path $wRoot -Filter 'build-autocad-plugin.ps1' -Recurse -File -ErrorAction SilentlyContinue |
	Select-Object -First 1 -ExpandProperty FullName
if (-not (Test-Path $centralBuild)) {
	throw "Central script directory not found."
}

$centralScript = Join-Path (Split-Path -Parent $centralBuild) 'copy-sample-payload.ps1'

if (-not (Test-Path $centralScript)) {
	throw "Central payload script not found: $centralScript"
}

& $centralScript -ProjectRoot $projectRoot

