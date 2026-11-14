$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$app=Join-Path $root 'src\SpankyhoOptimizer\Assets'
$install=Join-Path $root 'installer'
$main=[IO.File]::ReadAllBytes((Join-Path $app 'App.ico'))
$setup=[IO.File]::ReadAllBytes((Join-Path $install 'App.ico'))
if(-not [Collections.StructuralComparisons]::StructuralEqualityComparer.Equals($main,$setup)){
  throw 'Application and installer icons must be identical.'
}
function Test-MultiResolutionIcon([string]$path){
  $data=[IO.File]::ReadAllBytes($path)
  if($data.Length -lt 64 -or [BitConverter]::ToUInt16($data,2) -ne 1){throw "Invalid ICO: $path"}
  $count=[BitConverter]::ToUInt16($data,4)
  $sizes=[Collections.Generic.HashSet[int]]::new()
  for($i=0;$i -lt $count;$i++){
    $idx=6+$i*16
    $size=if($data[$idx] -eq 0){256}else{[int]$data[$idx]}
    $width=if($data[$idx+1] -eq 0){256}else{[int]$data[$idx+1]}
    $length=[BitConverter]::ToUInt32($data,$idx+8)
    $offset=[BitConverter]::ToUInt32($data,$idx+12)
    if($size -ne $width -or $length -lt 50 -or $offset+$length -gt $data.Length){throw "Corrupt ICO frame: $path frame=$i"}
    if([BitConverter]::ToUInt32($data,[int]$offset) -ne 0x474e5089){throw "Frame is not PNG-backed: $path frame=$i"}
    [void]$sizes.Add($size)
  }
  foreach($needed in @(16,20,24,32,40,48,64,128,256)){
    if(-not $sizes.Contains($needed)){throw "Missing $needed x $needed frame: $path"}
  }
  return $count
}
$frames=Test-MultiResolutionIcon (Join-Path $app 'App.ico')
foreach($s in @('Warning','Error')){
  [void](Test-MultiResolutionIcon (Join-Path $app "Tray$s.ico"))
}
$png=[IO.File]::ReadAllBytes((Join-Path $app 'AppLogo.png'))
if($png.Length -lt 5000 -or [BitConverter]::ToUInt32($png,0) -ne 0x474e5089){throw 'Invalid AppLogo.png'}
$project=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'))
foreach($s in @('TrayWarning.ico','TrayError.ico','App.ico','AppLogo.png')){
  if(-not $project.Contains($s)){throw "Icon missing from app project: $s"}
}
$xaml=[IO.File]::ReadAllText((Join-Path $install 'ModernInstaller.xaml'))
if(-not $xaml.Contains('CheckPlate') -or -not $xaml.Contains('M 4,4 L 16,16') -or
   -not $xaml.Contains('SingleGlassSurface')) {
  throw 'New single-surface installer identity missing.'
}
if(-not $xaml.Contains('GlassExpander') -or -not $xaml.Contains('#EE0B367B') -or
   -not $xaml.Contains('#AA020B30')) {
  throw 'Installer contrast/error-sheet policy missing.'
}
$installerCode=[IO.File]::ReadAllText((Join-Path $install 'InstallerWindow.Restored.cs'))
# The installer uses readable, medium-opacity dark glass: wallpaper remains
# subtly visible, while the large progress panel is deliberately darker.
# The user does not want runtime version badges or a MODERN SETUP caption.
# Keep runtime detection/auto-install available via the install progress and logs.
if(-not $installerCode.Contains('base.AllowsTransparency = true;') -or
   -not $installerCode.Contains('if (!base.AllowsTransparency)') -or
   -not $xaml.Contains('Background="#770B1729"') -or
   -not $xaml.Contains('Background="#480E243F"') -or
   -not $xaml.Contains('BorderThickness="0" BorderBrush="Transparent"') -or
   $xaml.Contains('Background="#B8DEFAFF"') -or
   -not $xaml.Contains('CornerRadius="23"') -or
   -not $xaml.Contains('x:Name="InstallProgress"') -or
   -not $xaml.Contains('Height="21"') -or
   -not $xaml.Contains('x:Name="PART_Indicator"') -or
   -not $xaml.Contains('x:Name="ProgressPercent"') -or
   $xaml.Contains('x:Name="HardwareCard"') -or
   $xaml.Contains('x:Name="DotNetVisibleStatus"') -or
   $xaml.Contains('x:Name="WebViewVisibleStatus"') -or
   $xaml.Contains('x:Name="InstallerVersionLabel"') -or
   $installerCode.Contains('UpdateVisibleRequirement(') -or
   $installerCode.Contains('ReadSystemSpecificationsAsync(')) {
  throw 'Balanced glass installer / clean header / real progress contract failed.'
}
# Reject nearly white rims in regular, focus, hover and failure UI states.
# This deliberately leaves white lettering and vector symbols untouched.
$borderRefs=[regex]::Matches($xaml,'(?:BorderBrush="|Property="BorderBrush"\s+Value=")(#[A-Fa-f0-9]{8}|White)"','IgnoreCase')
foreach($m in $borderRefs){
  $v=$m.Groups[1].Value
  if($v -eq 'White'){throw 'Installer white border detected.'}
  $alpha=[Convert]::ToInt32($v.Substring(1,2),16)
  $red=[Convert]::ToInt32($v.Substring(3,2),16)
  $green=[Convert]::ToInt32($v.Substring(5,2),16)
  $blue=[Convert]::ToInt32($v.Substring(7,2),16)
  if($alpha -ge 50 -and $red -ge 145 -and $green -ge 175 -and $blue -ge 185){
    throw "Installer near-white border detected: $v"
  }
}
if(-not $xaml.Contains('Color="#7C4276A8"') -or
   -not $xaml.Contains('Color="#62507AA6"') -or
   -not $xaml.Contains('Property="IsKeyboardFocusWithin"') -or
   -not $xaml.Contains('Property="FocusVisualStyle"')){
  throw 'Dark edge palette/focus-safe textbox was reverted.'
}
# Verify visible saturated segments in the source logo itself.
Add-Type -AssemblyName System.Drawing
$bitmap=[Drawing.Bitmap]::new((Join-Path $app 'AppLogo.png'))
$colors=@{Cyan=0;Pink=0;Orange=0;Green=0}
for($y=0;$y -lt $bitmap.Height;$y+=8){
  for($x=0;$x -lt $bitmap.Width;$x+=8){
    $px=$bitmap.GetPixel($x,$y)
    if($px.A -lt 190){continue}
    if($px.B -gt 180 -and $px.G -gt 175 -and $px.R -lt 85){$colors.Cyan++}
    if($px.R -gt 180 -and $px.B -gt 130 -and $px.G -lt 120){$colors.Pink++}
    if($px.R -gt 180 -and $px.G -gt 110 -and $px.G -lt 225 -and $px.B -lt 95){$colors.Orange++}
    if($px.G -gt 180 -and $px.R -lt 135 -and $px.B -ge 95 -and $px.B -lt 235){$colors.Green++}
  }
}
$bitmap.Dispose()
foreach($key in $colors.Keys){if($colors[$key] -lt 15){throw "Bright $key icon segment too small or absent: $($colors[$key])"}}
$tray=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\FanTrayIconService.cs'))
if(-not $tray.Contains('SelectTrayIconSize') -or -not $tray.Contains('GetDpiForSystem') -or
   -not $tray.Contains('_trayIconSize, _trayIconSize')) {
  throw 'DPI-scaled tray asset selection is missing.'
}
Write-Host "BRAND_ASSET_GUARD_OK matched=true icoFrames=$frames variants=3 balancedGlass=true cleanHeader=true roundedTransparency=true realProgress=true noHardware=true dpiTray=true errorContrast=true noWhiteOutlines=true multicolorSegments=4"
