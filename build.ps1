# Builds UsageTracker.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK or installs needed. Run:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src  = Join-Path $root 'src\UsageTracker.cs'
$ico  = Join-Path $root 'app.ico'
$out  = Join-Path $root 'UsageTracker.exe'

Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# One .ico frame. 256 px is stored as PNG; smaller sizes as 32-bit bitmaps, because
# .NET Framework's Icon class (used for the tray icon) can't read small PNG frames.
function ConvertTo-IconFrame([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    if ($n -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return $ms.ToArray()
    }
    $rect = New-Object System.Drawing.Rectangle 0, 0, $n, $n
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($n * $n * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($data)
    $maskRow = [Math]::Floor(($n + 31) / 32) * 4
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER: height is doubled to cover the colour image plus the AND mask
    $w.Write([uint32]40); $w.Write([int32]$n); $w.Write([int32]($n * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($px.Length + $maskRow * $n))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($row = $n - 1; $row -ge 0; $row--) { $w.Write($px, $row * $n * 4, $n * 4) }   # bottom-up BGRA
    $w.Write((New-Object byte[] ($maskRow * $n)))   # AND mask, unused because alpha is present
    $w.Flush()
    return $ms.ToArray()
}

# Icon: dark rounded tile with two usage bars (blue and amber).
function New-AppIcon([string]$path) {
    $frames = @()
    foreach ($n in 16, 20, 24, 32, 40, 48, 64, 256) {
        $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $tile = New-RoundRect 0 0 ($n - 0.01) ($n - 0.01) ($n * 0.22)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(27, 29, 34))), $tile)
        if ($n -ge 32) {
            $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 76, 86)), ([single]($n / 64))
            $g.DrawPath($pen, (New-RoundRect ($n / 128) ($n / 128) ($n - $n / 64) ($n - $n / 64) ($n * 0.22)))
        }

        $m  = if ($n -le 24) { $n * 0.16 } else { $n * 0.19 }
        $bh = if ($n -le 24) { [Math]::Max(3, [Math]::Round($n * 0.19)) } else { $n * 0.16 }
        $gap = $n * 0.12
        $bw = $n - 2 * $m
        $y1 = ($n - (2 * $bh + $gap)) / 2
        $y2 = $y1 + $bh + $gap
        $track = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(52, 57, 66))
        $blue  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 169, 230))
        $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(232, 176, 74))
        $r = $bh / 2
        $g.FillPath($track, (New-RoundRect $m $y1 $bw $bh $r))
        $g.FillPath($track, (New-RoundRect $m $y2 $bw $bh $r))
        $g.FillPath($blue,  (New-RoundRect $m $y1 ($bw * 0.72) $bh $r))
        $g.FillPath($amber, (New-RoundRect $m $y2 ($bw * 0.42) $bh $r))
        $g.Dispose()

        $frames += [pscustomobject]@{ Size = $n; Data = (ConvertTo-IconFrame $bmp) }
        $bmp.Dispose()
    }

    $fs = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f.Data) }
    $w.Close()
}

if (-not (Test-Path $ico)) {
    Write-Host 'Drawing app.ico'
    New-AppIcon $ico
}

if (Get-Process -Name UsageTracker -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $out }) {
    throw 'Usage Tracker is running. Exit it first (right-click the widget > Exit), then build again.'
}

& $csc /nologo /target:winexe /optimize+ /out:$out /win32icon:$ico "/resource:$ico,app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    $src
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit $LASTEXITCODE)" }
Write-Host "Built $out ($([Math]::Round((Get-Item $out).Length / 1KB)) KB)"
