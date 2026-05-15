param(
    [Parameter(Mandatory = $true)]
    [string]$Action,
    [string]$ImagePath
)

$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:TEMP 'oleimgdir-clipboard-state'
$metaPath = Join-Path $stateDir 'clipboard.json'
$savedImagePath = Join-Path $stateDir 'clipboard.png'
$filesPath = Join-Path $stateDir 'clipboard-files.json'
$dataPath = Join-Path $stateDir 'clipboard-data.json'
$logPath = Join-Path $env:TEMP 'oleimgdir-clipboard.log'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$maxClipboardDimension = 6000

# v1.1.64: 引入 CF_ENHMETAFILE 包装，让 AutoCAD 把图按 EMF OLE 识别（位图内核但渲染更平滑）
Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
public static class HalouEmfHelper {
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    public const uint CF_ENHMETAFILE = 14;

    public static IntPtr CreateEmfFromImage(Image img) {
        IntPtr hemf;
        using (Graphics refG = Graphics.FromHwnd(IntPtr.Zero)) {
            IntPtr hdc = refG.GetHdc();
            try {
                using (var mf = new Metafile(hdc, new Rectangle(0,0,img.Width,img.Height), MetafileFrameUnit.Pixel, EmfType.EmfOnly)) {
                    using (Graphics g = Graphics.FromImage(mf)) {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, 0, 0, img.Width, img.Height);
                    }
                    hemf = mf.GetHenhmetafile();
                }
            } finally { refG.ReleaseHdc(hdc); }
        }
        return hemf;
    }
    public static bool AppendEmfToClipboard(IntPtr hemf) {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try { SetClipboardData(CF_ENHMETAFILE, hemf); return true; }
        finally { CloseClipboard(); }
    }
}
"@ -ReferencedAssemblies System.Drawing -ErrorAction SilentlyContinue

function Get-ScaledSize([int]$width, [int]$height, [int]$maxDimension) {
    if ($width -le $maxDimension -and $height -le $maxDimension) {
        return @{ Width = $width; Height = $height }
    }

    if ($width -ge $height) {
        $scale = $maxDimension / [double]$width
    }
    else {
        $scale = $maxDimension / [double]$height
    }

    $newWidth = [Math]::Max([int][Math]::Round($width * $scale), 1)
    $newHeight = [Math]::Max([int][Math]::Round($height * $scale), 1)
    return @{ Width = $newWidth; Height = $newHeight }
}

function Ensure-StateDir {
    if (-not (Test-Path -LiteralPath $stateDir)) {
        New-Item -ItemType Directory -Path $stateDir | Out-Null
    }
}

