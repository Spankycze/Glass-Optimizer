param(
  [Parameter(Mandatory=$true,Position=0)][string]$Package,
  [switch]$Upgrade
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
if($env:SPANKY_UV){
  $args=@('tool','install',$Package)
  if($Upgrade){$args=@('tool','upgrade',$Package)}
  & $env:SPANKY_UV @args
  exit $LASTEXITCODE
}
if($Upgrade){
  & py -m pip install --user --upgrade $Package
}else{
  & py -m pip install --user $Package
}
exit $LASTEXITCODE
