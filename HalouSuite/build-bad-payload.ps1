# 构建一个故意不兼容的 Payload (API=99)，用于验证 Host 拒绝并回退 LKG。
# 输出到 Payload\dist\HalouPayload.99.0.0.dll
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$dist = Join-Path $root 'Payload\dist'
$contractDll = Join-Path $root 'Contract\dist\HalouContract.dll'
$tmp = Join-Path $env:TEMP 'PayloadBad99.cs'
$out = Join-Path $dist 'HalouPayload.99.0.0.dll'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $contractDll)) { throw "先 build-all.ps1 一下" }
New-Item -ItemType Directory -Force $dist | Out-Null

@'
using System;
using HalouSuite.Contract;

namespace HalouSuite.Payload
{
    public sealed class PayloadEntry : IPayload
    {
        public string Version { get { return "99.0.0-incompatible"; } }
        public int RequiredHostApiLevel { get { return 99; } }
        public void Activate(IHostServices host) { throw new InvalidOperationException("This payload should never be activated"); }
        public void Dispose() { }
        public void ShowPalette() { }
        public void TogglePalette() { }
        public void RefreshManifest(bool m) { }
        public void RunFeatureById(string id) { }
        public void HookPasteClip(bool s) { }
        public void UnhookPasteClip() { }
        public void PasteFromClipboard() { }
        public bool PasteClipOverrideHandled() { return false; }
        public void PasteFromFile() { }
        public bool IsFeatureAuthorized(string id) { return true; }
        public bool JtEmbedDwg(string a, string b) { return false; }
        public bool JtExtractDwg(string a, string b) { return false; }
        public bool JtCropWhite(string a, string b, int p, int t) { return false; }
        public bool JtUpscalePng(string a, string b, double s) { return false; }
        public bool JtPngToClipboard(string a) { return false; }
        public bool JtMergePngHorizontal(string[] a, string b, int g) { return false; }
        public bool JtPngsToClipboard(string[] a) { return false; }
        public bool JtPlotPng(string a, string b) { return false; }
    }
}
'@ | Set-Content -Path $tmp -Encoding UTF8

& $csc /nologo /target:library /platform:x64 /out:$out /r:"$contractDll" $tmp
if ($LASTEXITCODE -ne 0) { throw "build bad payload failed ($LASTEXITCODE)" }
Write-Host "Built: $out" -ForegroundColor Green
