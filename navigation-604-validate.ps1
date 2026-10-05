$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& '.\dc-smart.ps1' '.\build-test.ps1 -NoBuild -ForceTests'
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
$exe=& '.\resolve-debug-exe.ps1'
foreach($arg in '--release-ui-smoke','--navigation-race-smoke','--page-scroll-input-smoke'){
  $p=Start-Process $exe -ArgumentList $arg -WorkingDirectory (Split-Path $exe) -PassThru
  if(-not $p.WaitForExit(120000)){try{$p.Kill()}catch{};throw "$arg timeout"}
  Write-Output ("NAV604_VALIDATION arg=$arg exit="+$p.ExitCode)
  if($p.ExitCode -ne 0){exit $p.ExitCode}
}
