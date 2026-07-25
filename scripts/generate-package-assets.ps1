[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\packaging\identity\Assets')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

function New-InfiniTranseonLogo {
    param(
        [Parameter(Mandatory)] [int] $Size,
        [Parameter(Mandatory)] [string] $Path
    )

    $bitmap = [Drawing.Bitmap]::new($Size, $Size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $backgroundPath = [Drawing.Drawing2D.GraphicsPath]::new()
    $infinityPath = [Drawing.Drawing2D.GraphicsPath]::new()
    $backgroundBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 20, 23, 31))
    $accentPen = [Drawing.Pen]::new(
        [Drawing.Color]::FromArgb(255, 96, 205, 255),
        [Math]::Max(2, $Size * 0.09))
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)

        $margin = $Size * 0.05
        $diameter = $Size - (2 * $margin)
        $radius = $Size * 0.22
        $backgroundPath.AddArc($margin, $margin, 2 * $radius, 2 * $radius, 180, 90)
        $backgroundPath.AddArc(
            $margin + $diameter - (2 * $radius),
            $margin,
            2 * $radius,
            2 * $radius,
            270,
            90)
        $backgroundPath.AddArc(
            $margin + $diameter - (2 * $radius),
            $margin + $diameter - (2 * $radius),
            2 * $radius,
            2 * $radius,
            0,
            90)
        $backgroundPath.AddArc(
            $margin,
            $margin + $diameter - (2 * $radius),
            2 * $radius,
            2 * $radius,
            90,
            90)
        $backgroundPath.CloseFigure()
        $graphics.FillPath($backgroundBrush, $backgroundPath)

        $accentPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $accentPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $accentPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $center = [Drawing.PointF]::new($Size * 0.5, $Size * 0.5)
        $left = [Drawing.PointF]::new($Size * 0.19, $Size * 0.5)
        $right = [Drawing.PointF]::new($Size * 0.81, $Size * 0.5)
        $infinityPath.StartFigure()
        $infinityPath.AddBezier(
            $center,
            [Drawing.PointF]::new($Size * 0.37, $Size * 0.31),
            [Drawing.PointF]::new($Size * 0.19, $Size * 0.31),
            $left)
        $infinityPath.AddBezier(
            $left,
            [Drawing.PointF]::new($Size * 0.19, $Size * 0.69),
            [Drawing.PointF]::new($Size * 0.37, $Size * 0.69),
            $center)
        $infinityPath.AddBezier(
            $center,
            [Drawing.PointF]::new($Size * 0.63, $Size * 0.31),
            [Drawing.PointF]::new($Size * 0.81, $Size * 0.31),
            $right)
        $infinityPath.AddBezier(
            $right,
            [Drawing.PointF]::new($Size * 0.81, $Size * 0.69),
            [Drawing.PointF]::new($Size * 0.63, $Size * 0.69),
            $center)
        $graphics.DrawPath($accentPen, $infinityPath)

        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $accentPen.Dispose()
        $backgroundBrush.Dispose()
        $infinityPath.Dispose()
        $backgroundPath.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$assets = [ordered]@{
    'StoreLogo.png' = 50
    'Square150x150Logo.png' = 150
    'Square44x44Logo.png' = 44
}
foreach ($asset in $assets.GetEnumerator()) {
    New-InfiniTranseonLogo `
        -Size $asset.Value `
        -Path (Join-Path $outputPath $asset.Key)
}

Get-ChildItem -LiteralPath $outputPath -File |
    Sort-Object Name |
    Select-Object Name, Length
