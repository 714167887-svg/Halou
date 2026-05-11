param(
    [string]$Version = '2.0.0'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$contractRoot = Join-Path (Split-Path -Parent $projectRoot) 'Contract'
$dist = Join-Path $projectRoot 'dist'
$out = Join-Path $dist ("HalouPayload.$Version.dll")
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$autocadDir = 'C:\Program Files\Autodesk\AutoCAD 2021'

if (-not (Test-Path $csc)) { throw "C# 编译器不存在: $csc" }

# 确保 Contract.dll 已构建
$contractDll = Join-Path $contractRoot 'dist\HalouContract.dll'
if (-not (Test-Path $contractDll)) {
    Write-Host "Contract 未构建，先执行 Contract\build.ps1 ..." -ForegroundColor Yellow
    & (Join-Path $contractRoot 'build.ps1')
}

$srcFiles = @(Get-ChildItem -Path $projectRoot -Filter *.cs -File -Recurse | ForEach-Object { $_.FullName })
if ($srcFiles.Count -eq 0) { throw "Payload 没有 .cs 源文件" }

New-Item -ItemType Directory -Force $dist | Out-Null
# 清理旧版本，保留最近一份方便回退
Get-ChildItem $dist -Filter 'HalouPayload.*.dll' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne (Split-Path $out -Leaf) } |
    ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

# 阶段 1：Payload 暂时只引用 Contract.dll，未来真实业务搬入后会再引用 acmgd.dll 等
$autocadRefs = @()
if (Test-Path (Join-Path $autocadDir 'acmgd.dll')) {
    $autocadRefs = @(
        "/r:$autocadDir\acdbmgd.dll",
        "/r:$autocadDir\acmgd.dll",
        "/r:$autocadDir\accoremgd.dll"
    )
}

# v1.1.75: 嵌入 OLE 辅助脚本（ps1/lsp），与 OLD host 一致；
# 运行时 ExtractEmbeddedPayloads() 会按 "Payload.<subdir>.<filename>" 资源命名解压到 DLL 旁 OLE\
$payloadArgs = @()
$oleDir = Join-Path (Split-Path -Parent (Split-Path -Parent $projectRoot)) 'OLE'
if (Test-Path $oleDir) {
    Get-ChildItem -Path $oleDir -File -Recurse:$false |
        Where-Object { $_.Extension -in '.lsp', '.ps1' } |
        ForEach-Object {
            $resName = "Payload.OLE.$($_.Name)"
            $payloadArgs += "/resource:$($_.FullName),$resName"
            Write-Host "  embed: OLE/$($_.Name) -> $resName"
        }
} else {
    Write-Host "  [warn] OLE dir not found: $oleDir" -ForegroundColor Yellow
}

& $csc /nologo /target:library /platform:x64 /optimize+ /out:$out `
    /r:"$contractDll" `
    @autocadRefs `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll `
    /r:System.Runtime.Serialization.dll `
    /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" `
    /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" `
    @payloadArgs `
    @srcFiles

if ($LASTEXITCODE -ne 0) { throw "Payload 构建失败 ($LASTEXITCODE)" }
Write-Host "Built: $out" -ForegroundColor Green
