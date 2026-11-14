param([string]$ExpectedVersion = '15.4.1473', [int]$Samples = 3, [int]$IntervalSeconds = 5, [string]$FixtureRoot = '')
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'SpankyhoOptimizer\FanAgent'
$healthPath = Join-Path $root 'fan-agent-health.json'
$heartbeatPath = Join-Path $root 'fan-agent-heartbeat.txt'
$profilePath = Join-Path $env:LOCALAPPDATA 'SpankyhoOptimizer\fan-profile.json'
if ($FixtureRoot) {
  $root = $FixtureRoot
  $healthPath = Join-Path $root 'fan-agent-health.json'
  $heartbeatPath = Join-Path $root 'fan-agent-heartbeat.txt'
  $profilePath = Join-Path $root 'fan-profile.json'
}
if ($Samples -lt 1 -or $Samples -gt 30 -or $IntervalSeconds -lt 0 -or $IntervalSeconds -gt 30) { throw 'FAN_HANDOFF_BLOCK: invalid sampling settings' }
if (!(Test-Path -LiteralPath $profilePath -PathType Leaf)) { throw 'FAN_HANDOFF_BLOCK: saved profile missing' }
if (!(Test-Path -LiteralPath $heartbeatPath -PathType Leaf)) { throw 'FAN_HANDOFF_BLOCK: heartbeat missing' }
$profileHash = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
$previousStamp = [datetime]::MinValue
$changed = $false
for ($i=0; $i -lt $Samples; $i++) {
  if (!(Test-Path -LiteralPath $healthPath -PathType Leaf)) { throw 'FAN_HANDOFF_BLOCK: health missing' }
  $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
  $stamp = ([datetime]$health.UpdatedUtc).ToUniversalTime()
  $age = ((Get-Date).ToUniversalTime() - $stamp).TotalSeconds
  $heartbeatAge = ((Get-Date).ToUniversalTime() - (Get-Item -LiteralPath $heartbeatPath).LastWriteTimeUtc).TotalSeconds
  if ($health.Version -ne $ExpectedVersion) { throw ('FAN_HANDOFF_BLOCK: unexpected version ' + $health.Version) }
  if (!$health.Healthy -or !$health.HidOk) { throw 'FAN_HANDOFF_BLOCK: unhealthy/HID failure' }
  if ($age -lt -2 -or $age -gt 15 -or $heartbeatAge -gt 20) { throw ('FAN_HANDOFF_BLOCK: stale status age=' + $age + ' heartbeatAge=' + $heartbeatAge) }
  if ($null -eq $health.Rpms -or @($health.Rpms).Count -ne 4) { throw 'FAN_HANDOFF_BLOCK: missing RPM data' }
  if (@($health.Rpms | Where-Object { $_ -gt 0 }).Count -eq 0) { throw 'FAN_HANDOFF_BLOCK: no active RPM readback' }
  if (!$health.ProcessId -or $health.ProcessId -le 0) { throw 'FAN_HANDOFF_BLOCK: invalid agent PID' }
  if (!$FixtureRoot) {
    $agentProcess = Get-Process -Id $health.ProcessId -ErrorAction SilentlyContinue
    if (!$agentProcess) { throw 'FAN_HANDOFF_BLOCK: agent process missing' }
    if ($agentProcess.ProcessName -ne 'SpankyhoOptimizer.RuntimeHost') { throw ('FAN_HANDOFF_BLOCK: wrong agent process ' + $agentProcess.ProcessName) }
    if ($agentProcess.StartTime.ToUniversalTime() -gt $stamp) { throw 'FAN_HANDOFF_BLOCK: process started after health snapshot' }
  }
  if (!$health.LastHidSuccessUtc -or (((Get-Date).ToUniversalTime() - ([datetime]$health.LastHidSuccessUtc).ToUniversalTime()).TotalSeconds -gt 30)) { throw 'FAN_HANDOFF_BLOCK: stale HID success' }
  if ($previousStamp -ne [datetime]::MinValue -and $stamp -gt $previousStamp) { $changed = $true }
  $previousStamp = $stamp
  Write-Output ('FAN_READONLY_SAMPLE index=' + ($i+1) + ' version=' + $health.Version + ' ageSec=' + [math]::Round($age,1) + ' rpm=' + ($health.Rpms -join ','))
  if ($i+1 -lt $Samples) { Start-Sleep -Seconds $IntervalSeconds }
}
if ($Samples -ge 3 -and !$changed) { throw 'FAN_HANDOFF_BLOCK: health timestamp did not advance' }
if ((Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash -ne $profileHash) { throw 'FAN_HANDOFF_BLOCK: profile changed during observation' }
Write-Output ('FAN_HANDOFF_READONLY_PASS version=' + $ExpectedVersion + ' samples=' + $Samples + ' profileUnchanged=true')
