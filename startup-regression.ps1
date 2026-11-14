param([ValidateSet('record','check')][string]$Mode='check',[int]$Runs=5,[double]$RegressionPct=20)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(!(Test-Path $exe)){throw 'Release EXE missing'}
$dir=Join-Path $root '.startup-baseline';New-Item -ItemType Directory -Force $dir|Out-Null
$times=@()
for($i=0;$i -lt [Math]::Max(3,$Runs);$i++){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $p=Start-Process $exe -ArgumentList '--startup-hotpath-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
  if(!$p.WaitForExit(30000)){try{$p.Kill()}catch{};throw 'startup hotpath smoke timeout'}
  $sw.Stop();if($p.ExitCode -ne 0){throw ('startup hotpath smoke exit='+$p.ExitCode)}
  $times+=$sw.Elapsed.TotalMilliseconds
}
$sorted=@($times|Sort-Object);$median=$sorted[[int][math]::Floor($sorted.Count/2)]
$current=[pscustomobject]@{utc=(Get-Date).ToUniversalTime().ToString('o');runs=$times;medianMs=[math]::Round($median,2)}
$latest=Join-Path $dir 'latest.json';$base=Join-Path $dir 'baseline.json';$current|ConvertTo-Json|Set-Content $latest -Encoding UTF8
if($Mode -eq 'record' -or !(Test-Path $base)){Copy-Item $latest $base -Force;Write-Host ('SPANKY_STARTUP_BASELINE '+$current.medianMs+'ms');exit 0}
$b=Get-Content $base -Raw|ConvertFrom-Json;$pct=(($current.medianMs-$b.medianMs)/[double]$b.medianMs)*100
Write-Host ('SPANKY_STARTUP median='+$current.medianMs+'ms delta='+[math]::Round($pct,1)+'%')
if($pct -gt $RegressionPct){exit 42};Write-Host 'SPANKY_STARTUP_REGRESSION_OK'
