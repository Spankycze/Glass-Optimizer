param(
  [Parameter(Mandatory=$true)][string]$InputPath,
  [Parameter(Mandatory=$true)][ValidateSet('dns','json')][string]$Mode
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if(-not(Test-Path $InputPath -PathType Leaf)){throw ('Input missing: '+$InputPath)}
$destDir=Join-Path $root ('fuzz\regressions\'+$Mode)
New-Item -ItemType Directory -Force -Path $destDir|Out-Null
$hash=(Get-FileHash -Algorithm SHA256 -LiteralPath $InputPath).Hash.ToLowerInvariant()
$ext=if($Mode -eq 'json'){'.json'}else{'.txt'}
$dest=Join-Path $destDir ('reg-'+$hash.Substring(0,16)+$ext)
if(-not(Test-Path $dest)){
  Copy-Item -LiteralPath $InputPath -Destination $dest
  Write-Output ('FUZZ_REGRESSION_PROMOTED mode='+$Mode+' file='+$dest)
}else{
  Write-Output ('FUZZ_REGRESSION_EXISTS mode='+$Mode+' file='+$dest)
}
$store=Join-Path $root 'regression-store.ps1'
if(Test-Path $store){
  & $store -Action add -Kind ('fuzz-'+$Mode) -Key ('fuzz-'+$Mode+'-'+$hash.Substring(0,16)) -Message ('Promoted fuzz regression '+$hash) -Artifact $dest | Out-Null
}
