#requires -Version 5.1
<#
.SYNOPSIS
    一键发布 Halou Suite Phase 2 新架构（HalouHost + HalouPayload 热重载）。

.DESCRIPTION
    流程：
      1. 改写 Payload/PayloadEntry.cs 的 PayloadVersion 常量
      2. 调 W/HalouSuite/build-all.ps1 -PayloadVersion <Ver> 编译三件套
      3. 把 HalouPayload.<Ver>.dll + HalouHost.dll + HalouContract.dll + manifest + license.json 复制到 halou-release/release/
      4. 更新 license.json：latest_version / download_url / release_notes
      5. git add / commit / push
    买家下次启动 acad（或者点「下载新版本」）就能在线热重载，无需关闭 acad。

.PARAMETER Version
    新版本号，必须严格大于当前 license.json 的 latest_version。例：2.0.1

.PARAMETER Message
    本次发布说明（写到 license.json 的 release_notes，也作为 git commit message）。

.PARAMETER ReleaseRepo
    halou-release 的本地克隆路径。默认 C:\Users\Administrator\Desktop\halou-release

.PARAMETER SkipBuild
    跳过编译（仅同步 + 推 git）。

.PARAMETER DryRun
    只做编译 + 同步，不改 license 不 push（本地验证用）。

.EXAMPLE
    powershell -File publish-halou-suite.ps1 -Version 2.0.1 -Message "修复 ZK MText 段长 bug"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Message = "",
    [string]$ReleaseRepo = "C:\Users\Administrator\Desktop\halou-release",
    [switch]$SkipBuild,
    [switch]$DryRun,
    # 多 ARX SDK 模式：传 @{ arx24='...AutoCAD 2021'; arx25='C:\ObjectARX\2024' }
    # 不传则保持单 SDK 旧行为（HalouHost.dll / HalouPayload.<ver>.dll）
    [hashtable]$ArxSdks,
    # 多 SDK 模式下，license.json 的 payload_download_url 默认指向哪个 tag（旧客户端兼容）
    [string]$DefaultArxTag = 'arx24'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repoRoot = Split-Path -Parent $here  # W\
$suiteRoot = Join-Path $repoRoot 'HalouSuite'
$payloadEntryFile = Join-Path $suiteRoot 'Payload\PayloadEntry.cs'
$buildAll = Join-Path $suiteRoot 'build-all.ps1'

$srcLicense = Join-Path $here 'license.json'
$srcManifest = Join-Path $here 'release\halou-plugin-manifest.json'

function Write-Step($s) { Write-Host "`n==> $s" -ForegroundColor Cyan }

# ---- 0. 校验 ----
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?$') {
    throw "版本号格式不合法：$Version （应为 X.Y.Z 或 X.Y.Z-tag）"
}
if (-not (Test-Path $payloadEntryFile)) { throw "找不到：$payloadEntryFile" }
if (-not (Test-Path $buildAll)) { throw "找不到：$buildAll" }
if (-not (Test-Path $srcLicense)) { throw "找不到：$srcLicense" }

# ---- 1. 改写 Payload 版本号常量 ----
Write-Step "改写 PayloadEntry.cs 的 PayloadVersion = `"$Version`""
$utf8 = New-Object System.Text.UTF8Encoding($false)
$entryText = [System.IO.File]::ReadAllText($payloadEntryFile, [System.Text.Encoding]::UTF8)
$rxVer = 'public const string PayloadVersion = "[^"]*"'
if ($entryText -notmatch $rxVer) { throw "PayloadEntry.cs 里没找到 PayloadVersion 常量" }
$origEntryText = $entryText
$newEntryText = [regex]::Replace($entryText, $rxVer, "public const string PayloadVersion = `"$Version`"")
[System.IO.File]::WriteAllText($payloadEntryFile, $newEntryText, $utf8)

# ---- 2. 编译 ----
$multiSdk = $PSBoundParameters.ContainsKey('ArxSdks') -and $ArxSdks -and $ArxSdks.Count -gt 0
if (-not $SkipBuild) {
    if ($multiSdk) {
        Write-Step ("编译三件套（多 SDK: " + ($ArxSdks.Keys -join ', ') + "）v$Version")
        & $buildAll -PayloadVersion $Version -ArxSdks $ArxSdks
    } else {
        Write-Step "编译三件套 (Contract / Host / Payload v$Version)"
        & $buildAll -PayloadVersion $Version
    }
    if ($LASTEXITCODE -ne 0) { throw "构建失败，已退出" }
} else {
    Write-Host "跳过编译（-SkipBuild）" -ForegroundColor Yellow
}

