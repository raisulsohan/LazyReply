# Builds LazyReply.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Nothing to install. Run:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

# App icon: the same "অ" mark as the tray icon, stored as PNG images inside an .ico file.
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $root 'app.ico'
$sizes = 16, 24, 32, 48, 256
$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [Math]::Max(2, [int]($size / 5)); $d = $r * 2; $w = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(91, 108, 240))), $path)
    $font = New-Object System.Drawing.Font 'Nirmala UI', ([float]($size * 0.62)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]0x0985, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ([float]($size * 0.04)), $size, $size), $sf)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
[System.IO.File]::WriteAllBytes($ico, $out.ToArray())

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $root 'LazyReply.exe'
$wpf = Join-Path (Split-Path -Parent $csc) 'WPF'   # UI Automation lives with the WPF assemblies
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$ico" "/lib:$wpf" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:Accessibility.dll `
    /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "Built $exe"
