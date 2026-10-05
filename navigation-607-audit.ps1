param([ValidateSet('before','after')][string]$Phase='before')
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$exe=& '.\resolve-debug-exe.ps1'
$out=Join-Path $PSScriptRoot 'artifacts\navigation-607'
New-Item -ItemType Directory -Force $out|Out-Null
$rows=@()
$log='C:\ProgramData\SpankyhoOptimizer\performance.log'
$sentinel=Join-Path ([IO.Path]::GetTempPath()) 'SpankyOptimizer.PerfTelemetry.enabled'
$wasEnabled=Test-Path $sentinel
try {
  Set-Content $sentinel '1' -NoNewline
  foreach($i in 1..5){
    $start=Get-Date
    $p=Start-Process $exe -ArgumentList '--performance-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
    if(-not $p.WaitForExit(90000)){try{$p.Kill()}catch{};throw 'Navigation smoke timeout'}
    $lines=@(Get-Content $log -Tail 2400)
    $cold=@($lines | Where-Object {$_ -match 'PERF_COLD '})|Select-Object -Last 1
    $warm=@($lines | Where-Object {$_ -match 'PERF_WARM '})|Select-Object -Last 1
    $memory=@($lines | Where-Object {$_ -match 'PERF_MEMORY '})|Select-Object -Last 1
    $rows+=[pscustomobject]@{iteration=$i;exit=$p.ExitCode;started=$start.ToString('o');cold=$cold;warm=$warm;memory=$memory}
    Write-Output ("NAV607 sample=$i exit=$($p.ExitCode) $cold")
  }
  $rows|ConvertTo-Json -Depth 4|Set-Content (Join-Path $out ($Phase+'.json')) -Encoding UTF8
  $lines | Where-Object {$_ -match 'NAV_PHASE|PAGE_BUILD_PHASE|VIRTUAL_|PREWARM|PERF_'} | Set-Content (Join-Path $out ($Phase+'-phases.log')) -Encoding UTF8
  if(@($rows | Where-Object exit -ne 0).Count){throw 'Navigation budget failure; evidence captured'}
} finally {if(-not $wasEnabled){Remove-Item $sentinel -ErrorAction SilentlyContinue}}


