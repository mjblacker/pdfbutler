# Rebuild the PNG and multi-resolution ICO from the same simple geometry as assets/pdfbutler-icon.svg.
# Uses Windows' System.Drawing only; no image tools or fonts are required.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '../assets'
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $navy = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#163342'))
    $paper = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#f7f5ef'))
    $mint = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#65c9bc'))
    $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#b4c5c9'), 8)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(8, 8, 104, 104, 180, 90)
    $path.AddArc(144, 8, 104, 104, 270, 90)
    $path.AddArc(144, 144, 104, 104, 0, 90)
    $path.AddArc(8, 144, 104, 104, 90, 90)
    $path.CloseFigure()
    $g.FillPath($navy, $path)
    $g.FillPolygon($paper, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(66,38), [System.Drawing.PointF]::new(150,38), [System.Drawing.PointF]::new(190,78), [System.Drawing.PointF]::new(190,218), [System.Drawing.PointF]::new(66,218)))
    $g.FillPolygon($mint, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(150,38), [System.Drawing.PointF]::new(150,78), [System.Drawing.PointF]::new(190,78)))
    $g.DrawLine($pen, 94, 100, 162, 100)
    $g.DrawLine($pen, 94, 118, 148, 118)
    $g.FillPolygon($navy, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(89,150), [System.Drawing.PointF]::new(122,166), [System.Drawing.PointF]::new(89,182)))
    $g.FillPolygon($navy, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(167,150), [System.Drawing.PointF]::new(134,166), [System.Drawing.PointF]::new(167,182)))
    $g.FillEllipse($mint, 119, 157, 18, 18)
    $g.FillEllipse($navy, 124, 195, 8, 8)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $assets 'pdfbutler.png'), $stream.ToArray()) }
    $stream.Dispose(); $path.Dispose(); $pen.Dispose(); $mint.Dispose(); $paper.Dispose(); $navy.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assets 'pdfbutler.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
} finally { $writer.Dispose(); $file.Dispose() }
