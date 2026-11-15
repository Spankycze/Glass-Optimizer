param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$SkipRuntime
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'source-set.ps1')
$source = Get-SpankySourceText (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\PowerShellCenterService.cs')
$app = Get-SpankySourceText (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\App.cs')
foreach ($rule in @(
    'StatusCacheTtl = TimeSpan.FromSeconds(45)',
    'StatusRefreshGate.WaitAsync(token)',
    'ModulesRefreshGate.WaitAsync(token)',
    'LoadManagedModulesFreshAsync(token)',
    'ModulesProcessLoadsForDiagnostics',
    'private static PowerShellCenterStatus? GetCachedStatus()',
    'InvalidateStatusCache()',
    'Queue<string>(keep)',
    'foreach (string line in File.ReadLines(path))',
    'File.Move(temporary, path, overwrite: true)',
    'current.LastWriteTimeUtc != timestamp',
    'ScheduleHistoryCleanupOnce()',
    'Interlocked.CompareExchange(ref _historyCleanupRunning, 1, 0)'
)) {
    if (-not $source.Contains($rule)) { throw "Status/history native guard failed: $rule" }
}
if ($source.Contains('string[] lines = File.ReadAllLines(path);')) {
    throw 'PowerShell history full-file allocation was reintroduced'
}
if (($source.Split('InvalidateStatusCache();').Count - 1) -lt 4) {
    throw 'PowerShell module mutation does not invalidate status cache'
}
if (-not $app.Contains('--powershell-status-cache-smoke')) {
    throw 'PowerShell status/history runtime smoke missing'
}
Write-Output 'POWERSHELL_STATUS_HISTORY_STATIC_GUARD_OK statusSingleFlight=true ttl=45s historyRing=10000 atomicWrite=true'
if (-not $SkipRuntime) {
    $exe = Join-Path $ProjectRoot 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Compiled exe missing: $exe" }
    $p = Start-Process -FilePath $exe -ArgumentList '--powershell-status-cache-smoke' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "PowerShell status cache smoke failed with exit $($p.ExitCode)" }
    Write-Output 'POWERSHELL_STATUS_HISTORY_RUNTIME_GUARD_OK'
}
