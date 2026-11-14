param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent),[switch]$SkipRuntime)
$ErrorActionPreference='Stop'
$repair=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\SystemRepairDiagnosticsService.cs') -Raw

. (Join-Path $ProjectRoot 'tools\source-set.ps1')
$modern=Get-SpankyWindowsModernizerSource $ProjectRoot
$app=Get-SpankyAppSource $ProjectRoot
if($repair -match 'ProcessRunner\.RunAsync\("sc\.exe"' -or $modern -match 'RunAsync\("sc\.exe"'){
    throw 'Legacy sc.exe service subprocess remains in repair/modernizer source.'
}
foreach($required in @(
    'NativeCore.ChangeServiceStartType(name, startMode',
    'ServiceSafetyService.ReadStartType(name)',
    'new ServiceController("MMCSS")',
    'VerifyNativeServiceRepairReadOnly()'
)){
    if(!$repair.Contains($required)){throw "Native repair service contract missing: $required"}
}
foreach($required in @(
    'NativeCore.ChangeServiceStartType(service, target',
    'QueryServiceStartMode(service)',
    'TryStopVendorServiceNativeAsync("ArmouryCrateDownloadTool"',
    'SetServiceStartModeNativeAsync("lltdio"',
    'SetServiceStartModeNativeAsync("ArmouryCrateDownloadTool"'
)){
    if(!$modern.Contains($required)){throw "Native Windows Modernizer service contract missing: $required"}
}
if(!$app.Contains('--native-service-repair-smoke')){throw 'Native service read-only smoke missing'}
Write-Output 'NATIVE_SERVICE_STATIC_GUARD_OK configNative=true repairReadback=true modernizerReadback=true vendorStopNative=true'
if(!$SkipRuntime){
    $exe=Join-Path $ProjectRoot 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
    if(!(Test-Path $exe)){throw 'Compiled app not found'}
    $p=Start-Process -FilePath $exe -ArgumentList '--native-service-repair-smoke' -PassThru -Wait
    if($p.ExitCode -ne 0){throw "Native service read-only smoke exit=$($p.ExitCode)"}
    Write-Output 'NATIVE_SERVICE_RUNTIME_GUARD_OK writes=false'
}