# hostDlls / payloadDlls：tag -> 绝对路径；单 SDK 模式 tag = ''
$contractDlls = [ordered]@{}
$hostDlls    = [ordered]@{}
$payloadDlls = [ordered]@{}
if ($multiSdk) {
    foreach ($tag in $ArxSdks.Keys) {
        $c = Join-Path $suiteRoot "Contract\dist\HalouContract.$tag.dll"
        $h = Join-Path $suiteRoot "Host\dist\HalouHost.$tag.dll"
        $p = Join-Path $suiteRoot "Payload\dist\HalouPayload.$Version.$tag.dll"
        if (-not (Test-Path $c)) { throw "构建产物缺失：$c" }
        if (-not (Test-Path $h)) { throw "构建产物缺失：$h" }
        if (-not (Test-Path $p)) { throw "构建产物缺失：$p" }
        $contractDlls[$tag] = $c
        $hostDlls[$tag]    = $h
        $payloadDlls[$tag] = $p
    }
    if (-not $hostDlls.Contains($DefaultArxTag)) {
        throw "DefaultArxTag '$DefaultArxTag' 不在 ArxSdks 列表中：$($ArxSdks.Keys -join ', ')"
    }
} else {
    $c = Join-Path $suiteRoot 'Contract\dist\HalouContract.dll'
    $h = Join-Path $suiteRoot 'Host\dist\HalouHost.dll'
    $p = Join-Path $suiteRoot ("Payload\dist\HalouPayload.$Version.dll")
    if (-not (Test-Path $c)) { throw "构建产物缺失：$c" }
    if (-not (Test-Path $h)) { throw "构建产物缺失：$h" }
    if (-not (Test-Path $p)) { throw "构建产物缺失：$p" }
    $contractDlls[''] = $c
    $hostDlls['']    = $h
    $payloadDlls[''] = $p
}

# ---- 3. 检查发布仓库 ----
Write-Step "检查发布仓库 $ReleaseRepo"
if (-not (Test-Path $ReleaseRepo))           { throw "未找到 halou-release 克隆：$ReleaseRepo" }
if (-not (Test-Path (Join-Path $ReleaseRepo '.git'))) { throw "$ReleaseRepo 不是 git 仓库" }
$dstRelease = Join-Path $ReleaseRepo 'release'
New-Item -ItemType Directory -Path $dstRelease -Force | Out-Null

