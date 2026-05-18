param(
    [string]$Version = '2.0.0',
    # 指定 ObjectARX/AutoCAD 安装目录（必须包含 acmgd.dll/acdbmgd.dll/accoremgd.dll）
    [string]$AutocadDir = 'C:\Program Files\Autodesk\AutoCAD 2021',
    # 可选的 ARX 标签：传入后输出文件名变成 HalouPayload.<Ver>.<ArxTag>.dll；不传则保持 HalouPayload.<Ver>.dll（向后兼容）
    [string]$ArxTag = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$contractRoot = Join-Path (Split-Path -Parent $projectRoot) 'Contract'
$dist = Join-Path $projectRoot 'dist'
$outName = if ($ArxTag) { "HalouPayload.$Version.$ArxTag.dll" } else { "HalouPayload.$Version.dll" }
$out = Join-Path $dist $outName
$autocadDir = $AutocadDir
$resourceProtectKey = 'HalouSuite.Payload.Resources.2026-05'

function Get-AcadManagedMajor {
    param([string]$Dir)
    try { return [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $Dir 'acmgd.dll')).Version.Major } catch { return 0 }
}

function Ensure-Net8BuildAssets {
    $compiler = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.net.compilers.toolset\4.8.0\tasks\net472\csc.exe'
    $coreRefRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netcore.app.ref'
    $desktopRefRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsdesktop.app.ref'
    $coreRef = Get-ChildItem $coreRefRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    $desktopRef = Get-ChildItem $desktopRefRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ((-not (Test-Path $compiler)) -or (-not $coreRef) -or (-not $desktopRef)) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw "缺少 dotnet，无法自动还原 .NET 8 引用包 / Roslyn 编译器。"
        }
        $tmp = Join-Path $env:TEMP 'halou-net8-build-assets'
        New-Item -ItemType Directory -Force $tmp | Out-Null
        $proj = Join-Path $tmp 'assets.csproj'
        Set-Content -Path $proj -Encoding UTF8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><UseWPF>true</UseWPF><EnableWindowsTargeting>true</EnableWindowsTargeting></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.Net.Compilers.Toolset" Version="4.8.0" PrivateAssets="all" /></ItemGroup></Project>'
        dotnet restore $proj -v:minimal | Write-Host
    }
    if (-not (Test-Path $compiler)) { throw "Roslyn 编译器不存在: $compiler" }
    $coreRef = Get-ChildItem $coreRefRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    $desktopRef = Get-ChildItem $desktopRefRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $coreRef) { throw "缺少 Microsoft.NETCore.App.Ref .NET 8 引用包。" }
    if (-not $desktopRef) { throw "缺少 Microsoft.WindowsDesktop.App.Ref .NET 8 引用包。" }
    return @{ Compiler = $compiler; CoreRef = (Join-Path $coreRef.FullName 'ref\net8.0'); DesktopRef = (Join-Path $desktopRef.FullName 'ref\net8.0') }
}

function Protect-HalouResource {
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $plain = [System.IO.File]::ReadAllBytes($InputPath)
    $salt = New-Object byte[] 16
    $iv = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($salt)
        $rng.GetBytes($iv)
    } finally {
        $rng.Dispose()
    }

    $derive = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($resourceProtectKey, $salt, 10000)
    $aes = New-Object System.Security.Cryptography.AesManaged
    try {
        $aes.KeySize = 256
        $aes.BlockSize = 128
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $derive.GetBytes(32)
        $hmacKey = $derive.GetBytes(32)
        $aes.IV = $iv

        $ms = New-Object System.IO.MemoryStream
        try {
            $magic = [System.Text.Encoding]::ASCII.GetBytes('HLR2')
            $ms.Write($magic, 0, $magic.Length)
            $ms.Write($salt, 0, $salt.Length)
            $ms.Write($iv, 0, $iv.Length)
            $encryptor = $aes.CreateEncryptor()
            try {
                $cs = New-Object System.Security.Cryptography.CryptoStream($ms, $encryptor, [System.Security.Cryptography.CryptoStreamMode]::Write)
                try {
                    $cs.Write($plain, 0, $plain.Length)
                    $cs.FlushFinalBlock()
                } finally {
                    $cs.Dispose()
                }
            } finally {
                $encryptor.Dispose()
            }
            $body = $ms.ToArray()
            $hmac = New-Object System.Security.Cryptography.HMACSHA256(,$hmacKey)
            try {
                $tag = $hmac.ComputeHash($body)
                $final = New-Object byte[] ($body.Length + $tag.Length)
                [System.Buffer]::BlockCopy($body, 0, $final, 0, $body.Length)
                [System.Buffer]::BlockCopy($tag, 0, $final, $body.Length, $tag.Length)
                [System.IO.File]::WriteAllBytes($OutputPath, $final)
            } finally {
                $hmac.Dispose()
            }
        } finally {
            $ms.Dispose()
        }
    } finally {
        $derive.Dispose()
        $aes.Dispose()
    }
}

