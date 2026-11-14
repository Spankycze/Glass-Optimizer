param(
    [string]$BenchmarkRoot = 'V:\SpankyDevCache\OptimizerRuntimeBenchmark',
    [ValidateRange(4,100)][int]$SamplesPerVariant = 20
)
$ErrorActionPreference = 'Stop'
$executables = @{
    IL = Join-Path $BenchmarkRoot 'IL\SpankyhoOptimizer.RuntimeHost.exe'
    R2R = Join-Path $BenchmarkRoot 'R2R\SpankyhoOptimizer.RuntimeHost.exe'
}
foreach ($path in $executables.Values) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Cannot benchmark missing isolated RuntimeHost executable: $path"
    }
}
function Invoke-StartupProbe([string]$variant) {
    $exe = $executables[$variant]
    $start = [Diagnostics.ProcessStartInfo]::new($exe, '--startup-probe')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path -Parent $exe
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(10000)) {
            $process.Kill($true)
            throw "$variant startup-probe timeout"
        }
        $clock.Stop()
        if ($process.ExitCode -ne 0) {
            throw "$variant startup-probe failed exit code $($process.ExitCode)"
        }
        [pscustomobject]@{
            variant = $variant
            milliseconds = [math]::Round($clock.Elapsed.TotalMilliseconds, 3)
            peakWorkingSetBytes = $process.PeakWorkingSet64
            cpuMilliseconds = [math]::Round($process.TotalProcessorTime.TotalMilliseconds, 3)
        }
    } finally {
        $process.Dispose()
    }
}
# Warm up both isolated builds; do not start, stop or touch the installed FanAgent.
foreach ($v in @('IL','R2R')) {
    for ($i=0; $i -lt 3; $i++) { $null = Invoke-StartupProbe $v }
}
$measurements = [Collections.Generic.List[object]]::new()
for ($i=0; $i -lt $SamplesPerVariant; $i++) {
    # Reverse order every iteration to reduce OS cache/order bias.
    $order = if ($i % 2 -eq 0) { @('IL','R2R') } else { @('R2R','IL') }
    foreach ($v in $order) { $measurements.Add((Invoke-StartupProbe $v)) }
}
$summary = foreach ($v in @('IL','R2R')) {
    $times = @($measurements | Where-Object variant -eq $v |
        Select-Object -ExpandProperty milliseconds | Sort-Object)
    $count = $times.Count
    $median = if ($count % 2 -eq 0) {
        ($times[($count/2)-1] + $times[$count/2]) / 2
    } else { $times[[int][math]::Floor($count/2)] }
    [pscustomobject]@{
        variant = $v
        samples = $count
        medianMs = [math]::Round($median, 2)
        p90Ms = $times[[int][math]::Ceiling(0.90*$count)-1]
        minMs = $times[0]
        maxMs = $times[$count-1]
        payloadMB = [math]::Round(
            ((Get-ChildItem (Split-Path -Parent $executables[$v]) -File -Recurse |
            Measure-Object Length -Sum).Sum / 1MB),2)
    }
}
$report = [pscustomobject]@{
    timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
    probe = 'read-only --startup-probe; no fan/HID/settings access'
    summary = @($summary)
    measurements = @($measurements)
}
$reportPath = Join-Path $BenchmarkRoot 'startup-comparison.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
$summary | Format-Table -AutoSize
"BENCHMARK_REPORT=$reportPath"
