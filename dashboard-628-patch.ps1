$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\dashboard-628'
$changes=[IO.File]::ReadAllText((Join-Path $dir 'changes.json'))|ConvertFrom-Json
foreach($group in ($changes|Group-Object file)){
 $path=Join-Path $PWD $group.Name
 Copy-Item $path (Join-Path (Join-Path $dir 'before') ($group.Name.Replace('\','_'))) -Force
 $s=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
 foreach($c in $group.Group){
  $a=$c.old.Replace("`r`n","`n")
  if(!$s.Contains($a)){throw ('Missing anchor: '+$group.Name+' '+$a)}
  $s=$s.Replace($a,$c.next.Replace("`r`n","`n"))
 }
 [IO.File]::WriteAllText($path,$s,(New-Object Text.UTF8Encoding($false)))
}
Write-Output 'DASHBOARD628_PATCH_OK'
