$ErrorActionPreference = 'Stop'

# 一次性拆分脚本：把 src\JsqClipboardCadPlugin.cs（约 5153 行）按 22 个顶层类型拆成独立文件。
# 编码统一为 UTF-8 无 BOM，与原文件一致。
# #region/#endregion（4335 / 5152）为跨类标记，拆分时丢弃。

$projectRoot = Split-Path -Parent $PSScriptRoot
$src = Join-Path $projectRoot 'src\JsqClipboardCadPlugin.cs'
$srcDir = Split-Path $src

if (-not (Test-Path $src)) { throw "Source not found: $src" }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$lines = [IO.File]::ReadAllLines($src, $utf8NoBom)

# 顶部 using 列表（行 1-23），共 23 行；行 24 空行。
$usings = $lines[0..22]

# 各类型切片（1-indexed inclusive）。每条记录覆盖类声明本身 + 紧贴其上的文档注释/属性。
# 切片范围已验证：参考前置研究表格。
$entries = @(
    @{ name = 'ClipboardCommands';        start = 27;   end = 568 },
    @{ name = 'HalouSuiteManager';        start = 570;  end = 2096 },
    @{ name = 'RobustHttp';               start = 2098; end = 2345 },
    @{ name = 'SuitePaletteControl';      start = 2347; end = 3585 },
    @{ name = 'PluginManifest';           start = 3587; end = 3811 },
    @{ name = 'CadPluginFeature';         start = 3813; end = 3826 },
    @{ name = 'SuiteConfiguration';       start = 3828; end = 3932 },
    @{ name = 'LicenseStatus';            start = 3934; end = 3940 },
    @{ name = 'LicenseInfo';              start = 3942; end = 4041 },
    @{ name = 'LicenseAccountInfo';       start = 4043; end = 4052 },
    @{ name = 'HotKeyModifiers';          start = 4053; end = 4061 },
    @{ name = 'HotkeyBinding';            start = 4063; end = 4149 },
    @{ name = 'HotKeyPressedEventArgs';   start = 4151; end = 4155 },
    @{ name = 'HotkeyCaptureTextBox';     start = 4157; end = 4234 },
    @{ name = 'HotKeyWindow';             start = 4237; end = 4297 },
    @{ name = 'CadClipboardPayload';      start = 4300; end = 4306 },
    @{ name = 'CadLayerDefinition';       start = 4309; end = 4313 },
    @{ name = 'CadEntityDefinition';      start = 4315; end = 4334 },
    @{ name = 'HalouDragDropInterop';     start = 4337; end = 4435 },
    @{ name = 'HalouImageDropTarget';     start = 4437; end = 4501 },
    @{ name = 'JtPngEmbed';               start = 4504; end = 4630 },
    @{ name = 'JtLispBridge';             start = 4633; end = 5151 }
)

# 完整性自检：覆盖 27..5151 中除以下行外的全部行（namespace 大括号、跨类 region、空行）。
# 这里只做粗校验：每条 end >= start，且不互相覆盖。
$prevEnd = 26
foreach ($e in $entries) {
    if ($e.start -le $prevEnd) { throw "Overlap: $($e.name) start=$($e.start) <= prevEnd=$prevEnd" }
    if ($e.end -lt $e.start) { throw "Bad range: $($e.name)" }
    $prevEnd = $e.end
}

$created = @()
foreach ($e in $entries) {
    $body = $lines[($e.start - 1)..($e.end - 1)]

    # 跳过 #region / #endregion 行（跨类标记，拆分后无意义）
    $body = $body | Where-Object {
        $t = $_.TrimStart()
        -not ($t.StartsWith('#region') -or $t.StartsWith('#endregion'))
    }

    $out = New-Object System.Collections.Generic.List[string]
    foreach ($u in $usings) { [void]$out.Add($u) }
    [void]$out.Add('')
    [void]$out.Add('namespace JsqClipboardCadPlugin')
    [void]$out.Add('{')
    foreach ($b in $body) { [void]$out.Add($b) }
    [void]$out.Add('}')

    $outPath = Join-Path $srcDir "$($e.name).cs"
    [IO.File]::WriteAllLines($outPath, $out, $utf8NoBom)
    $created += $outPath
    Write-Host ("  wrote: {0}  ({1} lines)" -f $e.name, $out.Count)
}

Write-Host "`nCreated $($created.Count) files."
Write-Host "Now removing original monolithic file..."
Remove-Item $src -Force
Write-Host "Done."
