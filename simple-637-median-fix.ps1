$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$f=Join-Path $PWD 'simple-637-final.ps1'
$t=[IO.File]::ReadAllText($f).Replace('$bucket[$key]=@($bucket[$key])+$value','if($bucket.ContainsKey($key)){$bucket[$key]=@($bucket[$key])+$value}else{$bucket[$key]=@($value)}')
[IO.File]::WriteAllText($f,$t)
& .\simple-637-final.ps1
