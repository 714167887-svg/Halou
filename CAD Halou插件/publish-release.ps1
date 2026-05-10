#requires -Version 5.1
<#
.SYNOPSIS
    一键发布 Halou 插件到 halou-release 公开仓库。
.DESCRIPTION
    流程：
      1. 构建最新 DLL（调用 build-autocad-plugin.ps1）
      2. 复制 DLL + manifest + license.json 到 halou-release 本地克隆
      3. git add / commit / push
    买家无需任何操作，下次打开 CAD 会自动拉到新版本和新授权。
.PARAMETER Message
    本次提交说明（会同时写到 license.json 的 release_notes，除非 -SkipNotes）。
.PARAMETER ReleaseRepo
    halou-release 的本地克隆路径。默认 C:\Users\Administrator\Desktop\halou-release
.PARAMETER SkipBuild
    跳过编译，直接用 release 目录里已有的 DLL。
.PARAMETER SkipNotes
    不修改 license.json 的 release_notes 字段。
.EXAMPLE
    powershell -File publish-release.ps1 -Message "修复 ZK 展开 bug"
#>
param(
    [string]$Message = "",
    [string]$ReleaseRepo = "C:\Users\Administrator\Desktop\halou-release",
    [switch]$SkipBuild,
    [switch]$SkipNotes
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$srcReleaseDir = Join-Path $here 'release'
$srcLicense = Join-Path $here 'license.json'

function Write-Step($s) { Write-Host "`n==> $s" -ForegroundColor Cyan }

# 1. 构建
if (-not $SkipBuild) {
    Write-Step "构建插件"
    & (Join-Path $here 'build-autocad-plugin.ps1')
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }
} else {
    Write-Host "跳过构建（-SkipBuild）"
}

# 2. 检查发布仓库
Write-Step "检查发布仓库 $ReleaseRepo"
if (-not (Test-Path $ReleaseRepo)) {
    throw "未找到 halou-release 克隆：$ReleaseRepo`n请先执行：git clone https://github.com/714167887-svg/halou-release.git $ReleaseRepo"
}
if (-not (Test-Path (Join-Path $ReleaseRepo '.git'))) {
    throw "$ReleaseRepo 不是 git 仓库"
}

# 3. 可选：更新 release_notes
if (-not $SkipNotes -and -not [string]::IsNullOrWhiteSpace($Message)) {
    Write-Step "写入 release_notes = '$Message'"
    $json = Get-Content $srcLicense -Raw -Encoding UTF8
    $safe = $Message -replace '\\','\\\\' -replace '"','\"'
    $json = [regex]::Replace($json, '"release_notes"\s*:\s*"[^"]*"', "`"release_notes`": `"$safe`"")
    Set-Content -Path $srcLicense -Value $json -Encoding UTF8 -NoNewline
}

# 4. 同步文件
Write-Step "同步文件到发布仓库"
$dstRelease = Join-Path $ReleaseRepo 'release'
New-Item -ItemType Directory -Path $dstRelease -Force | Out-Null
Copy-Item $srcLicense (Join-Path $ReleaseRepo 'license.json') -Force
Copy-Item (Join-Path $srcReleaseDir 'JsqClipboardCadPlugin.dll') (Join-Path $dstRelease 'JsqClipboardCadPlugin.dll') -Force
Copy-Item (Join-Path $srcReleaseDir 'halou-plugin-manifest.json') (Join-Path $dstRelease 'halou-plugin-manifest.json') -Force

# 5. Git commit & push
Write-Step "git commit & push"
Push-Location $ReleaseRepo
try {
    git add -A | Out-Null
    $status = git status --porcelain
    if (-not $status) {
        Write-Host "没有变更，跳过提交" -ForegroundColor Yellow
    } else {
        $commitMsg = if ([string]::IsNullOrWhiteSpace($Message)) {
            "release: sync on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        } else { $Message }
        git -c user.email="release@halou.local" -c user.name="halou-release-bot" commit -m $commitMsg
        git push
        Write-Host "`n发布完成！" -ForegroundColor Green
        Write-Host "买家下次打开 CAD 会自动拉取新版本和授权。"
    }
} finally {
    Pop-Location
}
