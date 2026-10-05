param(
  [Parameter(Position=0)]
  [ValidateSet('fast','deep','perf','release')]
  [string]$Mode='fast',
  [ValidateSet('Quiet','Normal','Verbose')]
  [string]$OutputMode='Quiet',
  [switch]$Force
)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=$PSScriptRoot
$logRoot=Join-Path $root '.ai-state\logs'
New-Item -ItemType Directory -Force -Path $logRoot|Out-Null
$runId=(Get-Date -Format 'yyyyMMdd_HHmmss_fff')
$runDir=Join-Path $logRoot ('devcheck_'+$runId)
New-Item -ItemType Directory -Force -Path $runDir|Out-Null
$results=[Collections.Generic.List[object]]::new()
$total=[Diagnostics.Stopwatch]::StartNew()

function Normalize-Key([string]$line){
  $s=$line.Trim().ToLowerInvariant()
  $s=$s -replace '\b\d{4}-\d{2}-\d{2}[t ][0-9:.+-z]+\b','<time>'
  $s=$s -replace '\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b','<guid>'
  $s=$s -replace '\b0x[0-9a-f]+\b','0x#'
  $s=$s -replace '\(\d+,\d+\)','(#,#)'
  $s=$s -replace '\b\d{2,}\b','#'
  $s=$s -replace '\s+',' '
  return $s
}

function Show-Failure([string[]]$lines,[string]$log){
  $seen=@{};$shown=0
  foreach($line in $lines){
    if($line -notmatch '(?i)(error|fail|exception|fatal|timeout|denied|CS\d{4}|MSB\d{4})'){continue}
    $key=Normalize-Key $line
    if(-not $key -or $seen.ContainsKey($key)){continue}
    $seen[$key]=$true
    Write-Output ('[FAIL] '+$line.Trim())
    $shown++;if($shown -ge 20){break}
  }
  Write-Output ('[LOG] '+$log)
}

function Invoke-DirectStep([string]$Name,[scriptblock]$Action){
  $log=Join-Path $runDir ($Name+'.log')
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $lines=@();$ok=$true
  try{$lines=@(& $Action *>&1)}catch{$ok=$false;$lines+=@($_.Exception.ToString())}
  $sw.Stop()
  [IO.File]::WriteAllLines($log,[string[]]@($lines),[Text.UTF8Encoding]::new($false))
  $results.Add([pscustomobject]@{name=$Name;ok=$ok;ms=[math]::Round($sw.Elapsed.TotalMilliseconds,1);log=$log;lines=$lines.Count})|Out-Null
  if(-not $ok){Show-Failure ([string[]]$lines) $log;throw ($Name+' failed')}
  Write-Output ('['+$Name.ToUpperInvariant()+'] OK '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+' ms')
  if($OutputMode -eq 'Verbose'){$lines|Select-Object -Last 30}
}

function Invoke-ExternalStep([string]$Name,[string]$Script,[string[]]$Args=@()){
  $log=Join-Path $runDir ($Name+'.log')
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $argList=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$Script)+$Args
  $psi=[Diagnostics.ProcessStartInfo]::new()
  $psi.FileName='powershell.exe'
  function Quote-Arg([string]$v){
    if($null -eq $v){return '""'}
    if($v -notmatch '[\s"]'){return $v}
    return '"'+($v -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1')+'"'
  }
  $psi.Arguments=($argList|ForEach-Object{Quote-Arg ([string]$_)}) -join ' '
  $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
  $psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
  $p=[Diagnostics.Process]::new();$p.StartInfo=$psi
  if(-not $p.Start()){throw ($Name+' failed to start')}
  $stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
  $p.WaitForExit();$text=$stdout.Result+[Environment]::NewLine+$stderr.Result;$code=$p.ExitCode
  $p.Dispose();$sw.Stop()
  [IO.File]::WriteAllText($log,$text,[Text.UTF8Encoding]::new($false))
  $lines=@($text -split '\r?\n'|Where-Object{$_})
  $ok=$code -eq 0
  $results.Add([pscustomobject]@{name=$Name;ok=$ok;ms=[math]::Round($sw.Elapsed.TotalMilliseconds,1);log=$log;lines=$lines.Count})|Out-Null
  if(-not $ok){Show-Failure ([string[]]$lines) $log;throw ($Name+' failed exit='+$code)}
  Write-Output ('['+$Name.ToUpperInvariant()+'] OK '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+' ms')
  if($OutputMode -eq 'Verbose'){$lines|Select-Object -Last 30}
}

