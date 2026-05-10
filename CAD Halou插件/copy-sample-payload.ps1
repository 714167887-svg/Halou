param(
    [string]$ProjectRoot = $(Join-Path $PSScriptRoot "..\autocad-clipboard-plugin-autoon-test")
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$payloadPath = Join-Path $ProjectRoot 'demo\sample-payload.json'
$payload = Get-Content $payloadPath -Raw -Encoding UTF8
Set-Clipboard -Value ("JSQCAD:" + $payload)
Write-Host "Sample payload copied to clipboard."
