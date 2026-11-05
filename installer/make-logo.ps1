# Draws the Wave Flume logo (an original design: three layered waves in a blue disc, transparent outside) and writes
# src\WaveFlume.App\Assets\WaveFlume.png and WaveFlume.ico (16-256 px). Run from the repository root:
#   powershell -STA -File installer\make-logo.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src\WaveFlume.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Brush($hex) { [Windows.Media.BrushConverter]::new().ConvertFromString($hex) }

function New-Logo {
    $canvas = New-Object Windows.Controls.Canvas
    $canvas.Width = 256; $canvas.Height = 256
    $canvas.Background = [Windows.Media.Brushes]::Transparent

    $disc = New-Object Windows.Shapes.Ellipse
    $disc.Width = 248; $disc.Height = 248
    [Windows.Controls.Canvas]::SetLeft($disc, 4); [Windows.Controls.Canvas]::SetTop($disc, 4)
    $grad = New-Object Windows.Media.LinearGradientBrush
    $grad.StartPoint = [Windows.Point]::new(0.15, 0); $grad.EndPoint = [Windows.Point]::new(0.85, 1)
    $grad.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.ColorConverter]::ConvertFromString('#38B6F2'), 0))
    $grad.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.ColorConverter]::ConvertFromString('#0B3C6E'), 1))
    $disc.Fill = $grad
    $canvas.Children.Add($disc) | Out-Null

    # The waves are clipped to the disc.
    $waves = New-Object Windows.Controls.Canvas
    $waves.Width = 256; $waves.Height = 256
    $waves.Clip = [Windows.Media.EllipseGeometry]::new([Windows.Point]::new(128, 128), 124, 124)
    $defs = @(
        @{ dy = 0;  color = '#FFFFFF'; opacity = 1.0;  width = 17 },
        @{ dy = 48; color = '#FFFFFF'; opacity = 0.82; width = 17 },
        @{ dy = 96; color = '#CDEBFF'; opacity = 0.62; width = 17 }
    )
    foreach ($d in $defs) {
        $p = New-Object Windows.Shapes.Path
        $p.Data = [Windows.Media.Geometry]::Parse("M -20,$(70 + $d.dy) C 12,$(30 + $d.dy) 44,$(30 + $d.dy) 76,$(70 + $d.dy) S 108,$(110 + $d.dy) 140,$(70 + $d.dy) S 172,$(30 + $d.dy) 204,$(70 + $d.dy) S 236,$(110 + $d.dy) 268,$(70 + $d.dy)")
        $p.Stroke = Brush $d.color
        $p.StrokeThickness = $d.width
        $p.StrokeStartLineCap = 'Round'; $p.StrokeEndLineCap = 'Round'
        $p.Opacity = $d.opacity
        $waves.Children.Add($p) | Out-Null
    }
    $canvas.Children.Add($waves) | Out-Null
    return $canvas
}

function Render-Png([int]$size) {
    $box = New-Object Windows.Controls.Viewbox
    $box.Stretch = 'Uniform'; $box.Width = $size; $box.Height = $size
    $box.Child = New-Logo
    $box.Measure([Windows.Size]::new($size, $size)); $box.Arrange([Windows.Rect]::new(0, 0, $size, $size)); $box.UpdateLayout()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($box)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = Render-Png $s }

[IO.File]::WriteAllBytes((Join-Path $assets 'WaveFlume.png'), $pngs[256])
[IO.File]::WriteAllBytes((Join-Path $assets 'WaveFlume-128.png'), $pngs[128])

# ICO container with PNG-compressed images.
$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$s].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'WaveFlume.ico'), $ms.ToArray())
"Wrote logo files to $assets"
