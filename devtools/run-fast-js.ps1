[CmdletBinding()]
param([ValidateSet('Versions','Smoke','Oxlint','Biome')][string]$Mode = 'Versions')
$projectParent = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$toolchain = Join-Path $projectParent '.tools\js-performance-suite\run-toolchain.ps1'
if (-not (Test-Path -LiteralPath $toolchain)) { throw "Shared development toolchain not found: $toolchain" }
& $toolchain -Mode $Mode -Project Optimizer
if (-not $?) { exit 1 }
