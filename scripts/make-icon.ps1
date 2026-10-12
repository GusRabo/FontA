<#
  Regenerates assets/fonta.ico (16 to 256 px) from the master picture assets/fonta-icon.png, which is a render of
  assets/fonta-icon.svg (the FontA logo without its tagline). Run it only if you change the icon; the generated file is
  committed. The app and the installer both use assets/fonta.ico.
#>
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$master = Join-Path $PSScriptRoot '..\assets\fonta-icon.png'
$out = Join-Path $PSScriptRoot '..\assets\fonta.ico'
$src = [System.Drawing.Image]::FromFile((Resolve-Path $master))

function New-Png([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed images (supported by Windows Vista and later).
$images = foreach ($s in $sizes) { , (New-Png $s) }
$src.Dispose()
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bytes = $images[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$bytes.Length); $w.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $images) { $w.Write($bytes) }
$w.Close(); $fs.Close()
"Wrote $out ($((Get-Item $out).Length) bytes)"
