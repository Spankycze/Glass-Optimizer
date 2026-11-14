$ErrorActionPreference='Stop'
$root=Join-Path $PSScriptRoot 'verify-fan-handoff-readonly.ps1'
$fixture=Join-Path $env:TEMP ('fan-handoff-fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $fixture | Out-Null
try {
 Set-Content (Join-Path $fixture 'fan-profile.json') '{}'
 Set-Content (Join-Path $fixture 'fan-agent-heartbeat.txt') 'ok'
 $base=[ordered]@{UpdatedUtc=(Get-Date).ToUniversalTime().ToString('o');ProcessId=123;Version='15.4.1473';Healthy=$true;HidOk=$true;LastHidSuccessUtc=(Get-Date).ToUniversalTime().ToString('o');Rpms=@(0,1500,1480,0)}
 foreach($scenario in @('stale-health','stale-hid','no-rpm','unhealthy','wrong-version')) {
  $h=[ordered]@{}; foreach($key in $base.Keys){$h[$key]=$base[$key]}
  switch($scenario) {
   'stale-health' {$h.UpdatedUtc=(Get-Date).ToUniversalTime().AddMinutes(-2).ToString('o')}
   'stale-hid' {$h.LastHidSuccessUtc=(Get-Date).ToUniversalTime().AddMinutes(-2).ToString('o')}
   'no-rpm' {$h.Rpms=@(0,0,0,0)}
   'unhealthy' {$h.HidOk=$false}
   'wrong-version' {$h.Version='15.4.1474'}
  }
  $h|ConvertTo-Json | Set-Content (Join-Path $fixture 'fan-agent-health.json')
  $output=& pwsh -NoProfile -File $root -ExpectedVersion '15.4.1473' -Samples 1 -FixtureRoot $fixture 2>&1
  if($LASTEXITCODE -ne 1 -or !($output -match 'FAN_HANDOFF_BLOCK:')) {throw ('FIXTURE_FAIL '+$scenario)}
  Write-Output ('FAN_FIXTURE_REJECT_OK '+$scenario)
 }
} finally {Remove-Item $fixture -Recurse -Force -ErrorAction SilentlyContinue}
Write-Output 'FAN_FIXTURE_GATE_PASS 5/5'