function Clear-State {
    if (Test-Path -LiteralPath $stateDir) {
        Remove-Item -LiteralPath $stateDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Save-ClipboardState {
    Ensure-StateDir
    $meta = [ordered]@{ mode = 'empty'; hasImage = $false; hasFiles = $false; text = $null; unicodeText = $null; html = $null; rtf = $null; csv = $null }
    $dataObject = [System.Windows.Forms.Clipboard]::GetDataObject()

    if ($dataObject) {
        if ($dataObject.GetDataPresent([System.Windows.Forms.DataFormats]::UnicodeText)) {
            $meta.unicodeText = [string]$dataObject.GetData([System.Windows.Forms.DataFormats]::UnicodeText)
            $meta.mode = 'text'
        }

        if ($dataObject.GetDataPresent([System.Windows.Forms.DataFormats]::Text)) {
            $meta.text = [string]$dataObject.GetData([System.Windows.Forms.DataFormats]::Text)
            if ($meta.mode -eq 'empty') { $meta.mode = 'text' }
        }

        if ($dataObject.GetDataPresent([System.Windows.Forms.DataFormats]::Html)) {
            $meta.html = [string]$dataObject.GetData([System.Windows.Forms.DataFormats]::Html)
            if ($meta.mode -eq 'empty') { $meta.mode = 'text' }
        }

        if ($dataObject.GetDataPresent([System.Windows.Forms.DataFormats]::Rtf)) {
            $meta.rtf = [string]$dataObject.GetData([System.Windows.Forms.DataFormats]::Rtf)
            if ($meta.mode -eq 'empty') { $meta.mode = 'text' }
        }

        if ($dataObject.GetDataPresent([System.Windows.Forms.DataFormats]::CommaSeparatedValue)) {
            $meta.csv = [string]$dataObject.GetData([System.Windows.Forms.DataFormats]::CommaSeparatedValue)
            if ($meta.mode -eq 'empty') { $meta.mode = 'text' }
        }
    }

    if ([System.Windows.Forms.Clipboard]::ContainsImage()) {
        $meta.hasImage = $true
        if ($meta.mode -eq 'empty') { $meta.mode = 'image' }
        $img = [System.Windows.Forms.Clipboard]::GetImage()
        $bmp = New-Object System.Drawing.Bitmap $img
        $bmp.Save($savedImagePath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        if ($img) { $img.Dispose() }
    }

    if ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
        $meta.hasFiles = $true
        if ($meta.mode -eq 'empty') { $meta.mode = 'files' }
        $files = @()
        foreach ($item in [System.Windows.Forms.Clipboard]::GetFileDropList()) {
            $files += [string]$item
        }
        $files | ConvertTo-Json | Set-Content -LiteralPath $filesPath -Encoding UTF8
    }

    $meta | ConvertTo-Json | Set-Content -LiteralPath $metaPath -Encoding UTF8
}

function Restore-ClipboardState {
    if (-not (Test-Path -LiteralPath $metaPath)) {
        return
    }

    $meta = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json
    $data = New-Object System.Windows.Forms.DataObject

    if ($meta.unicodeText) {
        $data.SetData([System.Windows.Forms.DataFormats]::UnicodeText, [string]$meta.unicodeText)
    }

    if ($meta.text) {
        $data.SetData([System.Windows.Forms.DataFormats]::Text, [string]$meta.text)
    }

    if ($meta.html) {
        $data.SetData([System.Windows.Forms.DataFormats]::Html, [string]$meta.html)
    }

    if ($meta.rtf) {
        $data.SetData([System.Windows.Forms.DataFormats]::Rtf, [string]$meta.rtf)
    }

    if ($meta.csv) {
        $data.SetData([System.Windows.Forms.DataFormats]::CommaSeparatedValue, [string]$meta.csv)
    }

    if ($meta.hasImage -and (Test-Path -LiteralPath $savedImagePath)) {
        $img = [System.Drawing.Image]::FromFile($savedImagePath)
        $bmp = New-Object System.Drawing.Bitmap $img
        $data.SetImage($bmp)
        $bmp.Dispose()
        $img.Dispose()
    }

    if ($meta.hasFiles -and (Test-Path -LiteralPath $filesPath)) {
        $fileList = Get-Content -LiteralPath $filesPath -Raw | ConvertFrom-Json
        $drop = New-Object System.Collections.Specialized.StringCollection
        foreach ($item in $fileList) {
            [void]$drop.Add([string]$item)
        }
        $data.SetFileDropList($drop)
    }

    [System.Windows.Forms.Clipboard]::Clear()
    [System.Windows.Forms.Clipboard]::SetDataObject($data, $true)
}

# v1.1.65: 调用 mspaint 自动化生成 Paint.Picture OLE 嵌入对象到剪贴板
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class HalouWin {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
"@ -ErrorAction SilentlyContinue

function Set-ClipboardImage_PaintEmbed([string]$path) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'mspaint.exe'
    $psi.Arguments = '"' + $path + '"'
    $psi.WindowStyle = 'Minimized'
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForInputIdle(8000)) { throw 'mspaint 启动超时' }
    # 等待主窗口句柄
    $deadline = [DateTime]::UtcNow.AddSeconds(6)
    while (($proc.MainWindowHandle -eq [IntPtr]::Zero) -and ([DateTime]::UtcNow -lt $deadline)) {
        Start-Sleep -Milliseconds 100
        $proc.Refresh()
    }
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { throw 'mspaint 主窗口未出现' }

    # 把 mspaint 移到屏幕外并强制前台聚焦（AttachThreadInput 提高成功率）
    [HalouWin]::ShowWindow($hwnd, 9) | Out-Null  # SW_RESTORE
    [HalouWin]::SetWindowPos($hwnd, [IntPtr]::Zero, -32000, -32000, 800, 600, 0x4) | Out-Null
    $fg = [HalouWin]::GetForegroundWindow()
    $fgPid = 0; $null = [HalouWin]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    $fgTid = [HalouWin]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    $myTid = [HalouWin]::GetCurrentThreadId()
    [HalouWin]::AttachThreadInput($myTid, $fgTid, $true) | Out-Null
    [HalouWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 250

    # 全选 + 复制
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Start-Sleep -Milliseconds 600

    # 关闭 mspaint，不保存
    [HalouWin]::SetForegroundWindow($hwnd) | Out-Null
    [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('n')
    Start-Sleep -Milliseconds 300
    [HalouWin]::AttachThreadInput($myTid, $fgTid, $false) | Out-Null

    if (-not $proc.WaitForExit(2000)) { try { $proc.Kill() } catch {} }
}

function Set-ClipboardImage([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "未找到图片文件: $path"
    }

    # v1.1.75: 把图统一归一化为 96 DPI 临时文件，避免源图 DPI 不同（如扫描件 300/600 DPI）
    # 导致 mspaint/Bitmap 把高 DPI 元数据带进剪贴板后，AutoCAD 把图按物理尺寸解释成 3~6 倍大。
    # 像素数完全保留，只重置 DPI 元数据。
    $normalizedPath = $path
    try {
        $srcImg = [System.Drawing.Image]::FromFile($path)
        try {
            $hRes = $srcImg.HorizontalResolution
            $vRes = $srcImg.VerticalResolution
            if ([Math]::Abs($hRes - 96.0) -gt 0.5 -or [Math]::Abs($vRes - 96.0) -gt 0.5) {
                $normBmp = New-Object System.Drawing.Bitmap $srcImg.Width, $srcImg.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $normBmp.SetResolution(96.0, 96.0)
                $gNorm = [System.Drawing.Graphics]::FromImage($normBmp)
                try {
                    $gNorm.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                    $gNorm.DrawImageUnscaled($srcImg, 0, 0)
                } finally { $gNorm.Dispose() }
                $normalizedPath = Join-Path $env:TEMP ("oleimgdir-norm96-" + [System.Guid]::NewGuid().ToString() + ".png")
                $normBmp.Save($normalizedPath, [System.Drawing.Imaging.ImageFormat]::Png)
                $normBmp.Dispose()
            }
        } finally { $srcImg.Dispose() }
    } catch {
        # 归一化失败就用原文件，至少不阻断主流程
        $normalizedPath = $path
    }

    # v2.0.39: 不再优先走 mspaint/EMF OLE 路线。
    # 某些 AutoCAD 环境会把 Paint/EMF 剪贴板内容当成带文字的 OLE 对象，弹出“确认 OLE 字体大小”，
    # 并按 OLE 物理单位重新解释尺寸，导致图片大小和 LSP 的 2500mm 规则对不上。
    # 改为只写入 96 DPI Bitmap，由 LSP 粘贴后统一按包围盒缩放。

    $tmp = Join-Path $env:TEMP ([System.Guid]::NewGuid().ToString() + [System.IO.Path]::GetExtension($normalizedPath))
    Copy-Item -LiteralPath $normalizedPath -Destination $tmp -Force
    try {
        $bytes = [System.IO.File]::ReadAllBytes($tmp)
        $ms = New-Object System.IO.MemoryStream(,$bytes)
        $img = [System.Drawing.Image]::FromStream($ms)

        $scaledSize = Get-ScaledSize -width $img.Width -height $img.Height -maxDimension $maxClipboardDimension
        $targetWidth = $scaledSize.Width
        $targetHeight = $scaledSize.Height
        $needResize = ($targetWidth -ne $img.Width) -or ($targetHeight -ne $img.Height)

        # v1.1.64: 不超过上限时直接用原图位图，避免无意义重绘损失精度
        # v1.1.75: 即使不需要缩放，也强制把位图 DPI 重设为 96，避免源图 DPI 元数据影响 CAD 解释
        if (-not $needResize) {
            $bmp = New-Object System.Drawing.Bitmap $img
            try { $bmp.SetResolution(96.0, 96.0) } catch {}
        } else {
            # 超过上限才缩放：白底 24bpp 位图（AutoCAD 重开 dwg 时减少 OLE 持久化问题）
            $bmp = New-Object System.Drawing.Bitmap $targetWidth, $targetHeight, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
            try { $bmp.SetResolution(96.0, 96.0) } catch {}
            $graphics = [System.Drawing.Graphics]::FromImage($bmp)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::White)
            $graphics.DrawImage($img, 0, 0, $targetWidth, $targetHeight)
            $graphics.Dispose()
        }

        [System.Windows.Forms.Clipboard]::Clear()
        [System.Windows.Forms.Clipboard]::SetImage($bmp)

        $bmp.Dispose()
        $img.Dispose()
        $ms.Dispose()
    }
    finally {
        if (Test-Path -LiteralPath $tmp) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
        # 清理 v1.1.75 归一化的临时文件
        if ($normalizedPath -ne $path -and (Test-Path -LiteralPath $normalizedPath)) {
            Remove-Item -LiteralPath $normalizedPath -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    switch ($Action.ToLowerInvariant()) {
        'save' { Save-ClipboardState }
        'restore' { Restore-ClipboardState }
        'setimage' { Set-ClipboardImage $ImagePath }
        'clearstate' { Clear-State }
        default { throw "不支持的操作: $Action" }
    }
}
catch {
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    Add-Content -LiteralPath $logPath -Value ("[$ts] ERROR: " + $_.Exception.Message)
    Add-Content -LiteralPath $logPath -Value ("[$ts] ACTION: " + $Action)
    if ($ImagePath) { Add-Content -LiteralPath $logPath -Value ("[$ts] PATH  : " + $ImagePath) }
    throw
}
