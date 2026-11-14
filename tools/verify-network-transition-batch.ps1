param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent),[switch]$SkipRuntime)
$ErrorActionPreference='Stop'
$service = Get-Content (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\NetworkTransitionStateService.cs') -Raw
$repair = Get-Content (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\SystemRepairDiagnosticsService.cs') -Raw
. (Join-Path $ProjectRoot 'tools\source-set.ps1')
$app = Get-SpankyAppSource $ProjectRoot
foreach($str in @(
    'network-transition-v2',
    'RunBatchedNetshAsync',
    'ReadOnlyCommands',
    'int teredo show state',
    'int ipv6 isatap show state',
    'int ipv6 6to4 show state',
    'int ipv6 show privacy',
    'int ipv6 show global',
    'System.Text.Encoding.ASCII',
    'Directory.Delete(directory, recursive: true)',
    'state.Combined.Success',
    'RunBatchParitySmokeAsync',
    'referenceWatch.ElapsedMilliseconds'
)){if(!$service.Contains($str)){throw "Network transition batch regression: missing $str"}}
if($service.Contains('var teredoTask = ProcessRunner.RunAsync(') -or
    $service.Contains('var globalTask = ProcessRunner.RunAsync(')){
    throw 'Old network transition five-process fan-out returned'
}
if(!$repair.Contains('transition.Combined.StdOut') -or
    !$repair.Contains('transitionUnknown = true') -or
    !$repair.Contains('string status = healthy ? "Clean" : issues.Count > 0 ? "Needs repair" : "Unknown"')){
    throw 'Repair diagnosis must distinguish unknown from confirmed drift'
}
if(!$app.Contains('--network-transition-batch-smoke')){
    throw 'Read-only parity smoke CLI missing'
}
Write-Output 'NETWORK_TRANSITION_BATCH_STATIC_GUARD_OK netshProcesses=1 readOnly=5 localeNative=true unknownStatus=true'
if(!$SkipRuntime){
    $e=Join-Path $ProjectRoot 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
    if(!(Test-Path $e)){throw 'Compiled EXE missing for batch parity'}
    $p=Start-Process -FilePath $e -ArgumentList '--network-transition-batch-smoke' -Wait -PassThru
    if($p.ExitCode -ne 0){throw "Network batch parity exit $($p.ExitCode)"}
    Write-Output 'NETWORK_TRANSITION_BATCH_RUNTIME_GUARD_OK runs=3 networkWrites=0'
}
