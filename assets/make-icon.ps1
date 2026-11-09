# Backup360 logosunu üretir: assets\logo.png ve assets\Backup360.ico
# Kullanım: powershell -ExecutionPolicy Bypass -File assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-Logo([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $s = $size / 512.0

    # Yuvarlak köşeli zemin
    $r = 112 * $s
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($size - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($size - 2 * $r - 1, $size - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $size - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object Drawing.RectangleF 0, 0, $size, $size
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(79, 70, 229)), ([Drawing.Color]::FromArgb(6, 182, 212)), 45.0
    $g.FillPath($bg, $path)

    # 360 derece halkası (sonunda ok başı)
    $ringPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(235, 255, 255, 255)), (30 * $s)
    $ringPen.StartCap = 'Round'
    $ringPen.EndCap = 'Flat'
    $ring = New-Object Drawing.RectangleF (84 * $s), (84 * $s), (344 * $s), (344 * $s)
    $g.DrawArc($ringPen, $ring, -55, 305)
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $cx = 256 * $s; $cy = 256 * $s; $rad = 172 * $s
    $a = (-110.0) * [Math]::PI / 180.0
    $tipX = $cx + $rad * [Math]::Cos($a); $tipY = $cy + $rad * [Math]::Sin($a)
    $arrow = @(
        (New-Object Drawing.PointF ($tipX + 62 * $s), ($tipY + 0 * $s)),
        (New-Object Drawing.PointF ($tipX - 26 * $s), ($tipY - 50 * $s)),
        (New-Object Drawing.PointF ($tipX - 26 * $s), ($tipY + 50 * $s))
    )
    $g.FillPolygon($white, [Drawing.PointF[]]$arrow)

    # Bulut
    $cloud = New-Object Drawing.Drawing2D.GraphicsPath
    $cloud.AddEllipse((158 * $s), (240 * $s), (96 * $s), (96 * $s))
    $cloud.AddEllipse((206 * $s), (192 * $s), (120 * $s), (120 * $s))
    $cloud.AddEllipse((276 * $s), (246 * $s), (86 * $s), (86 * $s))
    $g.FillPath($white, $cloud)
    $g.FillRectangle($white, (206 * $s), (284 * $s), (112 * $s), (52 * $s))

    # Buluttaki yukarı ok
    $brand = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(79, 70, 229))
    $up = @(
        (New-Object Drawing.PointF (260 * $s), (218 * $s)),
        (New-Object Drawing.PointF (302 * $s), (266 * $s)),
        (New-Object Drawing.PointF (274 * $s), (266 * $s)),
        (New-Object Drawing.PointF (274 * $s), (316 * $s)),
        (New-Object Drawing.PointF (246 * $s), (316 * $s)),
        (New-Object Drawing.PointF (246 * $s), (266 * $s)),
        (New-Object Drawing.PointF (218 * $s), (266 * $s))
    )
    $g.FillPolygon($brand, [Drawing.PointF[]]$up)
    $g.Dispose()
    return $bmp
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$logo = New-Logo 512
$logo.Save((Join-Path $here 'logo.png'), [Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($n in $sizes) {
    $b = New-Logo $n
    $m = New-Object IO.MemoryStream
    $b.Save($m, [Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    , $m.ToArray()
}

$out = [IO.File]::Create((Join-Path $here 'Backup360.ico'))
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]
    $dim = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Close()
Write-Host 'logo.png ve Backup360.ico üretildi.'
