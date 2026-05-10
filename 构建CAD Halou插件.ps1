$ErrorActionPreference = 'Stop'

$script = Get-ChildItem -Path $PSScriptRoot -Filter 'build-autocad-plugin.ps1' -Recurse -File -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not (Test-Path $script)) {
    throw "Build script not found: $script"
}

& $script
