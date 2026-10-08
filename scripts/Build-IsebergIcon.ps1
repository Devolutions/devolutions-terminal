[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SkiaSharpAssemblyPath,
    [Parameter(Mandatory)]
    [string] $NativeLibraryPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'assets\profile-icons\iseberg.svg'
$output = Join-Path $root 'assets\profile-icons\iseberg.scale-100.png'

Add-Type -Path (Resolve-Path -LiteralPath $SkiaSharpAssemblyPath).Path
$nativeHandle = [System.Runtime.InteropServices.NativeLibrary]::Load(
    (Resolve-Path -LiteralPath $NativeLibraryPath).Path)
try {
    $readerSettings = [System.Xml.XmlReaderSettings]::new()
    $readerSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $readerSettings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($source, $readerSettings)
    try {
        $svg = [System.Xml.Linq.XDocument]::Load($reader)
    } finally {
        $reader.Dispose()
    }
    $info = [SkiaSharp.SKImageInfo]::new(128, 128)
    $surface = [SkiaSharp.SKSurface]::Create($info)
    try {
        $surface.Canvas.Clear([SkiaSharp.SKColors]::Transparent)
        $surface.Canvas.Scale(4)
        foreach ($element in $svg.Root.Elements()) {
            if ($element.Name.LocalName -in @('title', 'desc')) { continue }
            if ($element.Name.LocalName -ne 'path') {
                throw "Unsupported icon element: $($element.Name.LocalName)"
            }
            $path = [SkiaSharp.SKPath]::ParseSvgPathData($element.Attribute('d').Value)
            if ($null -eq $path) { throw 'Invalid SVG path.' }
            $paint = [SkiaSharp.SKPaint]::new()
            try {
                $paint.IsAntialias = $true
                $paint.Color = [SkiaSharp.SKColor]::Parse($element.Attribute('fill').Value)
                $surface.Canvas.DrawPath($path, $paint)
            } finally {
                $paint.Dispose()
                $path.Dispose()
            }
        }
        $image = $surface.Snapshot()
        try {
            $data = $image.Encode([SkiaSharp.SKEncodedImageFormat]::Png, 100)
            try {
                [System.IO.File]::WriteAllBytes($output, $data.ToArray())
            } finally {
                $data.Dispose()
            }
        } finally {
            $image.Dispose()
        }
    } finally {
        $surface.Dispose()
    }
} finally {
    [System.Runtime.InteropServices.NativeLibrary]::Free($nativeHandle)
}
Write-Output $output
