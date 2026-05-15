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
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$autocadDir = $AutocadDir
$resourceProtectKey = 'HalouSuite.Payload.Resources.2026-05'

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
        $aes.IV = $iv

        $ms = New-Object System.IO.MemoryStream
        try {
            $magic = [System.Text.Encoding]::ASCII.GetBytes('HLR1')
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
            [System.IO.File]::WriteAllBytes($OutputPath, $ms.ToArray())
        } finally {
            $ms.Dispose()
        }
    } finally {
        $derive.Dispose()
        $aes.Dispose()
    }
}

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
