param(
    [Parameter(Mandatory = $true)][string]$PreviousLog,
    [Parameter(Mandatory = $true)][string]$CurrentLog,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture

function Read-ReleaseMetrics([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release log not found: $path"
    }
    $raw = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $path))
    $release = [regex]::Match($raw, '(?m)^RELEASE_OK total=([0-9]+(?:[.,][0-9]+)?)s')
    $version = [regex]::Match($raw, '(?m)^LOCAL_UPDATE_BRIDGE_OK version=(\d+\.\d+\.\d+)')
    if (-not $release.Success -or -not $version.Success) {
        throw "Incomplete/unverified release log: $path"
    }

    $phases = @{}
    foreach ($match in [regex]::Matches($raw, '(?m)^RELEASE_PHASE\s+([^=\r\n]+)=([0-9]+)ms')) {
        $phases[$match.Groups[1].Value] = [long]::Parse($match.Groups[2].Value, $culture)
    }
    if ($phases.Count -eq 0) {
        throw "No timing phases in $path"
    }
    $durationSeconds = [double]::Parse($release.Groups[1].Value.Replace(',', '.'), $culture)
    return [pscustomobject]@{
        Version = $version.Groups[1].Value
        TotalMs = [long][Math]::Round($durationSeconds * 1000)
        Phases = $phases
    }
}

$previous = Read-ReleaseMetrics $PreviousLog
$current = Read-ReleaseMetrics $CurrentLog
$results = @(
    foreach ($phase in ($previous.Phases.Keys | Where-Object { $current.Phases.ContainsKey($_) } | Sort-Object)) {
        $old = $previous.Phases[$phase]
        $new = $current.Phases[$phase]
        $delta = $new - $old
        [pscustomobject]@{
            Phase = $phase
            PreviousMs = $old
            CurrentMs = $new
            DeltaMs = $delta
            DeltaPct = if ($old -gt 0) { [Math]::Round(100 * $delta / $old, 2) } else { $null }
            Trend = if ($delta -lt 0) { 'zlepseni' } elseif ($delta -gt 0) { 'zhorseni' } else { 'beze zmeny' }
        }
    }
)
if ($results.Count -eq 0) {
    throw 'No comparable timing phases found'
}

$totalDelta = $current.TotalMs - $previous.TotalMs
$totalPct = [Math]::Round(100 * $totalDelta / $previous.TotalMs, 2)
$rows = @(
    "# Release timings: $($previous.Version) -> $($current.Version)"
    ''
    "**Total build/release:** $($previous.TotalMs) ms -> $($current.TotalMs) ms; delta $totalDelta ms ($totalPct%)."
    ''
    '| Phase | Previous (ms) | Current (ms) | Delta (ms) | Trend |'
    '|---|---:|---:|---:|---|'
)
foreach ($r in $results) {
    $rows += "| $($r.Phase) | $($r.PreviousMs) | $($r.CurrentMs) | $($r.DeltaMs) | $($r.Trend) |"
}
$rows += ''
$rows += 'Times are build/test/release pipeline measurements, not application startup, navigation, Apply, CPU, or RAM runtime benchmarks.'
$rows += 'Small deltas can be normal measurement noise; repeat runtime benchmarks under matched conditions before claiming an application performance change.'

$report = $rows -join [Environment]::NewLine
if ($OutputPath) {
    $target = [System.IO.Path]::GetFullPath($OutputPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
    [System.IO.File]::WriteAllText($target, $report, [System.Text.UTF8Encoding]::new($false))
    Write-Output "PERFORMANCE_REPORT_SAVED $target"
}
Write-Output "PERF_TOTAL previous=$($previous.TotalMs)ms current=$($current.TotalMs)ms delta=$($totalDelta)ms pct=$totalPct"
$results | ForEach-Object {
    Write-Output "PERF_DIFF phase=$($_.Phase) previous=$($_.PreviousMs)ms current=$($_.CurrentMs)ms delta=$($_.DeltaMs)ms trend=$($_.Trend)"
}
