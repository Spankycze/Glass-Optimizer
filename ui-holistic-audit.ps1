$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'tools\source-set.ps1')
$src=Join-Path $root 'src\SpankyhoOptimizer'
$errors=New-Object System.Collections.Generic.List[string]

$uiFiles=@(Get-ChildItem $src -File -Filter 'MainWindow*.cs') + @(Get-Item (Join-Path $src 'DesktopUpdateNotificationWindow.cs'))
$fontCount=0
foreach($file in $uiFiles){
  $raw=[IO.File]::ReadAllText($file.FullName)
  foreach($m in [regex]::Matches($raw,'FontSize\s*=\s*(\d+(?:\.\d+)?)')){
    $fontCount++
    $v=[double]::Parse($m.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)
    if($v -lt 10.5){$errors.Add("FONT_TOO_SMALL|$($file.Name)|$v")}
  }
  foreach($m in [regex]::Matches($raw,'FallbackColor\s*=\s*Windows\.UI\.Color\.FromArgb\((\d+)')){
    $a=[int]$m.Groups[1].Value
    if($a -gt 112){$errors.Add("OPAQUE_ACRYLIC_FALLBACK|$($file.Name)|alpha=$a")}
  }
  if($raw.Contains('CompositionTarget.Rendering')){$errors.Add("PER_FRAME_RENDER_LOOP|$($file.Name)")}
  foreach($m in [regex]::Matches($raw,'DispatcherTimer\s*\{\s*Interval\s*=\s*TimeSpan\.FromMilliseconds\((\d+)\)')){
    $ms=[int]$m.Groups[1].Value
    if($ms -lt 50){$errors.Add("HOT_UI_TIMER|$($file.Name)|$($ms)ms")}
  }
}

$installer=Join-Path $root 'installer\ModernInstaller.xaml'
if(Test-Path $installer){
  $x=[IO.File]::ReadAllText($installer)
  foreach($m in [regex]::Matches($x,'FontSize="(\d+(?:\.\d+)?)"')){
    $v=[double]::Parse($m.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)
    if($v -lt 10.5){$errors.Add("INSTALLER_FONT_TOO_SMALL|$v")}
  }
}

$glass=[IO.File]::ReadAllText((Join-Path $src 'MainWindow.Glass21.cs'))
$main=Get-SpankyMainWindowSource $root
$shell=Get-SpankySourceText (Join-Path $src 'MainWindow.Shell.cs')
$neutral=[IO.File]::ReadAllText((Join-Path $src 'MainWindow.NeutralUi.cs'))
$dialogs=Get-SpankySourceText (Join-Path $src 'MainWindow.StyledDialogs.cs')
$motion=[IO.File]::ReadAllText((Join-Path $src 'MainWindow.Motion.cs'))
$popup=[IO.File]::ReadAllText((Join-Path $src 'DesktopUpdateNotificationWindow.cs')) + [IO.File]::ReadAllText((Join-Path $src 'DesktopPopupWindowFrame.cs'))

foreach($token in @(
  'RootGrid.Background = glass ? ThemeColor(0,0,0,0)',
  '_pageScroll.Background = ThemeColor(0,0,0,0)',
  'PageHost.Background = ThemeColor(0,0,0,0)',
  'configuration.IsInputActive = true'
)){
  if(-not $glass.Contains($token)){$errors.Add("GLASS_TOKEN_MISSING|$token")}
}
if(-not $main.Contains('tb.InactiveBackgroundColor = transparent;') -or -not $main.Contains('tb.ButtonInactiveBackgroundColor = transparent;')){
  $errors.Add('TITLEBAR_INACTIVE_NOT_TRANSPARENT')
}
if(-not $neutral.Contains('new(244,246,250), new(196,202,212)')){$errors.Add('DARK_TEXT_CONTRAST_REGRESSED')}
if(-not $dialogs.Contains('ThemeColor(196,202,212)')){$errors.Add('DIALOG_SECONDARY_CONTRAST_REGRESSED')}
$dialogMaterial=[regex]::Match($dialogs,'(?s)private static Brush DialogModalCardBrush\(\)\s*=>\s*_darkTheme\s*\?\s*\(_dialogModalCardBrush\s*\?\?=\s*new AcrylicBrush\s*\{(?<settings>.*?)\}\)')
if(-not $dialogMaterial.Success){
  $errors.Add('DIALOG_ACRYLIC_SURFACE_MISSING')
} else {
  $settings=$dialogMaterial.Groups['settings'].Value
  if($settings -notmatch 'TintOpacity\s*=\s*0\s*,' -or $settings -notmatch 'TintLuminosityOpacity\s*=\s*0\s*,'){
    $errors.Add('DIALOG_DARK_TINT_RETURNED')
  }
  $fallback=[regex]::Match($settings,'FallbackColor\s*=\s*Windows\.UI\.Color\.FromArgb\((\d+)')
  if(-not $fallback.Success -or [int]$fallback.Groups[1].Value -gt 112){$errors.Add('DIALOG_OPAQUE_FALLBACK_RETURNED')}
}
if(-not $shell.Contains('apply.Foreground = _darkTheme ? ThemeColor(246,250,255)')){$errors.Add('PRIMARY_ACTION_CONTRAST_REGRESSED')}
if($motion.Contains('PointerEntered +=') -or $motion.Contains('PointerExited +=') -or $motion.Contains('PointerPressed +=') -or $motion.Contains('PointerReleased +=')){
  $errors.Add('PERSISTENT_POINTER_MOTION_HANDLER_RETURNED')
}
if(-not $motion.Contains('cardVisual.Scale = new Vector3(0.985f, 0.985f, 1)')){$errors.Add('DIALOG_MOTION_SCALE_REGRESSED')}
if(-not $popup.Contains('WsExNoActivate') -or -not $popup.Contains('appWindow.Show(false)')){$errors.Add('POPUP_NOACTIVATE_REGRESSED')}

if($errors.Count -gt 0){
  $errors | ForEach-Object { Write-Host $_ }
  throw "UI_HOLISTIC_AUDIT_FAIL count=$($errors.Count)"
}
Write-Host "UI_HOLISTIC_AUDIT_OK files=$($uiFiles.Count) fontRefs=$fontCount minFont=10.5 acrylicMaxAlpha=112 perFrame=false"
