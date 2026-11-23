# Draws the app icon (dark tile, gold border, rising gold graph line) at every size Windows uses
# and packs them into LeagueOverlay\app.ico (PNG-compressed ICO entries).
# Usage: powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\LeagueOverlay\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $gold = [System.Drawing.Color]::FromArgb(255, 0xC8, 0x9B, 0x3C)
    $light = [System.Drawing.Color]::FromArgb(255, 0xF0, 0xC8, 0x70)
    $border = [Math]::Max(1.0, $s / 16.0)
    $tile = New-RoundedRect ($border / 2) ($border / 2) ($s - $border) ($s - $border) ([Math]::Max(2.0, $s * 0.2))
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)),
        ([System.Drawing.Color]::FromArgb(255, 0x1C, 0x21, 0x2D)), ([System.Drawing.Color]::FromArgb(255, 0x0F, 0x11, 0x15))
    $g.FillPath($bg, $tile)
    $g.DrawPath((New-Object System.Drawing.Pen $gold, $border), $tile)

    # Rising graph: three steps up and an end dot (simpler at small sizes).
    $w = [Math]::Max(1.5, $s / 11.0)
    $pen = New-Object System.Drawing.Pen $light, $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $m = $s * 0.24
    $pts = if ($s -le 24) {
        @([System.Drawing.PointF]::new($m, $s - $m), [System.Drawing.PointF]::new($s * 0.5, $s * 0.5), [System.Drawing.PointF]::new($s - $m, $m))
    } else {
        @([System.Drawing.PointF]::new($m, $s - $m), [System.Drawing.PointF]::new($s * 0.42, $s * 0.55),
          [System.Drawing.PointF]::new($s * 0.56, $s * 0.66), [System.Drawing.PointF]::new($s - $m, $m))
    }
    $g.DrawLines($pen, $pts)
    $dot = [Math]::Max(2.0, $s / 7.0)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $gold), ($s - $m - $dot / 2), ($m - $dot / 2), $dot, $dot)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: header, one directory entry per size, then the PNG data.
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset); $offset += $len
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Close()
"wrote $out ($((Get-Item $out).Length) bytes, sizes $($sizes -join ', '))"
