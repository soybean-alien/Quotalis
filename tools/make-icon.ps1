# Generates the Quotalis icon: Assets/Quotalis.ico (multi-resolution PNG frames), Assets/Quotalis-256.png,
# and the installer's wizard images (installer/wizard-*.bmp).
# The mark: on a black rounded square, the Claude mark's left half (Claude orange) and the OpenAI mark's right
# half (Codex blue), ringed by an orange arc on the left and a blue arc on the right. Each size is drawn from
# vectors; at 32 px and below the arcs get thicker so they survive the tray and taskbar.
# Runs in Windows PowerShell (WPF): powershell -STA -ExecutionPolicy Bypass -File tools/make-icon.ps1
param(
    [string]$AssetsDir = "$PSScriptRoot\..\src\Quotalis\Assets",
    [string]$InstallerDir = "$PSScriptRoot\..\installer"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

$marks = Get-Content "$PSScriptRoot\..\src\Quotalis\UI\Components\BrandMarks.cs" -Raw
$claude = [regex]::Match($marks, 'Claude = "([^"]+)"').Groups[1].Value
$openai = [regex]::Match($marks, 'OpenAI = "([^"]+)"').Groups[1].Value
$orange = '#D97757'; $blue = '#3A9BFF'; $black = '#0A0A0B'

# The icon on a 256-unit canvas. $small: thicker arcs and a slightly larger mark for 32 px and below.
function Get-IconXaml([bool]$small, [string]$background = 'Transparent') {
    $arc = if ($small) { 16 } else { 10 }
    $r = if ($small) { 96 } else { 100 }
    $s = if ($small) { 5.9 } else { 5.5 }
    $o = 128 - 12 * $s
    $top = 128 - $r; $bottom = 128 + $r
    $half = { param($data, $fill, $clip) "<Canvas Clip='$clip'><Path Data='$data' Fill='$fill' RenderTransform='$s,0,0,$s,$o,$o'/></Canvas>" }
    @"
<Canvas xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='256' Height='256' Background='$background'>
  <Rectangle Width='256' Height='256' RadiusX='58' RadiusY='58' Fill='$black'/>
  $(& $half $claude $orange 'M0,0 H128 V256 H0 Z')
  $(& $half $openai $blue 'M128,0 H256 V256 H128 Z')
  <Path Data='M128,$top A$r,$r 0 0 0 128,$bottom' Stroke='$orange' StrokeThickness='$arc' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
  <Path Data='M128,$top A$r,$r 0 0 1 128,$bottom' Stroke='$blue' StrokeThickness='$arc' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
</Canvas>
"@
}

function Render-Element($element, [int]$width, [int]$height) {
    $element.Measure([Windows.Size]::new($width, $height))
    $element.Arrange([Windows.Rect]::new(0, 0, $width, $height))
    $element.UpdateLayout()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $width, $height, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($element)
    $bitmap
}

function Get-Icon([int]$size, [string]$background = 'Transparent') {
    $box = New-Object Windows.Controls.Viewbox
    $box.Width = $size; $box.Height = $size
    $box.Child = [Windows.Markup.XamlReader]::Parse((Get-IconXaml ($size -le 32) $background))
    Render-Element $box $size $size
}

function Save-Image($bitmap, [string]$path, [string]$format) {
    $encoder = if ($format -eq 'bmp') { New-Object Windows.Media.Imaging.BmpBitmapEncoder } else { New-Object Windows.Media.Imaging.PngBitmapEncoder }
    $source = if ($format -eq 'bmp') { New-Object Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([Windows.Media.PixelFormats]::Bgr24), $null, 0 } else { $bitmap }
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($source))
    $stream = [IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Get-PngBytes($bitmap) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    , $stream.ToArray()
}

# Application icon: PNG frames in one .ico.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) { , @($size, (Get-PngBytes (Get-Icon $size))) }
$out = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $out
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $size = $frame[0]; $data = $frame[1]
    $b = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$b); $writer.Write([byte]$b); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$data.Length); $writer.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $AssetsDir 'Quotalis.ico'), $out.ToArray())
Save-Image (Get-Icon 256) (Join-Path $AssetsDir 'Quotalis-256.png') 'png'

# Installer wizard: the tall welcome panel (black, icon centred) and the small header image (icon on white),
# each at 100 % and 200 % scale.
foreach ($scale in 1, 2) {
    $w = 164 * $scale; $h = 314 * $scale; $icon = 112 * $scale
    $panel = New-Object Windows.Controls.Canvas
    $panel.Width = $w; $panel.Height = $h; $panel.Background = [Windows.Media.BrushConverter]::new().ConvertFrom($black)
    $box = New-Object Windows.Controls.Viewbox
    $box.Width = $icon; $box.Height = $icon
    $box.Child = [Windows.Markup.XamlReader]::Parse((Get-IconXaml $false))
    [Windows.Controls.Canvas]::SetLeft($box, ($w - $icon) / 2); [Windows.Controls.Canvas]::SetTop($box, ($h - $icon) / 2 - 20 * $scale)
    $panel.Children.Add($box) | Out-Null
    $suffix = if ($scale -eq 1) { '' } else { '-200' }
    Save-Image (Render-Element $panel $w $h) (Join-Path $InstallerDir "wizard-large$suffix.bmp") 'bmp'
    Save-Image (Get-Icon (55 * $scale) 'White') (Join-Path $InstallerDir "wizard-small$suffix.bmp") 'bmp'
}

Write-Output "Icon written to $AssetsDir; wizard images to $InstallerDir"
