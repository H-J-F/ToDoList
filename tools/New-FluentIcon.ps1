param([string]$Destination = (Join-Path $PSScriptRoot '../src/ToDoList.App/Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$assetRoot = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($assetRoot) | Out-Null
function Brush([string]$color) { return [Windows.Media.BrushConverter]::new().ConvertFromString($color) }
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256,512)
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($size/128.0,$size/128.0))
    $dc.DrawRoundedRectangle((Brush '#87C7F4'),$null,[Windows.Rect]::new(8,8,112,112),24,24)
    $dc.DrawRoundedRectangle((Brush '#F8FCFF'),$null,[Windows.Rect]::new(29,23,70,84),10,10)
    $pen = [Windows.Media.Pen]::new((Brush '#337EBB'),6)
    $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $check = [Windows.Media.Geometry]::Parse('M 41,46 L 46,51 L 54,40 M 41,70 L 46,75 L 54,64')
    $dc.DrawGeometry($null,$pen,$check)
    $linePen = [Windows.Media.Pen]::new((Brush '#9ACBEA'),5)
    $linePen.StartLineCap = $linePen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $dc.DrawLine($linePen,[Windows.Point]::new(66,46),[Windows.Point]::new(85,46))
    $dc.DrawLine($linePen,[Windows.Point]::new(66,70),[Windows.Point]::new(85,70))
    if ($size -ge 32) { $dc.DrawLine($linePen,[Windows.Point]::new(42,92),[Windows.Point]::new(85,92)) }
    $dc.Pop(); $dc.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new(); $encoder.Save($stream); $bytes = $stream.ToArray(); $stream.Dispose()
    [IO.File]::WriteAllBytes((Join-Path $assetRoot "icon-$size.png"),$bytes)
    if ($size -eq 512) { [IO.File]::WriteAllBytes((Join-Path $assetRoot 'app-icon.png'),$bytes) } else { $frames.Add($bytes) }
}
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assetRoot 'app.ico')))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]7)
    $offset = 6 + 16*7
    for ($i=0; $i -lt 7; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose() }
