$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'tools\source-set.ps1')
$context=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\ContextMenuService.cs') -Raw
$ui=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.ContextMenu.cs') -Raw
$modernizer=Get-SpankyWindowsModernizerSource $root

$required=@(
  'ExtendedSubCommandsKey", "Spanky.PowerTools"',
  'ExtendedSubCommandsKey", "Spanky.SystemTools"',
  'Terminál a PowerShell',
  'Systémové nástroje',
  'DeleteValue("Extended", false)',
  'PowerShell7x64',
  'LegacyDisable',
  'ProgrammaticAccessOnly'
)
foreach($token in $required){
  if(-not $context.Contains($token)){throw "CONTEXT_MENU_GUARD missing: $token"}
}
if($context -match 'SetValue\("Extended"'){throw 'CONTEXT_MENU_GUARD Shift-only Extended write detected'}
if($ui -match '(?i)shift'){throw 'CONTEXT_MENU_GUARD UI mentions Shift'}
if(-not $ui.Contains('Vyčistit a optimalizovat')){throw 'CONTEXT_MENU_GUARD cleanup action missing'}
if(-not $ui.Contains('hover submenu Terminál a PowerShell')){throw 'CONTEXT_MENU_GUARD terminal hover UI missing'}
if(-not $ui.Contains('hover submenu Systémové nástroje')){throw 'CONTEXT_MENU_GUARD system hover UI missing'}

$modernRequired=@(
  '"TaskbarAl",0',
  '"TaskbarGlomLevel",2',
  '"MMTaskbarGlomLevel",2',
  '"AppsUseLightTheme",0',
  '"SystemUsesLightTheme",0',
  '"EnableTransparency",1'
)
foreach($token in $modernRequired){
  if(-not $modernizer.Replace(' ','').Contains($token)){throw "WINDOWS_SHELL_GUARD missing: $token"}
}
if($modernizer -match 'TaskbarAcrylicOpacity'){throw 'WINDOWS_SHELL_GUARD TaskbarAcrylicOpacity must stay absent'}
Write-Host 'CONTEXT_MENU_SHELL_GUARD_OK classicFirstClick=true hoverSubmenus=true shiftOnly=false dark=true taskbarLeft=true labels=true transparency=true acrylicOverride=false'
