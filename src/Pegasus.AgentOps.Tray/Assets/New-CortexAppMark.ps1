[CmdletBinding()]
param([switch]$RoleIconsOnly)

# Source-only native asset resizing. Role coordinates use the existing 64-unit
# viewbox; the full-contrast raster master supplies the current Cortex artwork.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$artwork = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'CortexRoleArtwork.psd1')
if ($artwork.SchemaVersion -ne 1) { throw 'Unsupported Cortex role artwork schema.' }
$masterPath = Join-Path $PSScriptRoot 'Cortex.Graphic.png'
$master = [System.Drawing.Image]::FromFile($masterPath)
if ($master.Width -ne $master.Height -or $master.Width -lt 256 -or $master.Width -gt 8192) {
    $master.Dispose()
    throw 'The Cortex graphic master must be square, between 256 and 8192 pixels.'
}

function Get-AppMarkSvg([hashtable]$Badge = $null) {
    $label = if ($null -eq $Badge) { 'Pegasus Cortex' } else { 'Pegasus Cortex — ' + $Badge.Label }
    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add(('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="{0}">' -f [Security.SecurityElement]::Escape($label)))
    $parts.Add('  <title>' + [Security.SecurityElement]::Escape($label) + '</title>')
    $parts.Add('  <defs><clipPath id="cortex-tile"><rect x="1" y="1" width="62" height="62" rx="14"/></clipPath></defs>')
    $parts.Add('  <image href="Cortex.Graphic.png" width="64" height="64" preserveAspectRatio="xMidYMid meet" clip-path="url(#cortex-tile)"/>')
    if ($null -ne $Badge) { $parts.Add((Get-BadgeSvg $Badge)) }
    $parts.Add('</svg>')
    return $parts -join "`n"
}

function Get-BadgeSvg([hashtable]$Definition) {
    $pieces = [System.Collections.Generic.List[string]]::new()
    $pieces.Add('<circle cx="49" cy="49" r="14" fill="#F8FAFF" stroke="#245BCE" stroke-width="2"/>')
    $pieces.Add('<g fill="none" stroke="#0B234A" stroke-width="3.2" stroke-linecap="round" stroke-linejoin="round">')
    foreach ($line in $Definition.Lines) {
        $points = for ($index = 0; $index -lt $line.Points.Count; $index += 2) { '{0},{1}' -f $line.Points[$index], $line.Points[$index + 1] }
        $pieces.Add(('<polyline points="{0}"/>' -f ($points -join ' ')))
    }
    foreach ($rectangle in $Definition.Rectangles) {
        $pieces.Add(('<rect x="{0}" y="{1}" width="{2}" height="{3}"/>' -f $rectangle.X, $rectangle.Y, $rectangle.Width, $rectangle.Height))
    }
    foreach ($circle in $Definition.Circles) {
        $fill = if ($circle.Filled) { '#0B234A' } else { 'none' }
        $radius = ([double]$circle.Radius).ToString([Globalization.CultureInfo]::InvariantCulture)
        $pieces.Add(('<circle cx="{0}" cy="{1}" r="{2}" fill="{3}"/>' -f $circle.X, $circle.Y, $radius, $fill))
    }
    foreach ($polygon in $Definition.Polygons) {
        $points = for ($index = 0; $index -lt $polygon.Points.Count; $index += 2) { '{0},{1}' -f $polygon.Points[$index], $polygon.Points[$index + 1] }
        $pieces.Add(('<polygon points="{0}" fill="#0B234A" stroke="none"/>' -f ($points -join ' ')))
    }
    $pieces.Add('</g>')
    return $pieces -join "`n"
}

function Draw-RoleBadge($Graphics, [hashtable]$Definition, [int]$Pixels) {
    $paper = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F8FAFF'))
    $ink = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#0B234A'))
    $border = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#245BCE'), 2)
    # Optical correction preserves a one-pixel symbol stroke in the smallest shell/tray sizes.
    $strokeWidth = if ($Pixels -le 20) { 4.0 } else { 3.2 }
    $stroke = [System.Drawing.Pen]::new($ink.Color, $strokeWidth)
    try {
        $stroke.StartCap = $stroke.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $stroke.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $Graphics.FillEllipse($paper, 35, 35, 28, 28)
        $Graphics.DrawEllipse($border, 35, 35, 28, 28)
        foreach ($line in $Definition.Lines) {
            for ($index = 0; $index -lt $line.Points.Count - 2; $index += 2) {
                $Graphics.DrawLine($stroke, [single]$line.Points[$index], [single]$line.Points[$index + 1],
                    [single]$line.Points[$index + 2], [single]$line.Points[$index + 3])
            }
        }
        foreach ($rectangle in $Definition.Rectangles) {
            $Graphics.DrawRectangle($stroke, [single]$rectangle.X, [single]$rectangle.Y, [single]$rectangle.Width, [single]$rectangle.Height)
        }
        foreach ($circle in $Definition.Circles) {
            $bounds = [System.Drawing.RectangleF]::new($circle.X - $circle.Radius, $circle.Y - $circle.Radius, $circle.Radius * 2, $circle.Radius * 2)
            if ($circle.Filled) { $Graphics.FillEllipse($ink, $bounds) } else { $Graphics.DrawEllipse($stroke, $bounds) }
        }
        foreach ($polygon in $Definition.Polygons) {
            $points = for ($index = 0; $index -lt $polygon.Points.Count; $index += 2) {
                [System.Drawing.PointF]::new($polygon.Points[$index], $polygon.Points[$index + 1])
            }
            $Graphics.FillPolygon($ink, [System.Drawing.PointF[]]$points)
        }
    } finally { $stroke.Dispose(); $border.Dispose(); $ink.Dispose(); $paper.Dispose() }
}

