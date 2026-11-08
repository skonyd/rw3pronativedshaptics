param([string]$AssetDirectory = (Join-Path $PSScriptRoot '../src/RW3ProNativeDSHaptics/Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assetPath = (Resolve-Path -LiteralPath $AssetDirectory).Path
[xml]$source = Get-Content -LiteralPath (Join-Path $assetPath 'AppIcon.svg') -Raw
function Brush([string]$color) {
    if (!$color -or $color -eq 'none') { return $null }
    return [System.Windows.Media.BrushConverter]::new().ConvertFromInvariantString($color)
}
function Render-Icon([int]$size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 256.0, $size / 256.0))
    foreach ($element in $source.DocumentElement.ChildNodes) {
        if ($element.NodeType -ne 'Element') { continue }
        $fill = Brush $element.GetAttribute('fill')
        $pen = $null
        if ($element.GetAttribute('stroke')) {
            $pen = [System.Windows.Media.Pen]::new((Brush $element.GetAttribute('stroke')), [double]$element.GetAttribute('stroke-width'))
            $pen.StartLineCap = $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
            $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        }
        switch ($element.LocalName) {
            'rect' {
                $rect = [System.Windows.Rect]::new([double]$element.x, [double]$element.y, [double]$element.width, [double]$element.height)
                $drawing.DrawRoundedRectangle($fill, $pen, $rect, [double]$element.rx, [double]$element.rx)
            }
            'path' { $drawing.DrawGeometry($fill, $pen, [System.Windows.Media.Geometry]::Parse($element.d)) }
            'circle' { $drawing.DrawEllipse($fill, $pen, [System.Windows.Point]::new([double]$element.cx, [double]$element.cy), [double]$element.r, [double]$element.r) }
            default { throw "Unsupported SVG element: $($element.LocalName)" }
        }
    }
    $drawing.Pop(); $drawing.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [System.IO.MemoryStream]::new()
    try { $encoder.Save($memory); return ,$memory.ToArray() } finally { $memory.Dispose() }
}
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Render-Icon $_) })
$file = [System.IO.File]::Create((Join-Path $assetPath 'App.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
[System.IO.File]::WriteAllBytes((Join-Path $assetPath 'AppIcon.png'), [byte[]]$frames[-1])
Write-Output "Generated App.ico (7 sizes) and AppIcon.png from AppIcon.svg."
