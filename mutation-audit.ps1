param([string]$Mutate='**/OperationCoordinator.cs',[int]$Concurrency=2)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir=Join-Path $root ('artifacts\quality\stryker-'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$strykerRoot=Join-Path $root 'tools\CoreTests'
Push-Location $strykerRoot
try{
  dotnet stryker --config-file '.\stryker-config.json' --configuration Release -m $Mutate -c $Concurrency -V warning --reporter json --reporter cleartext
  $exit=$LASTEXITCODE
  $reports=Get-ChildItem $strykerRoot -Recurse -File -Filter 'mutation-report.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if($reports){Copy-Item $reports.FullName (Join-Path $outDir 'mutation-report.json') -Force}
  Write-Output ('STRYKER_DONE exit='+$exit+' output='+$outDir)
  exit $exit
} finally {Pop-Location}
