# Makes the installer's side picture (164x314) and small corner picture (55x55) from the logo.
# Run from the repository root after make-logo.ps1:  powershell -File installer\make-wizard-images.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$logo = [Drawing.Image]::FromFile((Join-Path $root 'src\WaveFlume.App\Assets\WaveFlume.png'))

function New-Canvas($w, $h) {
    $bmp = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'ClearTypeGridFit'
    return @($bmp, $g)
}

# Small picture: logo on white.
$bmp, $g = New-Canvas 55 55
$g.Clear([Drawing.Color]::White)
$g.DrawImage($logo, 3, 3, 49, 49)
$bmp.Save((Join-Path $PSScriptRoot 'wizard-small.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose()

# Large picture: soft blue panel, logo and name.
$bmp, $g = New-Canvas 164 314
$rect = New-Object Drawing.Rectangle(0, 0, 164, 314)
$brush = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(0xF2, 0xF8, 0xFD), [Drawing.Color]::FromArgb(0xBF, 0xDD, 0xF0), 90)
$g.FillRectangle($brush, $rect)
$g.DrawImage($logo, 22, 70, 120, 120)
$fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'
$title = New-Object Drawing.Font('Segoe UI', 17, [Drawing.FontStyle]::Bold)
$sub = New-Object Drawing.Font('Segoe UI', 9)
$ink = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(0x0B, 0x3C, 0x6E))
$grey = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(0x50, 0x50, 0x50))
$g.DrawString('Wave Flume', $title, $ink, [Drawing.RectangleF]::new(0, 200, 164, 34), $fmt)
$g.DrawString('Wavemaker control', $sub, $grey, [Drawing.RectangleF]::new(0, 232, 164, 20), $fmt)
$bmp.Save((Join-Path $PSScriptRoot 'wizard-large.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose(); $logo.Dispose()
"Wrote wizard images to $PSScriptRoot"
