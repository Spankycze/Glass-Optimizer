param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent),[switch]$SkipRuntime)
$ErrorActionPreference='Stop'
$service=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\ShellHealthService.cs') -Raw
. (Join-Path $ProjectRoot 'tools\source-set.ps1')
$app=Get-SpankyAppSource $ProjectRoot
if(!$service.Contains('private const string BatchedProfileProbeScript') -or
   !$service.Contains('SPANKY_PROFILE_PROBE|') -or
   !$service.Contains('TimeSpan.FromSeconds(45)') -or
   !$service.Contains('SHELL_PROFILE_BATCH_PROBE processStarts=1') -or
   $service.Contains('MeasureProbeAsync(')){
    throw 'Six isolated shell profile launches reintroduced or batch protocol broken.'
}
foreach($name in @('PSReadLine','Terminal-Icons','PSFzf','posh-git','zoxide','Oh My Posh')){
    if(!$service.Contains('"' + $name + '"')){throw "Missing profile probe $name"}
}
if(!$app.Contains('CheckIntegrationUpdatesAfterStartupAsync(_lifetimeCts.Token)') -or
   !$app.Contains('SHELL_PROFILE_COSTS_SMOKE_OK')){
    throw 'Startup update discovery or batched profile smoke entry point missing.'
}
Write-Output 'SHELL_PROFILE_BATCH_STATIC_GUARD_OK probes=6 processStarts=1 delayedGitHubCheck=true'
if(!$SkipRuntime){
    $exe=Join-Path $ProjectRoot 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
    if(!(Test-Path $exe)){throw 'Missing published EXE for shell profile smoke.'}
    $p=Start-Process -FilePath $exe -ArgumentList '--shell-profile-costs-smoke' -Wait -PassThru
    if($p.ExitCode -ne 0){throw "Batch profile smoke exit $($p.ExitCode)"}
    Write-Output 'SHELL_PROFILE_BATCH_RUNTIME_GUARD_OK'
}
