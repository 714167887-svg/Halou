param(
    [string]$ProjectRoot = $(Join-Path $PSScriptRoot "..\autocad-clipboard-plugin-autoon-test")
)

$ErrorActionPreference = 'Stop'

$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$srcDir = Join-Path $ProjectRoot 'src'
$out = Join-Path $ProjectRoot 'dist\JsqClipboardCadPlugin.dll'
$manifestSrc = Join-Path $ProjectRoot 'manifest\halou-plugin-manifest.json'
$manifestOut = Join-Path $ProjectRoot 'dist\halou-plugin-manifest.json'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpfDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$autocadDir = 'C:\Program Files\Autodesk\AutoCAD 2021'

if (-not (Test-Path $srcDir)) {
    throw "Source directory not found: $srcDir"
}

# 收集 src 下所有 .cs 文件（拆分后由多文件组成）
$srcFiles = Get-ChildItem -Path $srcDir -Filter *.cs -Recurse -File | ForEach-Object { $_.FullName }
if (-not $srcFiles -or $srcFiles.Count -eq 0) {
    throw "No .cs source files found under: $srcDir"
}
Write-Host ("Source files: {0}" -f $srcFiles.Count)

if (-not (Test-Path $csc)) {
    throw "C# compiler not found: $csc"
}

if (-not (Test-Path (Join-Path $autocadDir 'acmgd.dll'))) {
    throw "AutoCAD managed DLLs not found under: $autocadDir"
}

New-Item -ItemType Directory -Force (Join-Path $ProjectRoot 'dist') | Out-Null

# 收集要嵌入进 DLL 的 payload 文件（LSP/辅助脚本）。
# 规则：解析 manifest 中每个功能的 LoadPath，若能在 W/ 下定位到文件，则连同其所在子目录的
# 所有同族辅助文件（lsp / ps1 / txt / json）一起嵌入；资源命名 Payload.<子目录>.<文件名>，
# 运行时由 ExtractEmbeddedPayloads() 解到 DLL 旁的同名子目录。
$wRoot = Split-Path -Parent $ProjectRoot
$payloadArgs = @()
$payloadDirs = New-Object System.Collections.Generic.HashSet[string]

if (Test-Path $manifestSrc) {
    try {
        $manifestJson = Get-Content -LiteralPath $manifestSrc -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($feat in $manifestJson.Features) {
            $lp = $feat.LoadPath
            if (-not $lp) { continue }
            # 取顶层目录名（manifest 写法：OLE/oleimgdir.lsp -> OLE）
            $topDir = ($lp -split '[\\/]')[0]
            if (-not $topDir -or $topDir -in @('..', '.')) { continue }
            $fullDir = Join-Path $wRoot $topDir
            if (Test-Path $fullDir -PathType Container) {
                [void]$payloadDirs.Add($topDir)
            }
        }
    } catch {
        Write-Host "[warn] 解析 manifest 失败，跳过自动嵌入：$_" -ForegroundColor Yellow
    }
}

function Convert-LspToGbk($filePath) {
    $b = [IO.File]::ReadAllBytes($filePath)
    if ($b.Length -lt 1) { return $false }
    $hasBom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
    $offset = 0
    if ($hasBom) { $offset = 3 }
    $isUtf8 = $hasBom
    if (-not $isUtf8) {
        try {
            $strict = New-Object Text.UTF8Encoding($false, $true)
            $null = $strict.GetString($b, $offset, $b.Length - $offset)
            $gbk = [Text.Encoding]::GetEncoding(936)
            $roundtrip = $gbk.GetBytes($gbk.GetString($b))
            $diff = $false
            if ($roundtrip.Length -ne $b.Length) { $diff = $true }
            else { for ($k = 0; $k -lt $b.Length; $k++) { if ($roundtrip[$k] -ne $b[$k]) { $diff = $true; break } } }
            if ($diff) { $isUtf8 = $true }
        } catch { $isUtf8 = $false }
    }
    if ($isUtf8) {
        $text = [Text.Encoding]::UTF8.GetString($b, $offset, $b.Length - $offset)
        $gbkBytes = [Text.Encoding]::GetEncoding(936).GetBytes($text)
        [IO.File]::WriteAllBytes($filePath, $gbkBytes)
        return $true
    }
    return $false
}

foreach ($subdir in $payloadDirs) {
    $dirPath = Join-Path $wRoot $subdir
    Get-ChildItem -Path $dirPath -File -Recurse:$false |
        Where-Object { $_.Extension -in '.lsp', '.ps1' } |
        ForEach-Object {
            # 编码守门：AutoLISP 在中文 Windows 按 GBK 读 .lsp，
            # 若实为 UTF-8 会让 lexer 错位，报"文件加载已取消"。
            if ($_.Extension -eq '.lsp') {
                if (Convert-LspToGbk $_.FullName) {
                    Write-Host "  [encoding-fix] $subdir/$($_.Name) UTF-8 -> GBK" -ForegroundColor Yellow
                }
            }
            $resName = "Payload.$subdir.$($_.Name)"
            $payloadArgs += "/resource:$($_.FullName),$resName"
            Write-Host "  embed: $subdir/$($_.Name)"
        }
}

# 嵌入 manifest 本身：v1.1.5+ 启动时若本地 manifest 缺失或来自旧版本，
# 由 ExtractEmbeddedPayloads() 用此嵌入版本覆盖一次（stamp 机制防回退）。
if (Test-Path $manifestSrc) {
    $payloadArgs += "/resource:$manifestSrc,EmbeddedManifest.halou-plugin-manifest.json"
    Write-Host "  embed: halou-plugin-manifest.json (root)"
}

& $csc `
    /nologo `
    /target:library `
    /platform:x64 `
    /optimize+ `
    /out:$out `
    /r:"$autocadDir\acdbmgd.dll" `
    /r:"$autocadDir\acmgd.dll" `
    /r:"$autocadDir\accoremgd.dll" `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    /r:"$wpfDir\WindowsBase.dll" `
    /r:"$wpfDir\PresentationCore.dll" `
    /r:"$wpfDir\PresentationFramework.dll" `
    /r:System.Web.Extensions.dll `
    @payloadArgs `
    @srcFiles

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Copy-Item $manifestSrc $manifestOut -Force

Write-Host "Built: $out"
Write-Host "Copied manifest: $manifestOut"

# 同步到 release 分发目录
$releaseDir = Join-Path $PSScriptRoot 'release'
if (Test-Path $releaseDir) {
    Copy-Item $out (Join-Path $releaseDir 'JsqClipboardCadPlugin.dll') -Force
    Copy-Item $manifestSrc (Join-Path $releaseDir 'halou-plugin-manifest.json') -Force
    Write-Host "Synced to release: $releaseDir"
}

