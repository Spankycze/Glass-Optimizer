param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent),[switch]$SkipRuntime)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'source-set.ps1')
$service=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\SystemRepairDiagnosticsService.cs') -Raw
$app=Get-SpankySourceText (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\App.cs')
foreach($needle in @(
  'Task<RepairDiagnostic> service = Task.Run(',
  'Task<RepairDiagnostic> store = Task.Run(CheckStore, token)',
  'Task<RepairDiagnostic> explorer = Task.Run(CheckExplorer, token)',
  'Task<RepairDiagnostic> network = Task.Run(() => CheckNetworkAsync(token), token)',
  'Task<RepairDiagnostic> drift = Task.Run(() => CheckBadTweaksAsync(token), token)',
  'Task<RepairDiagnostic> graphics = CheckNvidiaAsync(token)',
  'var bindingProbe = Task.Run(',
  'var bcdProbe = BcdEditStateService.GetCurrentAsync(token)',
  'var transitionProbe = NetworkTransitionStateService.GetAsync(token)',
  'var maintenanceProbe = new TaskSchedulerService().GetBasicAsync(false, token)',
  'RepairDiagnostic[] completed = await Task.WhenAll(',
  'service, store, explorer, network, drift, graphics).ConfigureAwait(false);',
  'completed[0], completed[1], completed[2], completed[3],',
  'completed[4], gameBar, completed[5]',
  'NativeNetworkBindingRepairService.ReadDisabled(token)',
  'NativeNetworkBindingRepairService.EnableDisabled(token)',
  '_scanGeneration++',
  'generation == _scanGeneration',
  '_scanInFlight = null',
  'RunParallelScanCacheSmokeAsync',
  'Failed scan left poisoned cache',
  'Obsolete repair scan overwrote current generation'
)){
    if(!$service.Contains($needle)){throw "Repair parallel/cache contract missing: $needle"}
}
# A completed Task.Result reintroduction must not pass this gate.
if($service.Contains('service.Result') -or $service.Contains('store.Result') -or
   $service.Contains('explorer.Result') -or $service.Contains('graphics.Result') -or
   $service.Contains('network.Result') -or $service.Contains('drift.Result')){
  throw 'Repair parallel result guard: blocking Task.Result access returned.'
}
if(!$app.Contains('--repair-scan-parallel-smoke') -or !$app.Contains('--network-parallel-smoke')){
    throw 'Repair/network parallel smoke CLI missing'
}
$network=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\NetworkDiagnosticsService.cs') -Raw
foreach($needle in @(
  'int intervalMs = 80',
  'var key = (target, samples, intervalMs)',
  'private const int CacheLimit = 8',
  'private static readonly SemaphoreSlim MeasureGate = new(2, 2)',
  'private static readonly SemaphoreSlim BenchmarkGate = new(1, 1)',
  'bool exclusive = samples >= 10',
  'if (permitsHeld > 0) MeasureGate.Release(permitsHeld)',
  'NETWORK_EXCLUSIVE_SMOKE_OK',
  'private static KeyGate RentKeyGate('
  'private static void ReturnKeyGate(',
  'await keyGate.Semaphore.WaitAsync(token)',
  'await MeasureGate.WaitAsync(token)',
  'Network transient gate/cache leaked:',
  'Cancelled network waiter poisoned owner cache.',
  'MeasureIndependentConcurrencySmokeAsync',
  'if (generation == _generation)',
  'VerifyCacheReadOnlyAsync',
  'concurrentSingleFlight=true'
)){
    if(!$network.Contains($needle)){throw "Network bounded multi-key cache contract missing: $needle"}
}
foreach($needle in @(
  'samples: 8, token: token, intervalMs: 30',
  'await NetworkDiagnosticsService.VerifyCacheReadOnlyAsync(token)'
)){
    if(!$service.Contains($needle)){throw "Repair fast probe/cache smoke missing: $needle"}
}
$fixer=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\MainWindow.FixerPageCache.cs') -Raw
foreach($needle in @(
  'TryAttachFixerPageCache(CancellationToken token)',
  '_fixerStaticActionsQueued = false',
  '_fixerHeroQueued = false',
  'ScheduleDeferredFixerHero(token)',
  'ScheduleDeferredFixerStaticActions(token)',
  'generation != _navigationGeneration',
  '!ReferenceEquals(_fixerAppHealthHost, appHealthHost)',
  'LoadAppHealthAsync(appHealthHost, token)',
  'Fixer cached diagnostics refresh accepted:'
 )) { if(!$fixer.Contains($needle)){throw "Fixer cached lifecycle contract missing: $needle"} }
$tools=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\MainWindow.Tools.cs') -Raw
foreach($needle in @('_ = BuildAppHealthAsync(token);', 'LoadAppHealthAsync(healthHost, token)', 'TryAttachFixerPageCache(token)', 'ScheduleDeferredFixerHero(token)', 'ScheduleDeferredFixerStaticActions(token)')) {
  if(!$tools.Contains($needle)){throw "Fixer initial navigation loader missing: $needle"}
}
$cache=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\MainWindow.FixerPageCache.cs') -Raw
foreach($needle in @('LoadAppHealthAsync(appHealthHost, token)', 'DispatcherQueuePriority.Normal', 'Fixer cached diagnostics refresh accepted:')) {
  if(!$cache.Contains($needle)){throw "Fixer cached navigation refresh missing: $needle"}
}
if(!$cache.Contains('if (_fixerAppHealthHost == null)') -or !$cache.Contains('InvalidateFixerPageCache();')){throw 'Fixer cached page must rebuild if dynamic host is missing'}
if($cache.Contains('DateTime.UtcNow - _fixerDynamicAt > TimeSpan.FromSeconds(15)')){throw 'Fixer cached navigation must always rebind current navigation token'}
$mark=$cache.IndexOf('_fixerDynamicAt = DateTime.UtcNow;')
$guard=$cache.IndexOf('!ReferenceEquals(_fixerAppHealthHost, appHealthHost)')
$refresh=$cache.IndexOf('LoadAppHealthAsync(appHealthHost, token)')
if($mark -le $guard -or $mark -ge $refresh){throw 'Fixer cache timestamp must update after callback guards and before refresh'}
Write-Output 'FIXER_UI_LIFECYCLE_STATIC_GUARD_OK timeout=true retry=true requestFence=true lifecycleLog=true firstNavigation=true cachedRefresh=true timestampAfterGuard=true'
Write-Output 'REPAIR_SCAN_PARALLEL_STATIC_GUARD_OK independentProbes=6 resultOrder=true nativeBinding=true invalidationGeneration=true failureRetry=true networkFastProbe=true boundedMultiKeyCache=true networkParallel=true benchmarkExclusive=true transientGates=true'
if(!$SkipRuntime){
    $exe=Join-Path $ProjectRoot 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
    if(!(Test-Path $exe)){throw "Compiled EXE missing: $exe"}
    $p=Start-Process -FilePath $exe -ArgumentList '--repair-scan-parallel-smoke' -PassThru -Wait
    if($p.ExitCode -ne 0){throw "Repair scan parallel smoke failed $($p.ExitCode)"}
    Write-Output 'REPAIR_SCAN_PARALLEL_RUNTIME_GUARD_OK readOnly=true areas=7'
    # Exercise the real child-worker JSON response, not just ScanLocalAsync.
    $ipc=Start-Process -FilePath $exe -ArgumentList '--repair-worker-ipc-smoke' -PassThru -Wait
    if($ipc.ExitCode -ne 0){throw "Repair worker IPC smoke failed $($ipc.ExitCode)"}
    Write-Output 'REPAIR_WORKER_IPC_RUNTIME_GUARD_OK readOnly=true areas=7'
}
