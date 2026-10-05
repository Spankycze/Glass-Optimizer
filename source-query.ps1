param(
  [Parameter(Mandatory=$true)][string]$Symbol,
  [int]$Max=80
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$rg=(Get-Command rg -ErrorAction Stop).Source
& $rg --line-number --no-heading --color never --glob '!**/bin/**' --glob '!**/obj/**' --glob '*.{cs,xaml,ps1}' --fixed-strings --max-count $Max -- $Symbol (Join-Path $root 'src')
