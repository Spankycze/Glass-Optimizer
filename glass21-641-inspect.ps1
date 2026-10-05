$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
rg -n 'sys.game|sys.dvr|sys.capture|system.*43|system.*Count|animation|animat' src\SpankyhoOptimizer -g '*Smoke*.cs' -g '*Catalog*.cs'
