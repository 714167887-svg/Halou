param(
    [string]$DllPath,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

if (-not $DllPath) {
    $DllPath = Join-Path $PSScriptRoot '..\autocad-clipboard-plugin-autoon-test\dist\JsqClipboardCadPlugin.dll'
}

$DllPath = [System.IO.Path]::GetFullPath($DllPath)

if (-not $Uninstall -and -not (Test-Path $DllPath)) {
    throw "未找到 DLL：$DllPath；请先构建插件。"
}

$rootPath = 'HKCU:\Software\Autodesk\AutoCAD'
if (-not (Test-Path $rootPath)) {
    throw "注册表未找到 AutoCAD 安装项（$rootPath）。请先安装并启动过一次 AutoCAD。"
}

$count = 0
$touched = @()

Get-ChildItem $rootPath | ForEach-Object {
    $versionKey = $_.PSPath
    Get-ChildItem $versionKey | Where-Object { $_.PSChildName -like 'ACAD-*' } | ForEach-Object {
        $appsKey = Join-Path $_.PSPath 'Applications'
        if (-not (Test-Path $appsKey)) {
            New-Item -Path $appsKey -Force | Out-Null
        }

        $entryKey = Join-Path $appsKey 'HalouSuite'

        if ($Uninstall) {
            if (Test-Path $entryKey) {
                Remove-Item -Path $entryKey -Recurse -Force
                $count++
                $touched += $entryKey
            }
        } else {
            if (-not (Test-Path $entryKey)) {
                New-Item -Path $entryKey -Force | Out-Null
            }
            Set-ItemProperty -Path $entryKey -Name 'DESCRIPTION' -Value 'Halou 插件集合（统一壳）' -Type String
            Set-ItemProperty -Path $entryKey -Name 'LOADCTRLS' -Value 2 -Type DWord
            Set-ItemProperty -Path $entryKey -Name 'LOADER' -Value $DllPath -Type String
            Set-ItemProperty -Path $entryKey -Name 'MANAGED' -Value 1 -Type DWord
            $count++
            $touched += $entryKey
        }
    }
}

if ($Uninstall) {
    Write-Host ("已停用 {0} 个 AutoCAD 自启动入口。" -f $count)
} else {
    Write-Host ("已启用 {0} 个 AutoCAD 自启动入口，指向：{1}" -f $count, $DllPath)
}

$touched | ForEach-Object { Write-Host "  - $_" }
