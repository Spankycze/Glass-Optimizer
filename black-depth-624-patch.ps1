$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\black-depth-624'
$changes=[IO.File]::ReadAllText((Join-Path $dir 'remaining.json'))|ConvertFrom-Json
foreach($group in ($changes|Group-Object file)){
 $path=Join-Path $PWD $group.Name
 $backup=Join-Path (Join-Path $dir 'before') ($group.Name.Replace('\','_'))
 Copy-Item $path $backup -Force
 $s=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
 foreach($c in $group.Group){
  $a=$c.old.Replace("`r`n","`n")
  if(!$s.Contains($a)){throw ('Patch anchor missing: '+$group.Name+' '+$a)}
  $s=$s.Replace($a,$c.next.Replace("`r`n","`n"))
 }
 [IO.File]::WriteAllText($path,$s,(New-Object Text.UTF8Encoding($false)))
}
Write-Output 'BLACK_COLOR_PATCH_OK'
& .\build-fast.ps1 *> (Join-Path $dir 'build-fast.log')
if(!$?){throw 'Build failed'}
$exe=& .\resolve-debug-exe.ps1
Write-Output ('FRESH_EXE='+$exe)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dir 'build-test.log')
if(!$?){throw 'Tests failed'}
Write-Output 'BLACK_COLOR_BUILD_TEST_OK'
