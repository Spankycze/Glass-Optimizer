$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'tools\source-set.ps1')
$shell=Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Shell.cs')
$themes=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Themes.cs') -Raw
$catalog=Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\Services\CatalogService.cs')
$wall=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\WallpaperThemeService.cs') -Raw
$context=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.ContextMenu.cs') -Raw
$main=Get-SpankyMainWindowSource $root

if(-not $shell.Contains('("Personalizace","themes")')){throw 'THEMES_HUB_GUARD Personalizace sidebar entry missing'}
if(-not $shell.Contains('("UI / Explorer","ui")')){throw 'THEMES_HUB_GUARD UI/Explorer sidebar entry missing'}
foreach($legacy in @('("PowerShell","powershell")','("Cursor","cursor")')){
  if($shell.Contains($legacy)){throw "THEMES_HUB_GUARD legacy sidebar entry returned: $legacy"}
}
if(-not $shell.Contains('"cursor" or "powershell" => "themes"')){throw 'THEMES_HUB_GUARD nested highlight mapping missing'}
if($shell.Contains('"ui" or "cursor" or "powershell" => "themes"')){throw 'THEMES_HUB_GUARD UI/Explorer is incorrectly nested under Themes'}
if(-not $catalog.Contains('new("themes", "Personalizace"')){throw 'THEMES_HUB_GUARD Personalizace catalog section missing'}
foreach($card in @('"Windows Glass"','"Kontextové menu"','"Cursor"','"PowerShell"','"Windows Neon Wave"','"Windows Purple Valley"')){
  if(-not $themes.Contains($card)){throw "THEMES_HUB_GUARD card missing: $card"}
}
if($themes.Contains('"Explorer / UI"')){throw 'THEMES_HUB_GUARD UI/Explorer must stay only in the sidebar, not as a Themes card'}
if($themes.Contains('"Nastavení aplikace"')){throw 'THEMES_HUB_GUARD Settings must stay only in the sidebar, not as a Themes card'}
foreach($wallToken in @('NeonWave','PurpleValley','OriginalWallpaper','Windows-Neon-Wave.jpg','Windows-Purple-Valley.jpg','NeonWaveSource','PurpleValleySource','BackgroundType','BackgroundHistoryPath0')){
  if(-not $wall.Contains($wallToken)){throw "THEMES_HUB_GUARD wallpaper token missing: $wallToken"}
}
if(-not $wall.Contains('string.Equals(current, NeonWaveSource') -or
   -not $wall.Contains('string.Equals(current, PurpleValleySource')){
  throw 'THEMES_HUB_GUARD uploaded wallpaper source detection missing'
}
foreach($removedWallpaper in @('Forest Green','Spanky Ice Blue','ForestGreen','IceBlue','Spanky-Forest-Green.jpg','Spanky-Ice-Blue.jpg')){
  if($themes.Contains($removedWallpaper) -or $wall.Contains($removedWallpaper)){
    throw "THEMES_HUB_GUARD removed wallpaper returned: $removedWallpaper"
  }
}
if($themes.Contains('LinearGradientBrush')){throw 'THEMES_HUB_GUARD wallpaper previews regressed to generated color gradients'}
if($context.Contains('Windhawk') -or $context.Contains('Glass kontextové menu')){throw 'THEMES_HUB_GUARD Windhawk context menu feature returned'}
if($main.Contains('BuildWindowsModernizerUiPanel(token);')){throw 'THEMES_HUB_GUARD Windows Modernizer panel returned to UI/Explorer'}
if($main.Contains('BuildWinhanceUiActions();')){throw 'THEMES_HUB_GUARD Winhance extra panel returned to UI/Explorer'}
Write-Host 'THEMES_HUB_GUARD_OK sidebar=themes+ui wallpapers=real-images legacyExtras=false nestedHighlight=cursor,powershell windhawk=false'