$targetFramework = if ((Get-AcadManagedMajor $autocadDir) -ge 25) { 'net8.0-windows' } else { 'net48' }

# 确保 Contract.dll 已构建
$contractDll = if ($ArxTag) { Join-Path $contractRoot "dist\HalouContract.$ArxTag.dll" } else { Join-Path $contractRoot 'dist\HalouContract.dll' }
if (-not (Test-Path $contractDll)) {
    Write-Host "Contract 未构建，先执行 Contract\build.ps1 ..." -ForegroundColor Yellow
    if ($ArxTag) { & (Join-Path $contractRoot 'build.ps1') -ArxTag $ArxTag -TargetFramework $targetFramework }
    else { & (Join-Path $contractRoot 'build.ps1') -TargetFramework $targetFramework }
}

$srcFiles = @(Get-ChildItem -Path $projectRoot -Filter *.cs -File -Recurse | ForEach-Object { $_.FullName })
if ($srcFiles.Count -eq 0) { throw "Payload 没有 .cs 源文件" }

New-Item -ItemType Directory -Force $dist | Out-Null
# 清理旧版本，保留当前构建产物；多 SDK 模式只清理同 tag，避免 arx25 构建删掉 arx24 产物。
if ($ArxTag) {
    Get-ChildItem $dist -Filter ("HalouPayload.*." + $ArxTag + ".dll") -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne (Split-Path $out -Leaf) } |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
} else {
    Get-ChildItem $dist -Filter 'HalouPayload.*.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^HalouPayload\.[\d\.]+(?:-[A-Za-z0-9\.]+)?\.dll$' -and $_.Name -ne (Split-Path $out -Leaf) } |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
}

# 阶段 1：Payload 暂时只引用 Contract.dll，未来真实业务搬入后会再引用 acmgd.dll 等
$autocadRefs = @()
if (Test-Path (Join-Path $autocadDir 'acmgd.dll')) {
    $autocadRefs = @(
        "/r:$autocadDir\acdbmgd.dll",
        "/r:$autocadDir\acmgd.dll",
        "/r:$autocadDir\accoremgd.dll"
    )
}

# v2.0.16: 嵌入功能子目录（OLE/ZK/KB/JT）下的 .lsp/.ps1；
# v2.0.34: 改为先加密再嵌入，资源名为 "ProtectedPayload.<subdir>.<filename>"，运行时只在临时目录短暂解密加载。
$payloadArgs = @()
$halouRoot = Split-Path -Parent (Split-Path -Parent $projectRoot)
$protectedDir = Join-Path $dist 'protected-resources'
if (Test-Path $protectedDir) { Remove-Item $protectedDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $protectedDir | Out-Null
foreach ($sub in @('OLE','ZK','KB','JT')) {
    $subDir = Join-Path $halouRoot $sub
    if (-not (Test-Path $subDir)) {
        Write-Host "  [warn] subdir not found: $subDir" -ForegroundColor Yellow
        continue
    }
    Get-ChildItem -Path $subDir -File -Recurse:$false |
        Where-Object { $_.Extension -in '.lsp', '.ps1' } |
        ForEach-Object {
            $resName = "ProtectedPayload.$sub.$($_.Name)"
            $safeName = ($sub + '-' + $_.Name + '.hlr') -replace '[\\/:*?"<>|]', '_'
            $protectedFile = Join-Path $protectedDir $safeName
            Protect-HalouResource -InputPath $_.FullName -OutputPath $protectedFile
            $payloadArgs += "/resource:$protectedFile,$resName"
            Write-Host "  protect+embed: $sub/$($_.Name) -> $resName"
        }
}

if ($targetFramework -eq 'net8.0-windows') {
    $assets = Ensure-Net8BuildAssets
    $refs = @()
    $refs += Get-ChildItem $assets.CoreRef -Filter '*.dll' | ForEach-Object { "/r:$($_.FullName)" }
    $refs += Get-ChildItem $assets.DesktopRef -Filter '*.dll' | ForEach-Object { "/r:$($_.FullName)" }
    & $assets.Compiler /nologo /noconfig /nostdlib /target:library /platform:x64 /optimize+ /define:HALOU_NET8,NET8_0_OR_GREATER "/out:$out" `
        "/r:$contractDll" `
        @autocadRefs `
        @refs `
        @payloadArgs `
        @srcFiles
} else {
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "C# 编译器不存在: $csc" }
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
}

if ($LASTEXITCODE -ne 0) { throw "Payload 构建失败 ($LASTEXITCODE)" }
Write-Host "Built: $out" -ForegroundColor Green
