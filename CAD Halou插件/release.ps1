# Halou Suite 一键发版脚本
#
# 用法（在本目录下打开 PowerShell）：
#   ./release.ps1 -NewVersion 1.1.44 -Notes "1.1.44 修复 XXX bug"
#
# 流程（全自动）：
#   1. 校验当前 W 仓与 halou-release 仓都干净（无未提交改动除目标文件外）
#   2. 改 JsqClipboardCadPlugin.cs 里的 CurrentVersion 常量
#   3. 调 build-autocad-plugin.ps1 编译
#   4. 同步 dist/JsqClipboardCadPlugin.dll → halou-release/release/
#   5. 改 license.json 的 latest_version + release_notes（保持无 BOM）
#   6. 双仓 git add/commit/push
#   7. 在桌面生成「给客户-Halou<ver>升级」目录，含 dll + 操作说明.txt
#
# 失败任一步立即停下，不破坏中间状态。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$NewVersion,

    [Parameter(Mandatory = $true)]
    [string]$Notes,

    [string]$WRoot = $(Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ReleaseRoot = 'e:\halou-release',
    [switch]$SkipPush,
    [switch]$SkipCustomerPackage
)

$ErrorActionPreference = 'Stop'

function Step($msg) { Write-Host ">> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "OK $msg" -ForegroundColor Green }

$pluginSrcRoot = Join-Path $WRoot 'autocad-clipboard-plugin-autoon-test'
$srcCs   = Join-Path $pluginSrcRoot 'src\HalouSuiteManager.cs'  # CurrentVersion 常量所在文件（拆分后）
$distDll = Join-Path $pluginSrcRoot 'dist\JsqClipboardCadPlugin.dll'
$buildPs = Join-Path $PSScriptRoot 'build-autocad-plugin.ps1'
$relDll  = Join-Path $ReleaseRoot 'release\JsqClipboardCadPlugin.dll'
$relLic  = Join-Path $ReleaseRoot 'license.json'

foreach ($p in @($srcCs, $buildPs, $relLic)) {
    if (-not (Test-Path $p)) { throw "Missing required file: $p" }
}

# ---- 1) 检查工作树状态 ----
Step "1/7 检查 git 工作树状态"
$wStatus = (& git -C $WRoot status --porcelain) -join "`n"
$rStatus = (& git -C $ReleaseRoot status --porcelain) -join "`n"
$wDirty = $wStatus -split "`n" | Where-Object {
    $_ -and ($_ -notmatch 'autocad-clipboard-plugin-autoon-test/src/HalouSuiteManager\.cs') -and
            ($_ -notmatch 'autocad-clipboard-plugin-autoon-test/dist/')
}
$rDirty = $rStatus -split "`n" | Where-Object {
    $_ -and ($_ -notmatch 'release/JsqClipboardCadPlugin\.dll') -and ($_ -notmatch 'license\.json')
}
if ($wDirty) { throw "W 仓有无关未提交改动：`n$($wDirty -join "`n")" }
if ($rDirty) { throw "halou-release 仓有无关未提交改动：`n$($rDirty -join "`n")" }
Ok "git 状态干净"

# ---- 2) 改源码版本号 ----
Step "2/7 改源码 CurrentVersion = $NewVersion"
$cs = [IO.File]::ReadAllText($srcCs, [Text.UTF8Encoding]::new($false))
$pattern = 'public const string CurrentVersion = "([^"]+)";'
if ($cs -notmatch $pattern) { throw "未在源码中找到 CurrentVersion 字段" }
$oldVersion = [Regex]::Match($cs, $pattern).Groups[1].Value
if ($oldVersion -eq $NewVersion) { throw "新旧版本号相同：$NewVersion" }
$cs2 = [Regex]::Replace($cs, $pattern, "public const string CurrentVersion = `"$NewVersion`";")
$noBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($srcCs, $cs2, $noBom)
Ok "已从 $oldVersion → $NewVersion"

# ---- 3) 编译 ----
Step "3/7 调用 build-autocad-plugin.ps1 编译"
& powershell -NoProfile -ExecutionPolicy Bypass -File $buildPs
if ($LASTEXITCODE -ne 0) { throw "编译失败 exit=$LASTEXITCODE" }
if (-not (Test-Path $distDll)) { throw "编译产物未生成：$distDll" }
$dllSize = (Get-Item $distDll).Length
Ok "dll $dllSize 字节"

# ---- 4) 同步 dll ----
Step "4/7 同步 dll → halou-release/release/"
Copy-Item $distDll $relDll -Force
Ok "已复制"

# ---- 5) 更新 license.json（保持无 BOM）----
Step "5/7 更新 license.json（无 BOM）"
$bytes = [IO.File]::ReadAllBytes($relLic)
$start = 0
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $start = 3 }
$lic = [Text.Encoding]::UTF8.GetString($bytes, $start, $bytes.Length - $start)
$lic2 = [Regex]::Replace($lic, '"latest_version"\s*:\s*"[^"]*"', "`"latest_version`": `"$NewVersion`"")
$escapedNotes = $Notes.Replace('\', '\\').Replace('"', '\"')
$lic2 = [Regex]::Replace($lic2, '"release_notes"\s*:\s*"[^"]*"', "`"release_notes`": `"$escapedNotes`"")
[IO.File]::WriteAllText($relLic, $lic2, $noBom)
$check = [IO.File]::ReadAllBytes($relLic)
if ($check[0] -eq 0xEF) { throw "license.json 仍带 BOM，发版中止" }
Ok "license.json 已更新"

# ---- 6) 双仓提交 + 推送 ----
Step "6/7 双仓 git commit + push"
& git -C $ReleaseRoot add release/JsqClipboardCadPlugin.dll license.json | Out-Null
& git -C $ReleaseRoot commit -m "Release v${NewVersion}: $Notes" | Out-Null
& git -C $WRoot add autocad-clipboard-plugin-autoon-test/src/HalouSuiteManager.cs | Out-Null
& git -C $WRoot commit -m "v${NewVersion}: $Notes" | Out-Null
if ($SkipPush) {
    Write-Host "  -SkipPush 指定，跳过 push" -ForegroundColor Yellow
} else {
    # 注：git 把进度信息写到 stderr，PowerShell 5.1 的 ErrorActionPreference=Stop 会把它当致命错误。
    # 用 cmd /c "... 2>&1" 把 stderr 合并进 stdout，绕过 NativeCommandError 干扰。
    cmd /c "git -C `"$ReleaseRoot`" push 2>&1" | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "halou-release push 失败" }
    cmd /c "git -C `"$WRoot`" push 2>&1" | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "W push 失败" }
}
Ok "双仓已发布"

# ---- 7) 客户应急包 ----
if ($SkipCustomerPackage) {
    Write-Host "  -SkipCustomerPackage 指定，跳过生成应急包" -ForegroundColor Yellow
} else {
    Step "7/7 生成桌面客户应急包"
    $desk = [Environment]::GetFolderPath('Desktop')
    $pkg = Join-Path $desk "给客户-Halou${NewVersion}升级"
    if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $pkg | Out-Null
    Copy-Item $relDll (Join-Path $pkg 'JsqClipboardCadPlugin.dll') -Force
    $readme = @"
Halou Suite $NewVersion 升级（手动替换 dll）
=============================================

【本次更新】
$Notes

【操作步骤】
1. 完全关闭 AutoCAD（任务管理器无 acad.exe）
2. 把本目录里的 JsqClipboardCadPlugin.dll 替换到：
     %LOCALAPPDATA%\HalouSuite\bin\JsqClipboardCadPlugin.dll
   方法：Win+R → 粘贴 %LOCALAPPDATA%\HalouSuite\bin → 回车 →
   把 dll 拖入 → 选"替换目标中的文件"
3. 重启 AutoCAD → HALOU → 检查更新，应显示 $NewVersion、授权正常
"@
    Set-Content -Path (Join-Path $pkg '操作说明.txt') -Value $readme -Encoding UTF8
    Ok "客户包：$pkg"
}

Write-Host ""
Write-Host "==============================" -ForegroundColor Green
Write-Host "  v$NewVersion 发版完成" -ForegroundColor Green
Write-Host "==============================" -ForegroundColor Green
