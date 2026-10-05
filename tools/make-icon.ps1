# Turns the artwork into the app icon, all sizes in one file.
#   pwsh tools\make-icon.ps1 src\DraftKeeper\Resources\draft-keeper.png src\DraftKeeper\Resources\draft-keeper.ico
#
# Below 32 pixels it's just the hood and the glowing page. The whole figure at 16 pixels
# is an orange smudge.

param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$cropBelow = 32          # frames under this size are cropped in
$crop = @(0.24, 0.02, 0.52, 0.74)   # x, y, width, height as a fraction of the source

$original = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    if ($s -lt $cropBelow) {
        $sx = [int]($original.Width * $crop[0])
        $sy = [int]($original.Height * $crop[1])
        $sw = [int]($original.Width * $crop[2])
        $sh = [int]($original.Height * $crop[3])
        # square crop, or it stretches
        $side = [Math]::Max($sw, $sh)
        $sx = $sx - [int](($side - $sw) / 2)
        $sy = $sy - [int](($side - $sh) / 2)
        $g.DrawImage($original,
            (New-Object System.Drawing.Rectangle 0, 0, $s, $s),
            (New-Object System.Drawing.Rectangle $sx, $sy, $side, $side),
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    else {
        $g.DrawImage($original, 0, 0, $s, $s)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @($s, $ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}
$original.Dispose()

# .ico layout: 6 byte header, 16 bytes per size, then the PNGs.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $size = $f[0]; $bytes = $f[1]
    $dim = if ($size -ge 256) { 0 } else { $size }   # 256 is recorded as zero
    $w.Write([Byte]$dim); $w.Write([Byte]$dim)
    $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($f in $frames) { $w.Write($f[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Destination, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Output "wrote $Destination, $($frames.Count) sizes, $((Get-Item $Destination).Length) bytes"
