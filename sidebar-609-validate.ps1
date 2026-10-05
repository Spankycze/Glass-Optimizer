$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& '.\dc-smart.ps1' '.\build-fast.ps1'
if($LASTEXITCODE -ne 0){throw 'Build failed'}
& '.\dc-smart.ps1' '.\build-test.ps1 -NoBuild -ForceTests'
if($LASTEXITCODE -ne 0){throw 'Base tests failed'}
$exe=& '.\resolve-debug-exe.ps1'
foreach($arg in '--release-ui-smoke','--navigation-race-smoke','--performance-smoke'){
 $p=Start-Process $exe -ArgumentList $arg -WorkingDirectory (Split-Path $exe) -PassThru
 if(-not $p.WaitForExit(120000)){try{$p.Kill()}catch{};throw "$arg timeout"}
 Write-Output ("SIDEBAR609_TEST arg=$arg exit="+$p.ExitCode)
 if($p.ExitCode -ne 0){throw "$arg failed"}
}
$log=@(Get-Content 'C:\ProgramData\SpankyhoOptimizer\performance.log' -Tail 1600)
New-Item -ItemType Directory -Force 'artifacts\sidebar-609'|Out-Null
$log|Where-Object {$_ -match 'SIDEBAR_SCROLL_|PERF_COLD'} |
 Set-Content 'artifacts\sidebar-609\validation-phases.log' -Encoding UTF8
& '.\dc-smart.ps1' '.\ui-redesign-audit.ps1'
if($LASTEXITCODE -ne 0){throw 'Protected UI audit failed'}
Write-Output 'SIDEBAR609_VALIDATION_OK'





