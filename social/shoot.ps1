# Renders the real LazyReply inline box and app mark at poster size (no screen, no typing),
# and puts them inside poster.html.
#   powershell -NoProfile -ExecutionPolicy Bypass -File social\shoot.ps1
param([double]$k = 3.6, [int]$width = 520)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '..\LazyReply.exe'))
$bf = [Reflection.BindingFlags]'NonPublic,Public,Instance,Static'
$TT = $asm.GetType('LazyReply.Theme')
$TT.GetMethod('Load', $bf).Invoke($null, @())

# The light palette, whatever the Windows setting is.
function Col($rgb) { [Drawing.Color]::FromArgb(255, ($rgb -shr 16) -band 255, ($rgb -shr 8) -band 255, $rgb -band 255) }
$light = @{ Page=0xFBFAF8; Bg=0xFFFFFF; Card=0xF6F4F0; Text=0x1F1D1A; Muted=0x8A857D; Border=0xECE9E4
            Accent=0x5B6CF0; AccentSoft=0xEEF0FE; Danger=0xD64545; Hover=0xF5F3EF; Bangla=0x2BA36B }
foreach ($n in $light.Keys) { $TT.GetField($n, $bf).SetValue($null, (Col $light[$n])) }
$TT.GetField('OnAccent', $bf).SetValue($null, [Drawing.Color]::White)
$TT.GetField('InputBg', $bf).SetValue($null, [Drawing.Color]::White)
$TT.GetField('Dark', $bf).SetValue($null, $false)

function Fld($o, $n) { $o.GetType().GetField($n, $bf).GetValue($o) }
function SetFld($o, $n, $v) { $o.GetType().GetField($n, $bf).SetValue($o, $v) }
function Call($o, $n, $a) { $o.GetType().GetMethod($n, $bf).Invoke($o, $a) }

$rows = [string[]]@(
  'I will deliver the work by tomorrow evening. Could you please send me the logo file?',
  "Sure thing! I'll have it ready by tomorrow evening. Just send over the logo file when you can.",
  'Will deliver by tomorrow evening. Please send the logo file.')

$PopupT = $asm.GetType('LazyReply.InlinePopup')
$p = [Activator]::CreateInstance($PopupT, $true)
$null = $p.Handle
# Scale the box k times: its own scale factor and its fonts together, so the layout stays the same.
$sc = Fld $p 'sc'
SetFld $p 'sc' ([single]($sc * $k))
foreach ($f in 'fSmall','fBody','fBadge','fClose','fName') {
  $old = Fld $p $f
  SetFld $p $f (New-Object Drawing.Font $old.FontFamily, ([single]($old.Size * $k)), $old.Style)
}
$p.BackColor = [Drawing.Color]::White
SetFld $p 'rows' $rows
SetFld $p 'mode' ([Enum]::Parse($PopupT.GetNestedType('Mode', $bf), 'Suggestions'))
Call $p 'Relayout' @([int]($width * $sc * $k))
"box: $($p.Width) x $($p.Height)"
$bmp = New-Object Drawing.Bitmap $p.Width, $p.Height
$p.DrawToBitmap($bmp, (New-Object Drawing.Rectangle 0, 0, $p.Width, $p.Height))

$mark = $TT.GetMethod('Mark', $bf).Invoke($null, @([int]256, (Col 0x5B6CF0)))

function DataUri($img) {
  $ms = New-Object IO.MemoryStream
  $img.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
  'data:image/png;base64,' + [Convert]::ToBase64String($ms.ToArray())
}
$bmp.Save((Join-Path $PSScriptRoot 'box.png'))
# The pictures go inside poster.html, so the page works alone (also when opened as a data: page).
$js = "<script id=`"assets`">// Made by shoot.ps1 from the real app. Do not edit by hand.`nwindow.ASSETS = {`n  box: '" + (DataUri $bmp) + "',`n  mark: '" + (DataUri $mark) + "'`n};`n</script>"
$page = Join-Path $PSScriptRoot 'poster.html'
$html = [IO.File]::ReadAllText($page, [Text.Encoding]::UTF8)
$re = New-Object Text.RegularExpressions.Regex '<script (src="assets.js"|id="assets")>[\s\S]*?</script>'
if (-not $re.IsMatch($html)) { throw "poster.html has no assets script" }
$html = $re.Replace($html, [Text.RegularExpressions.MatchEvaluator]{ param($m) $js }, 1)
[IO.File]::WriteAllText($page, $html, (New-Object Text.UTF8Encoding $false))
"done"