function New-AppMarkBitmap([int]$Pixels, [hashtable]$Badge = $null) {
    $canvas = [System.Drawing.Bitmap]::new($Pixels * 4, $Pixels * 4)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = $Pixels * 4 / 64.0
        $graphics.ScaleTransform($scale, $scale)
        $shape.AddArc(1, 1, 28, 28, 180, 90)
        $shape.AddArc(35, 1, 28, 28, 270, 90)
        $shape.AddArc(35, 35, 28, 28, 0, 90)
        $shape.AddArc(1, 35, 28, 28, 90, 90)
        $shape.CloseFigure()
        $graphics.SetClip($shape)
        $graphics.DrawImage($master, [System.Drawing.RectangleF]::new(0, 0, 64, 64))
        $graphics.ResetClip()
        if ($null -ne $Badge) { Draw-RoleBadge $graphics $Badge $Pixels }
        $result = [System.Drawing.Bitmap]::new($Pixels, $Pixels)
        $downsample = [System.Drawing.Graphics]::FromImage($result)
        try {
            $downsample.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $downsample.DrawImage($canvas, 0, 0, $Pixels, $Pixels)
        } finally { $downsample.Dispose() }
        return ,$result
    } finally {
        $shape.Dispose(); $graphics.Dispose(); $canvas.Dispose()
    }
}

function Write-AppMarkFiles([string]$Name, [hashtable]$Badge = $null) {
    $preview = New-AppMarkBitmap 512 $Badge
    try { $preview.Save((Join-Path $PSScriptRoot ($Name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $preview.Dispose() }

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-AppMarkBitmap $size $Badge
    $stream = [System.IO.MemoryStream]::new()
    try { $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); $frames.Add($stream.ToArray()) }
    finally { $stream.Dispose(); $bitmap.Dispose() }
}
$output = [System.IO.File]::Create((Join-Path $PSScriptRoot ($Name + '.ico')))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $output.Dispose() }
}

try {
if (-not $RoleIconsOnly) {
    Write-AppMarkFiles 'Pegasus.AgentOps'
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'CortexAppMark.svg'), (Get-AppMarkSvg), [Text.UTF8Encoding]::new($false))
}
foreach ($role in @($artwork.Roles.Keys | Sort-Object)) {
    if ($role -cnotmatch '^[A-Z][A-Za-z]+$') { throw 'Invalid role artwork name.' }
    $definition = $artwork.Roles[$role]
    $name = 'Cortex.' + $role
    Write-AppMarkFiles $name $definition
    $svg = Get-AppMarkSvg $definition
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot ($name + '.svg')), $svg, [Text.UTF8Encoding]::new($false))
}

# One local review sheet shows actual-size and nearest-neighbor enlarged pixels on both shell backgrounds.
$roles = @('Main') + @($artwork.Roles.Keys | Sort-Object)
$sheet = [System.Drawing.Bitmap]::new(790, 44 + 72 * $roles.Count)
$review = [System.Drawing.Graphics]::FromImage($sheet)
$font = [System.Drawing.Font]::new('Segoe UI', 10)
$lightInk = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#172B4D'))
$dark = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#101A2D'))
try {
    $review.Clear([System.Drawing.Color]::White)
    $review.FillRectangle($dark, 490, 0, 300, $sheet.Height)
    $review.DrawString('Cortex role', $font, $lightInk, 12, 12)
    $review.DrawString('16    16 x3      32      32 x2', $font, $lightInk, 204, 12)
    $review.DrawString('16    16 x3      32      32 x2', $font, [System.Drawing.Brushes]::White, 504, 12)
    for ($row = 0; $row -lt $roles.Count; $row++) {
        $role = $roles[$row]
        $badge = if ($role -eq 'Main') { $null } else { $artwork.Roles[$role] }
        $small = New-AppMarkBitmap 16 $badge
        $normal = New-AppMarkBitmap 32 $badge
        $top = 44 + 72 * $row
        try {
            $label = if ($role -eq 'Main') { 'Main application' } else { $badge.Label }
            $review.DrawString($label, $font, $lightInk, 12, $top + 20)
            foreach ($left in @(204, 504)) {
                $review.DrawImageUnscaled($small, $left, $top + 24)
                $review.DrawImageUnscaled($normal, $left + 108, $top + 16)
                $review.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                $review.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
                $review.DrawImage($small, $left + 38, $top + 8, 48, 48)
                $review.DrawImage($normal, $left + 164, $top, 64, 64)
            }
        } finally { $normal.Dispose(); $small.Dispose() }
    }
    $sheet.Save((Join-Path $PSScriptRoot 'Cortex.IconFamily.preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally { $dark.Dispose(); $lightInk.Dispose(); $font.Dispose(); $review.Dispose(); $sheet.Dispose() }
} finally { $master.Dispose() }
