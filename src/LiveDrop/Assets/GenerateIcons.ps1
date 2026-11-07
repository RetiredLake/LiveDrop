$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
[xml]$source = Get-Content (Join-Path $PSScriptRoot 'LiveDropLogo.svg') -Raw
$ns = New-Object Xml.XmlNamespaceManager($source.NameTable)
$ns.AddNamespace('s', 'http://www.w3.org/2000/svg')
$ring = [Windows.Media.Geometry]::Parse($source.SelectSingleNode('/s:svg/s:path', $ns).GetAttribute('d'))
$bodyNode = $source.SelectSingleNode('/s:svg/s:rect', $ns)
$body = New-Object Windows.Media.RectangleGeometry ([Windows.Rect]::new([double]$bodyNode.x, [double]$bodyNode.y, [double]$bodyNode.width, [double]$bodyNode.height)), ([double]$bodyNode.rx), ([double]$bodyNode.rx)
$panes = [Windows.Media.Geometry]::Parse($source.SelectSingleNode('/s:svg/s:defs/s:mask/s:path', $ns).GetAttribute('d'))
$window = New-Object Windows.Media.CombinedGeometry ([Windows.Media.GeometryCombineMode]::Exclude), $body, $panes
$pen = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::White), 14
foreach ($asset in @(@{Name='Square150x150Logo';Width=150;Height=150}, @{Name='Square44x44Logo';Width=44;Height=44}, @{Name='StoreLogo';Width=50;Height=50}, @{Name='Wide310x150Logo';Width=310;Height=150})) {
    foreach ($scale in @(100, 200, 400)) {
        $width = [int]($asset.Width * $scale / 100)
        $height = [int]($asset.Height * $scale / 100)
        $size = [double][Math]::Min($width, $height)
        $visual = New-Object Windows.Media.DrawingVisual
        $drawing = $visual.RenderOpen()
        $drawing.PushTransform([Windows.Media.TranslateTransform]::new(($width - $size) / 2, ($height - $size) / 2))
        $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 300, $size / 300))
        $drawing.DrawGeometry($null, $pen, $ring)
        $drawing.DrawGeometry([Windows.Media.Brushes]::White, $null, $window)
        $drawing.Pop(); $drawing.Pop(); $drawing.Close()
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $filename = $asset.Name + $(if ($scale -eq 100) { '.png' } else { '.scale-' + $scale + '.png' })
        $stream = [IO.File]::Create((Join-Path $PSScriptRoot $filename))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    }
}
