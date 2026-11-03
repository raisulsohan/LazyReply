# Saves the poster from poster.html as social\lazyreply-poster-4x5.jpg (2160 x 2700), with Edge in the background.
#   powershell -NoProfile -ExecutionPolicy Bypass -File social\export.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$html = (Resolve-Path (Join-Path $PSScriptRoot 'poster.html')).Path
$png = Join-Path $env:TEMP 'lazyreply-poster.png'
$prof = Join-Path $env:TEMP 'lazyreply-poster-edge'
$url = 'file:///' + $html.Replace('\', '/') + '?export'
Start-Process $edge -Wait -WindowStyle Hidden -ArgumentList @('--headless=new', '--disable-gpu', '--hide-scrollbars',
  "--user-data-dir=`"$prof`"", '--force-device-scale-factor=2', '--window-size=1080,1350',
  '--virtual-time-budget=4000', "--screenshot=`"$png`"", "`"$url`"")
for ($i = 0; $i -lt 50 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 200 }

$img = [Drawing.Image]::FromFile($png)
$enc = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ps = New-Object Drawing.Imaging.EncoderParameters 1
$ps.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality), ([long]93)
$out = Join-Path $PSScriptRoot 'lazyreply-poster-4x5.jpg'
$img.Save($out, $enc, $ps)
"$out  $($img.Width) x $($img.Height)  $([math]::Round((Get-Item $out).Length / 1KB)) KB"
$img.Dispose()
Remove-Item $png
