# Regenerate the multi-resolution Windows icon using built-in drawing APIs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outputPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'LLMChoir.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform(($size / 256.0), ($size / 256.0))
    $bubble = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bubble.AddArc(8, 16, 64, 64, 180, 90)
    $bubble.AddArc(184, 16, 64, 64, 270, 90)
    $bubble.AddArc(184, 144, 64, 64, 0, 90)
    $bubble.AddLine(184, 208, 98, 208)
    $bubble.AddLine(98, 208, 48, 244)
    $bubble.AddLine(48, 244, 48, 208)
    $bubble.AddArc(8, 144, 64, 64, 90, 90)
    $bubble.CloseFigure()
    $background = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 35, 39, 47))
    $graphics.FillPath($background, $bubble)
    $colors = @('#48D7C1', '#FFD166', '#FF7891')
    $tops = @(92, 60, 80)
    $bottoms = @(148, 172, 160)
    for ($i = 0; $i -lt 3; $i++) {
        $pen = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml($colors[$i]), 30)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $x = 72 + 56 * $i
        $graphics.DrawLine($pen, $x, $tops[$i], $x, $bottoms[$i])
        $pen.Dispose()
    }
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,($stream.ToArray())
    $stream.Dispose()
    $background.Dispose()
    $bubble.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
$file = [System.IO.File]::Create($outputPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = $sizes[$i] % 256
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally {
    $writer.Dispose()
}
Write-Output "Created $outputPath ($($sizes.Count) sizes)."
