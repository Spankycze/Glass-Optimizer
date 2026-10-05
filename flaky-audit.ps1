param(
  [int]$Iterations=5,
  [switch]$Full
)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(-not(Test-Path $exe)){throw 'Debug EXE missing. Run build-fast first.'}

$cases=@(
  @{arg='--process-timeout-smoke';timeout=30},
  @{arg='--process-cancel-smoke';timeout=30},
  @{arg='--single-instance-smoke';timeout=30},
  @{arg='--dns-readback-guard-smoke';timeout=30},
  @{arg='--dialog-lifecycle-smoke';timeout=30},
  @{arg='--exostenza-cache-reuse-smoke';timeout=30}
)
if($Full){
  $cases+=@(
    @{arg='--startup-delta-smoke';timeout=30},
    @{arg='--preflight-warm-smoke';timeout=30},
    @{arg='--bcd-cache-smoke';timeout=30}
  )
}

$outDir=Join-Path $root 'artifacts\quality\flaky'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$rows=[Collections.Generic.List[object]]::new()
$failures=[Collections.Generic.List[object]]::new()
$total=[Diagnostics.Stopwatch]::StartNew()

foreach($case in $cases){
  $durations=[Collections.Generic.List[double]]::new()
  for($i=1;$i -le $Iterations;$i++){
    $sw=[Diagnostics.Stopwatch]::StartNew()
    $p=Start-Process -FilePath $exe -ArgumentList $case.arg -WorkingDirectory (Split-Path $exe) -PassThru
    $ok=$p.WaitForExit([int]$case.timeout*1000)
    if(-not $ok){try{Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}catch{}}
    $sw.Stop()
    $exit=if($ok){$p.ExitCode}else{-999}
    $durations.Add($sw.Elapsed.TotalMilliseconds)
    if(-not $ok -or $exit -ne 0){
      $failures.Add([pscustomobject]@{case=$case.arg;iteration=$i;exit=$exit;timeout=(-not $ok);ms=[math]::Round($sw.Elapsed.TotalMilliseconds,1)})
      break
    }
  }
  $arr=@($durations|Sort-Object)
  $median=if($arr.Count){$arr[[int][math]::Floor(($arr.Count-1)/2)]}else{0}
  $max=if($arr.Count){($arr|Measure-Object -Maximum).Maximum}else{0}
  $rows.Add([pscustomobject]@{
    case=$case.arg;runs=$durations.Count
    medianMs=[math]::Round($median,1);maxMs=[math]::Round($max,1)
    flaky=@($failures|Where-Object case -eq $case.arg).Count -gt 0
  })|Out-Null
  Write-Output ('[FLAKY] '+$case.arg+' runs='+$durations.Count+' median='+[math]::Round($median,1)+'ms max='+[math]::Round($max,1)+'ms')
  if($failures.Count){break}
}

$total.Stop()
$report=[ordered]@{
  generatedAt=(Get-Date).ToString('o');iterations=$Iterations;full=[bool]$Full
  totalMs=[math]::Round($total.Elapsed.TotalMilliseconds,1)
  cases=@($rows);failures=@($failures)
}
$latest=Join-Path $outDir 'latest.json'
$tmp=$latest+'.tmp'
[IO.File]::WriteAllText($tmp,($report|ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false))
Move-Item $tmp $latest -Force
if($failures.Count){
  $f=$failures[0]
  $message=('FLAKY '+$f.case+' iteration='+$f.iteration+' exit='+$f.exit+' timeout='+$f.timeout)
  $store=Join-Path $root 'regression-store.ps1'
  if(Test-Path $store){
    & $store -Action add -Kind 'flaky' -Key ('flaky-'+($f.case -replace '[^A-Za-z0-9]+','-').Trim('-').ToLowerInvariant()) -Message $message -Artifact $latest | Out-Null
  }
  Write-Output ('[FAIL] '+$message)
  exit 42
}
Write-Output ('FLAKY_AUDIT_OK cases='+$rows.Count+' iterations='+$Iterations+' totalMs='+[math]::Round($total.Elapsed.TotalMilliseconds,1))
