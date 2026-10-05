$ErrorActionPreference='Stop'
$root=$PSScriptRoot
. (Join-Path $root 'build-state.ps1')
$xamlRoot=Join-Path $root 'src\SpankyhoOptimizer'
$hook=Join-Path $root 'build-fast-xaml-reuse.targets'
if(-not(Test-Path $hook)){throw 'XAML reuse hook missing'}
$unsafe=New-Object Collections.Generic.List[string]
$eventPattern='\b(Click|Loaded|Unloaded|SelectionChanged|Toggled|Checked|Unchecked|TextChanged|KeyDown|KeyUp|PointerPressed|PointerReleased|PointerMoved|Tapped|DoubleTapped|RightTapped|Holding|SizeChanged)\s*=\s*"'
foreach($f in Get-ChildItem $xamlRoot -Recurse -File -Filter '*.xaml'){
  $t=[IO.File]::ReadAllText($f.FullName)
  if($t -match '\{x:Bind\b'){$unsafe.Add($f.FullName+':xBind')}
  if($t -match $eventPattern){$unsafe.Add($f.FullName+':event-handler')}
  if($t -match 'xmlns:\w+\s*=\s*"using:SpankyhoOptimizer(?:\.|")'){$unsafe.Add($f.FullName+':custom-namespace')}
}
$stateSafe=Test-SpankyXamlReuseSafe
if($unsafe.Count -or -not $stateSafe){
  $unsafe|ForEach-Object{Write-Output ('XAML_REUSE_UNSAFE '+$_)}
  throw ('XAML_REUSE_AUDIT_FAIL unsafe='+$unsafe.Count+' stateSafe='+$stateSafe)
}
Write-Output ('XAML_REUSE_AUDIT_OK files='+(Get-ChildItem $xamlRoot -Recurse -File -Filter '*.xaml').Count+' stateSafe=true')
