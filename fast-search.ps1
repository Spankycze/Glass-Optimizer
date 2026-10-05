param(
  [Parameter(Mandatory=$true,Position=0)][string]$Pattern,
  [Parameter(Position=1)][string]$Path='.'
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$target=Resolve-Path (Join-Path $PSScriptRoot $Path)
if($env:SPANKY_RG){
  & $env:SPANKY_RG --hidden --glob '!.git/**' --glob '!bin/**' --glob '!obj/**' --glob '!node_modules/**' --glob '!artifacts/**' --glob '!.calm/**' --glob '!.smart-learning/**' --glob '!.agent-memory/**' -n --smart-case -- $Pattern $target
  exit $LASTEXITCODE
}
Get-ChildItem $target -File -Recurse | Select-String -Pattern $Pattern
