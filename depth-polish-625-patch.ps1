$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\depth-polish-625'
$changes=[IO.File]::ReadAllText((Join-Path $dir 'changes.json'))|ConvertFrom-Json
foreach($group in ($changes|Group-Object file)){
 $path=Join-Path $PWD $group.Name
 Copy-Item $path (Join-Path (Join-Path $dir 'before') ($group.Name.Replace('\','_'))) -Force
 $s=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
 foreach($c in $group.Group){
  $a=$c.old.Replace("`r`n","`n")
  if(!$s.Contains($a)){throw ('Missing patch anchor: '+$group.Name+' '+$a)}
  $s=$s.Replace($a,$c.next.Replace("`r`n","`n"))
 }
 [IO.File]::WriteAllText($path,$s,(New-Object Text.UTF8Encoding($false)))
}
Write-Output 'DEPTH625_PATCH_OK'
& .\build-fast.ps1 *> (Join-Path $dir 'build-fast.log')
if(!$?){throw 'Build failed'}
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dir 'build-test.log')
if(!$?){throw 'Tests failed'}
Write-Output 'DEPTH625_BUILD_TEST_OK'