function Run-Fast {
  $fpScript=Join-Path $root 'fast-fingerprint.ps1'
  if(-not $Force -and (Test-Path $fpScript) -and (Test-Path (Join-Path $root '.ai-state\dev-check-fast.ok'))){
    $fp=@(& $fpScript)
    if($fp.Count -and [string]$fp[-1] -like 'FRESH *'){
      $script:FastNoChange=$true
      $script:FastNoChangeMs=[math]::Round($total.Elapsed.TotalMilliseconds,1)
      return
    }
  }

  Invoke-DirectStep 'index' { & (Join-Path $root 'project-index.ps1') }
  Invoke-DirectStep 'build' { & (Join-Path $root 'build-fast.ps1') }
  Invoke-DirectStep 'test'  { & (Join-Path $root 'build-test.ps1') -NoBuild }
  Invoke-DirectStep 'state' { & (Join-Path $root 'update-project-state.ps1') -Reason 'dev-check-fast' -Light }

  if(Test-Path $fpScript){ & $fpScript -Save | Out-Null }
  [IO.File]::WriteAllText((Join-Path $root '.ai-state\dev-check-fast.ok'),(Get-Date).ToString('o'))
}

function Run-Deep {
  Run-Fast
  Invoke-ExternalStep 'quality-deep' (Join-Path $root 'quality-audit.ps1') @('-Deep')
}

function Run-Perf {
  Run-Fast
  Invoke-ExternalStep 'perf-gate' (Join-Path $root 'perf-regression-gate.ps1')
  if(Test-Path (Join-Path $root 'run-benchmarks.ps1')){
    Invoke-ExternalStep 'benchmark' (Join-Path $root 'run-benchmarks.ps1') @('-Filter','*DnsReadback*')
  }
  $latest=Join-Path $root 'artifacts\perf\statistics-latest.json'
  $regression=$false
  if(Test-Path $latest){
    try{$j=Get-Content $latest -Raw|ConvertFrom-Json;$regression=@($j.regressions).Count -gt 0}catch{}
  }
  if($regression -and (Test-Path (Join-Path $root 'etwprof-capture.ps1'))){
    Invoke-ExternalStep 'etw' (Join-Path $root 'etwprof-capture.ps1')
  }else{Write-Output '[PERF] ETW skipped; no regression trigger'}
}

function Run-Release {
  Run-Fast
  Invoke-ExternalStep 'flaky' (Join-Path $root 'flaky-audit.ps1') @('-Iterations','5','-Full')
  Invoke-ExternalStep 'rollback' (Join-Path $root 'rollback-matrix.ps1') @('-NoRestore')
  Invoke-ExternalStep 'mutation' (Join-Path $root 'mutation-audit.ps1')
  Invoke-ExternalStep 'coyote' (Join-Path $root 'run-coyote.ps1')
  Invoke-ExternalStep 'release-gate' (Join-Path $root 'build-full-test.ps1') $(if($Force){@('-Force')}else{@()})
}

switch($Mode){
  'fast' {Run-Fast}
  'deep' {Run-Deep}
  'perf' {Run-Perf}
  'release' {Run-Release}
}

$total.Stop()
$summary=[ordered]@{
  schema=1;mode=$Mode;generatedAt=(Get-Date).ToString('o')
  totalMs=[math]::Round($total.Elapsed.TotalMilliseconds,1)
  steps=@($results)
}
$latest=Join-Path $root '.ai-state\dev-check-latest.json'
$tmp=$latest+'.tmp'
[IO.File]::WriteAllText($tmp,($summary|ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false))
Move-Item $tmp $latest -Force
if($Mode -eq 'fast' -and $script:FastNoChange){
  Write-Output ('[OK] No relevant changes. Build/test cache valid. '+[math]::Round($total.Elapsed.TotalMilliseconds,1)+' ms')
}else{
  Write-Output ('[DONE] '+$Mode+' '+[math]::Round($total.Elapsed.TotalMilliseconds,1)+' ms | steps='+$results.Count+' errors=0')
}
if($OutputMode -eq 'Normal'){
  Write-Output ('[LOG] '+$runDir)
}