# ---- 4. 更新 license.json（先在本地源 license 改，再复制到 release 仓库）----
if (-not $DryRun) {
    Write-Step "更新 license.json (release_notes + latest_payload_version)"
    $licenseText = [System.IO.File]::ReadAllText($srcLicense, [System.Text.Encoding]::UTF8)

    # ⚠️ 重要：不要写 latest_version / download_url —— 这两个字段是【宿主自更新】用的
    # （host.IsUpdateAvailable() 会比较 host CurrentVersion < latest_version，
    #  点"下载新版本"会下 download_url 当 host DLL 装到 bin\）。
    # 如果在这里写 Payload 版本，客户端会把 Payload DLL 当 host 装，acad 启动直接挂。
    # 2026-05 真实事故：v2.0.1/v2.0.2 发布把 host 字段污染，导致一批客户端 bin 被覆盖。
    # —— 详见 memory/repo/halou-license-json-fields-2026-05.md。
    # Payload 版本走自己的字段，等 Phase 2 host 上线后再切换语义。

    # latest_payload_version（独立于 host 字段）
    if ($licenseText -match '"latest_payload_version"\s*:') {
        $licenseText = [regex]::Replace($licenseText,
            '"latest_payload_version"\s*:\s*"[^"]*"',
            "`"latest_payload_version`": `"$Version`"")
    } else {
        # 首次发布：在 release_notes 前面插一行
        $licenseText = [regex]::Replace($licenseText,
            '("release_notes"\s*:)',
            "`"latest_payload_version`": `"$Version`",`r`n  `$1")
    }

    # payload_download_url（独立于 host 字段）
    # 多 SDK 模式：payload_download_url 指向 DefaultArxTag（向后兼容旧客户端），
    #             并为每个非默认 tag 写 payload_download_url_<tag>
    $urlBase = "https://cdn.jsdelivr.net/gh/714167887-svg/halou-release@main/release"
    if ($multiSdk) {
        $defaultPayloadName = "HalouPayload.$Version.$DefaultArxTag.dll"
    } else {
        $defaultPayloadName = "HalouPayload.$Version.dll"
    }
    $newUrl = "$urlBase/$defaultPayloadName"
    if ($licenseText -match '"payload_download_url"\s*:') {
        $licenseText = [regex]::Replace($licenseText,
            '"payload_download_url"\s*:\s*"[^"]*"',
            "`"payload_download_url`": `"$newUrl`"")
    } else {
        $licenseText = [regex]::Replace($licenseText,
            '("release_notes"\s*:)',
            "`"payload_download_url`": `"$newUrl`",`r`n  `$1")
    }

    # 多 SDK：为每个非默认 tag 写 payload_download_url_<tag>
    if ($multiSdk) {
        foreach ($tag in $ArxSdks.Keys) {
            if ($tag -eq $DefaultArxTag) { continue }
            $tagName = "HalouPayload.$Version.$tag.dll"
            $tagUrl  = "$urlBase/$tagName"
            $field   = "payload_download_url_$tag"
            $rx      = '"' + [regex]::Escape($field) + '"\s*:\s*"[^"]*"'
            if ($licenseText -match $rx) {
                $licenseText = [regex]::Replace($licenseText, $rx,
                    "`"$field`": `"$tagUrl`"")
            } else {
                $licenseText = [regex]::Replace($licenseText,
                    '("release_notes"\s*:)',
                    "`"$field`": `"$tagUrl`",`r`n  `$1")
            }
        }
    }

    # release_notes（这个改无害，host UI 也会读）
    # v2.0.56 hotfix: 旧实现用 `-replace '"','\"'` + `[^"]*` regex，遇到 Message 含双引号时
    #   regex 提前在内层 \" 截断 → 残留旧 release_notes 后半段污染 license.json，
    #   导致客户端 JSON.parse 失败，热更新永远收不到新版本。
    # 改用 ConvertTo-Json 生成合法 JSON 字面量 + 支持 `\"` 的 regex。
    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        $safeJson = ConvertTo-Json -InputObject $Message -Compress  # 返回带两端引号的 JSON 字面量
        $licenseText = [regex]::Replace($licenseText,
            '"release_notes"\s*:\s*"(?:[^"\\]|\\.)*"',
            "`"release_notes`": $safeJson")
    }

    [System.IO.File]::WriteAllText($srcLicense, $licenseText, $utf8)
} else {
    Write-Host "DryRun：跳过 license.json 更新" -ForegroundColor Yellow
}

# ---- 5. 同步文件到发布仓库 ----
Write-Step "同步文件到发布仓库 $dstRelease"
if ($multiSdk) {
    Copy-Item $contractDlls[$DefaultArxTag] (Join-Path $dstRelease 'HalouContract.dll') -Force
    foreach ($tag in $ArxSdks.Keys) {
        Copy-Item $contractDlls[$tag] (Join-Path $dstRelease "HalouContract.$tag.dll") -Force
        Copy-Item $hostDlls[$tag]    (Join-Path $dstRelease "HalouHost.$tag.dll")              -Force
        Copy-Item $payloadDlls[$tag] (Join-Path $dstRelease "HalouPayload.$Version.$tag.dll") -Force
    }
} else {
    Copy-Item $contractDlls[''] (Join-Path $dstRelease 'HalouContract.dll')                   -Force
    Copy-Item $hostDlls['']    (Join-Path $dstRelease 'HalouHost.dll')                       -Force
    Copy-Item $payloadDlls[''] (Join-Path $dstRelease "HalouPayload.$Version.dll")           -Force
}
if (Test-Path $srcManifest) {
    Copy-Item $srcManifest (Join-Path $dstRelease 'halou-plugin-manifest.json') -Force
}
Copy-Item $srcLicense (Join-Path $ReleaseRepo 'license.json') -Force

# 顺手清理 release 仓库里旧版本的 HalouPayload.*.dll（保留最近 N 个 × tag 数）
$keepPerGroup = 3
# 单 SDK：所有 HalouPayload.*.dll 是一组；多 SDK：按 tag 分组分别保留
if ($multiSdk) {
    foreach ($tag in $ArxSdks.Keys) {
        Get-ChildItem $dstRelease -Filter ("HalouPayload.*." + $tag + ".dll") |
            Sort-Object LastWriteTime -Descending |
            Select-Object -Skip $keepPerGroup |
            ForEach-Object { Write-Host "  清理旧版本：$($_.Name)" -ForegroundColor DarkGray; Remove-Item $_.FullName -Force }
    }
} else {
    # 单 SDK 模式：只清理无 tag 后缀的旧 payload（避免误删多 SDK 历史产物）
    Get-ChildItem $dstRelease -Filter 'HalouPayload.*.dll' |
        Where-Object { $_.Name -match '^HalouPayload\.[\d\.]+(?:-[A-Za-z0-9\.]+)?\.dll$' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $keepPerGroup |
        ForEach-Object { Write-Host "  清理旧版本：$($_.Name)" -ForegroundColor DarkGray; Remove-Item $_.FullName -Force }
}

# ---- 6. Git ----
if ($DryRun) {
    Write-Host "`nDryRun：不执行 git push。" -ForegroundColor Yellow
    Write-Host "产物已在 $dstRelease，可手动检查"
    # DryRun 模式下还原 PayloadEntry.cs 的版本号常量，避免污染本地开发状态
    [System.IO.File]::WriteAllText($payloadEntryFile, $origEntryText, $utf8)
    Write-Host "DryRun：已还原 PayloadEntry.cs 的 PayloadVersion 常量" -ForegroundColor DarkGray
    return
}

Write-Step "git commit & push"
Push-Location $ReleaseRepo
try {
    # git 把 warning（如 LF/CRLF）写到 stderr，与 $ErrorActionPreference=Stop 冲突。
    # 改成 cmd.exe 调用，stderr 不抛 NativeCommandError。
    $oldEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        cmd /c 'git add -A 2>&1' | Out-Null
        $status = cmd /c 'git status --porcelain'
        if (-not $status) {
            Write-Host "没有变更，跳过提交" -ForegroundColor Yellow
        } else {
            $commitMsg = if ([string]::IsNullOrWhiteSpace($Message)) {
                "release v$Version on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
            } else { "release v$Version`: $Message" }
            cmd /c "git -c user.email=`"release@halou.local`" -c user.name=`"halou-release-bot`" commit -m `"$commitMsg`" 2>&1" | ForEach-Object { Write-Host $_ }
            cmd /c "git push 2>&1" | ForEach-Object { Write-Host $_ }

            # —— 主动 purge jsDelivr CDN（默认 12h 缓存，不 purge 客户端要等半天才看到新 license）
            Write-Host "`n--- 主动刷新 jsDelivr CDN 缓存 ---" -ForegroundColor Cyan
            $purgeBase = 'https://purge.jsdelivr.net/gh/714167887-svg/halou-release@main'
            $purgePaths = @('/license.json')
            if ($multiSdk) {
                foreach ($tag in $ArxSdks.Keys) {
                    $purgePaths += "/release/$(Split-Path -Leaf $payloadDlls[$tag])"
                }
            } else {
                $purgePaths += "/release/$(Split-Path -Leaf $payloadDlls[''])"
            }
            foreach ($p in $purgePaths) {
                try {
                    $resp = Invoke-RestMethod -Uri ($purgeBase + $p) -Method Get -TimeoutSec 30
                    Write-Host ("  purge OK: " + $p) -ForegroundColor Green
                } catch {
                    Write-Host ("  purge FAIL: " + $p + " -- " + $_.Exception.Message) -ForegroundColor Yellow
                }
            }

            Write-Host "`n=== 发布完成 v$Version ===" -ForegroundColor Green
            if ($multiSdk) {
                foreach ($tag in $ArxSdks.Keys) {
                    Write-Host ("  Payload[$tag] : " + (Split-Path -Leaf $payloadDlls[$tag]))
                }
                Write-Host "  默认 URL    : $newUrl  (tag=$DefaultArxTag)"
            } else {
                Write-Host "  Payload : $(Split-Path -Leaf $payloadDlls[''])"
                Write-Host "  URL     : $newUrl"
            }
            Write-Host "  买家下次点「下载新版本」即可在线热重载，无需关 CAD。"
        }
    } finally {
        $ErrorActionPreference = $oldEAP
    }
} finally {
    Pop-Location
}
