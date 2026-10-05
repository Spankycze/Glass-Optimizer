param(
  [string]$Text='',
  [string]$LogPath=''
)
$ErrorActionPreference='Stop'
if(-not [string]::IsNullOrWhiteSpace($LogPath) -and (Test-Path $LogPath)){
  $Text=(Get-Content $LogPath -Raw -Encoding UTF8)
}
if([string]::IsNullOrWhiteSpace($Text)){throw 'Use -Text or -LogPath'}
$t=$Text.ToLowerInvariant()
$scores=[ordered]@{
  our_code=0
  dotnet_runtime=0
  winui_windowsappsdk=0
  driver_windows=0
}
$evidence=New-Object System.Collections.Generic.List[string]
function Hit([string]$bucket,[int]$score,[string]$reason){
  $scores[$bucket]+=$score
  $evidence.Add($bucket+': '+$reason)|Out-Null
}
foreach($pair in @(
  @('our_code','spankyhooptimizer',3,'stack/log references optimizer code'),
  @('our_code','processrunner',3,'ProcessRunner path'),
  @('our_code','operationcoordinator',3,'OperationCoordinator path'),
  @('our_code','mainwindow',2,'MainWindow/UI code path'),
  @('dotnet_runtime','system.private.corelib',3,'.NET runtime/corelib stack'),
  @('dotnet_runtime','clr!',3,'CLR native frame'),
  @('dotnet_runtime','coreclr',3,'CoreCLR frame'),
  @('dotnet_runtime','gc heap',2,'GC/heap runtime evidence'),
  @('winui_windowsappsdk','microsoft.ui.xaml',4,'WinUI/XAML frame'),
  @('winui_windowsappsdk','microsoft.windowsappruntime',4,'Windows App SDK frame'),
  @('winui_windowsappsdk','xaml',1,'XAML/rendering clue'),
  @('winui_windowsappsdk','dispatcherqueue',2,'DispatcherQueue clue'),
  @('driver_windows','dwm.exe',3,'DWM/rendering system process'),
  @('driver_windows','dxgi',3,'DXGI frame'),
  @('driver_windows','d3d11',3,'Direct3D frame'),
  @('driver_windows','nvlddmkm',5,'NVIDIA kernel driver'),
  @('driver_windows','tcpip.sys',4,'Windows networking stack'),
  @('driver_windows','ndis.sys',4,'NDIS stack')
)){
  if($t.Contains([string]$pair[1])){Hit ([string]$pair[0]) ([int]$pair[2]) ([string]$pair[3])}
}
$winner=($scores.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 1)
$confidence=if($winner.Value -ge 6){'high'}elseif($winner.Value -ge 3){'medium'}else{'low'}
$result=[ordered]@{
  classification=$winner.Key
  confidence=$confidence
  scores=$scores
  evidence=@($evidence)
}
$result|ConvertTo-Json -Depth 5
