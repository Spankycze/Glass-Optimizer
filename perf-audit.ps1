param([int]$Repetitions=10,[switch]$SkipTrace,[switch]$SkipSoak,[switch]$SkipAnalyzer,[switch]$UpdateBaseline,[switch]$DeepDiagnostics)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$logRoot='C:\ProgramData\SpankyhoOptimizer'
$perfLog=Join-Path $logRoot 'performance.log'
$appLog=Join-Path $logRoot 'optimizer.log'
$procLog=Join-Path $logRoot 'process-telemetry.log'
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss'
$outDir=Join-Path $root ('artifacts\perf\'+$stamp)
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
function Median([double[]]$v){if(-not $v -or $v.Count -eq 0){return 0};$s=@($v|Sort-Object);if($s.Count%2){return [double]$s[[int]($s.Count/2)]};return ([double]$s[$s.Count/2-1]+[double]$s[$s.Count/2])/2}
function P95([double[]]$v){if(-not $v -or $v.Count -eq 0){return 0};$s=@($v|Sort-Object);$i=[Math]::Max(0,[Math]::Min($s.Count-1,[Math]::Ceiling($s.Count*0.95)-1));return [double]$s[$i]}
function Stats([System.Collections.IEnumerable]$v){$a=@($v|ForEach-Object{[double]$_});[pscustomobject]@{samples=$a;medianMs=[math]::Round((Median $a),1);p95Ms=[math]::Round((P95 $a),1)}}
function Parse-Kv([string]$line){$h=@{};foreach($m in [regex]::Matches($line,'([A-Za-z][A-Za-z0-9]*)=(-?\d+(?:[\.,]\d+)?)')){$h[$m.Groups[1].Value]=[double]::Parse($m.Groups[2].Value.Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture)};return $h}
function Last-Line([string]$path,[string]$pattern){if(-not(Test-Path $path)){return ''};return ((Select-String -Path $path -Pattern $pattern|Select-Object -Last 1).Line)}
function Run-App([string]$arg,[int]$timeoutSec=120){$sw=[Diagnostics.Stopwatch]::StartNew();$p=Start-Process -FilePath $exe -ArgumentList $arg -WorkingDirectory (Split-Path $exe) -PassThru;if(-not $p.WaitForExit($timeoutSec*1000)){try{$p.Kill($true)}catch{};throw ('timeout '+$arg)};$sw.Stop();if($p.ExitCode -ne 0){throw ('app smoke failed {0} exit={1}' -f $arg,$p.ExitCode)};return $sw.Elapsed.TotalMilliseconds}
function Add-SectionSample($bucket,$map){foreach($s in 'network','storage','gpu','startup','services','defender','debloat','power'){$k=$s+'Ms';if($map.ContainsKey($k)){$bucket[$s].Add([double]$map[$k])|Out-Null}}}
$buildSw=[Diagnostics.Stopwatch]::StartNew();$buildOut=@();try{$buildOut=@(& (Join-Path $root 'build-fast.ps1') 2>&1);$buildOk=$true}catch{$buildOut+=($_|Out-String);$buildOk=$false};$buildSw.Stop();$buildOut|Set-Content (Join-Path $outDir 'build-fast.log') -Encoding UTF8;if(-not $buildOk){throw 'build-fast failed'}
$exe=& (Join-Path $root 'resolve-debug-exe.ps1')
Write-Host ('PERF_EXE ' + $exe)
if(-not(Test-Path $exe)){throw 'Debug/staged EXE missing after build-fast'}
& (Join-Path $root 'source-index.ps1') -OutputPath (Join-Path $outDir 'source-index.json') | Set-Content (Join-Path $outDir 'source-index-summary.txt')
$buildProfileDir=Join-Path $outDir 'build-profile'; & (Join-Path $root 'build-profile.ps1') -OutputDir $buildProfileDir | Set-Content (Join-Path $outDir 'build-profile-summary.txt'); $buildProfile=Get-Content (Join-Path $buildProfileDir 'build-profile.json') -Raw|ConvertFrom-Json
if(-not $SkipAnalyzer){& (Join-Path $root 'analyze-performance.ps1') -OutputPath (Join-Path $outDir 'analyzers.txt') | Set-Content (Join-Path $outDir 'analyzer-summary.txt')}
$sentinel=Join-Path ([IO.Path]::GetTempPath()) 'SpankyOptimizer.PerfTelemetry.enabled';Set-Content $sentinel '1' -NoNewline
$procOffset=if(Test-Path $procLog){(Get-Item $procLog).Length}else{0}
$cold=@{};$warm=@{};foreach($s in 'network','storage','gpu','startup','services','defender','debloat','power'){$cold[$s]=New-Object System.Collections.Generic.List[double];$warm[$s]=New-Object System.Collections.Generic.List[double]}
$private=New-Object System.Collections.Generic.List[double];$handles=New-Object System.Collections.Generic.List[double];$ws=New-Object System.Collections.Generic.List[double]
for($i=1;$i -le $Repetitions;$i++){
  [void](Run-App '--performance-smoke' 120)
  $c=Parse-Kv (Last-Line $perfLog 'PERF_COLD ');$w=Parse-Kv (Last-Line $perfLog 'PERF_WARM ');$m=Parse-Kv (Last-Line $perfLog 'PERF_MEMORY ')
  Add-SectionSample $cold $c;Add-SectionSample $warm $w
  if($m.ContainsKey('privateDeltaMB')){$private.Add($m['privateDeltaMB'])|Out-Null};if($m.ContainsKey('handleDelta')){$handles.Add($m['handleDelta'])|Out-Null};if($m.ContainsKey('wsDeltaMB')){$ws.Add($m['wsDeltaMB'])|Out-Null}
}
$startup=New-Object System.Collections.Generic.List[double]
for($i=1;$i -le $Repetitions;$i++){[void](Run-App '--startup-hotpath-smoke' 30);$m=Parse-Kv (Last-Line $appLog 'STARTUP_HOTPATH_SMOKE_OK');if($m.ContainsKey('activatedMs')){$startup.Add($m['activatedMs'])|Out-Null}}
$backend=[ordered]@{}
foreach($spec in @(@('--storage-probe-smoke','STORAGE_PROBE_SMOKE_OK'),@('--gpu-detection-smoke','GPU_DETECTION_SMOKE_OK'),@('--task-scheduler-backend-smoke','TASK_SCHEDULER_BACKEND_SMOKE_OK'),@('--nic-capability-smoke','NIC_CAPABILITY_SMOKE_OK'),@('--dns-readback-guard-smoke','DNS_READBACK_GUARD_SMOKE_OK'))){[void](Run-App $spec[0] 90);$backend[$spec[0].TrimStart('-')]=Parse-Kv (Last-Line $appLog $spec[1])}
[void](Run-App '--dns-benchmark-smoke' 45);$backend['dns-benchmark-smoke']=Parse-Kv (Last-Line $appLog 'DNS_BENCHMARK_SMOKE_OK')
[void](Run-App '--process-runner-bench' 60);$backend['process-runner-bench']=Parse-Kv (Last-Line $appLog 'PROCESS_RUNNER_BENCH_OK')
$enum=New-Object System.Collections.Generic.List[double];1..30|ForEach-Object{$sw=[Diagnostics.Stopwatch]::StartNew();$n=[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces();$null=$n.Length;$sw.Stop();$enum.Add($sw.Elapsed.TotalMilliseconds)|Out-Null};$backend['adapter-enumeration']=[ordered]@{medianMs=[math]::Round((Median $enum),3);p95Ms=[math]::Round((P95 $enum),3)}
$soak=$null;if(-not $SkipSoak){[void](Run-App '--full-smoke' 420);$line=Last-Line $appLog 'SOAK_AUDIT navigations=';if($line -match 'wsDelta=(-?\d+)MB privateDelta=(-?\d+)MB managedDelta=(-?\d+)MB handleDelta=(-?\d+)'){$soak=[ordered]@{wsDeltaMb=[double]$matches[1];privateDeltaMb=[double]$matches[2];managedDeltaMb=[double]$matches[3];handleDelta=[double]$matches[4]}}}
$trace=[ordered]@{}
if(-not $SkipTrace){
  try{
    $nettrace=Join-Path $outDir 'performance.nettrace'
    $counter=Join-Path $outDir 'runtime-counters.json'
    $etl=Join-Path $outDir 'startup.etl'
    $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($outDir))
    $arg='--elevated-perf-trace-smoke-b64='+$encoded
    $null=Start-Process -FilePath $exe -ArgumentList $arg -WorkingDirectory (Split-Path $exe) -PassThru
    $complete=Join-Path $outDir 'trace.complete'
    $deadline=[DateTime]::UtcNow.AddSeconds(55)
    do{
      Start-Sleep -Milliseconds 500
      $traceReady=(Test-Path $nettrace) -and ((Get-Item $nettrace).Length -gt 4096)
      $counterReady=(Test-Path $counter) -and ((Get-Item $counter).Length -gt 256)
      $etlReady=(Test-Path $etl) -and ((Get-Item $etl).Length -gt 4096)
      $completeReady=Test-Path $complete
      if($traceReady -and $counterReady -and $etlReady -and $completeReady){break}
    }while([DateTime]::UtcNow -lt $deadline)
    if(-not($traceReady -and $counterReady -and $etlReady -and $completeReady)){throw 'Elevated profiler artifacts were not completed before timeout.'}
    $trace.dotnetTrace=$nettrace
    $trace.dotnetCounters=$counter
    $trace.wpr=$etl
    $trace.requiresElevation=$false
  }catch{
    $trace.error=$_.Exception.Message
    $trace.requiresElevation=$true
  }
}
$procText='';if(Test-Path $procLog){$fs=[IO.File]::Open($procLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite);try{$fs.Seek($procOffset,[IO.SeekOrigin]::Begin)|Out-Null;$sr=[IO.StreamReader]::new($fs);$procText=$sr.ReadToEnd();$sr.Dispose()}finally{$fs.Dispose()}}
$procRows=@();foreach($line in ($procText -split "`r?`n")){if($line -match 'PROCESS_TELEMETRY file=(\S+) caller=(\S+) ms=(\d+) exit=(-?\d+) stdoutBytes=(\d+) stderrBytes=(\d+)'){$procRows += [pscustomobject]@{file=$matches[1];caller=$matches[2];ms=[double]$matches[3];exit=[int]$matches[4];stdoutBytes=[int]$matches[5];stderrBytes=[int]$matches[6]}}}
$byFile=@($procRows|Group-Object file|Sort-Object Count -Descending|ForEach-Object{[pscustomobject]@{file=$_.Name;count=$_.Count;totalMs=[math]::Round((($_.Group|Measure-Object ms -Sum).Sum),1);medianMs=[math]::Round((Median @($_.Group.ms)),1)}})
$byCaller=@($procRows|Group-Object caller|Sort-Object Count -Descending|ForEach-Object{[pscustomobject]@{caller=$_.Name;count=$_.Count;totalMs=[math]::Round((($_.Group|Measure-Object ms -Sum).Sum),1);files=@($_.Group.file|Sort-Object -Unique)}})
$powershellCount=@($procRows|Where-Object{$_.file -in 'powershell.exe','pwsh.exe'}).Count
$coldReport=[ordered]@{};$warmReport=[ordered]@{};foreach($s in 'network','storage','gpu','startup','services','defender','debloat','power'){$coldReport[$s]=Stats $cold[$s];$warmReport[$s]=Stats $warm[$s]}
$report=[ordered]@{generatedAt=(Get-Date).ToString('o');version=((Get-Item $exe).VersionInfo.ProductVersion);repetitions=$Repetitions;buildFastMs=[math]::Round($buildSw.Elapsed.TotalMilliseconds,1);buildProfile=$buildProfile;startup=(Stats $startup);cold=$coldReport;warm=$warmReport;memory=[ordered]@{privateDeltaMbMedian=[math]::Round((Median $private),1);privateDeltaMbP95=[math]::Round((P95 $private),1);wsDeltaMbMedian=[math]::Round((Median $ws),1);handleDeltaMedian=[math]::Round((Median $handles),1)};backend=$backend;soak=$soak;subprocess=[ordered]@{count=$procRows.Count;powershellCount=$powershellCount;byFile=$byFile;byCaller=$byCaller};trace=$trace}
$latest=Join-Path $root 'artifacts\perf\latest.json'
$reportPath=Join-Path $outDir 'report.json'
$json=$report|ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($reportPath,$json,(New-Object Text.UTF8Encoding($false)))
$gatePath=$reportPath
for($i=0;$i -lt 10;$i++){
  try{
    [IO.File]::WriteAllText($latest,$json,(New-Object Text.UTF8Encoding($false)))
    $gatePath=$latest
    break
  }catch{
    Start-Sleep -Milliseconds 120
  }
}
if($gatePath -eq $reportPath){Write-Output ('PERF_AUDIT_WARN latest.json locked; gate uses '+$reportPath)}
$summary=@();$summary+=('Startup median/P95: {0:n1}/{1:n1} ms' -f $report.startup.medianMs,$report.startup.p95Ms);foreach($s in 'network','storage','gpu','startup','services','defender','debloat','power'){$summary+=('{0,-9} cold {1,5:n1}/{2,5:n1} ms  warm {3,5:n1}/{4,5:n1} ms' -f $s,$report.cold[$s].medianMs,$report.cold[$s].p95Ms,$report.warm[$s].medianMs,$report.warm[$s].p95Ms)};$summary+=('Private delta median: {0:n1} MB; handles median: {1:n0}' -f $report.memory.privateDeltaMbMedian,$report.memory.handleDeltaMedian);$summary+=('Subprocesses: {0}; PowerShell: {1}' -f $report.subprocess.count,$report.subprocess.powershellCount);$summary|Set-Content (Join-Path $outDir 'summary.txt') -Encoding UTF8;$summary|ForEach-Object{Write-Output ('PERF_AUDIT '+$_)}
& (Join-Path $root 'perf-regression-gate.ps1') -ReportPath $gatePath -UpdateBaseline:$UpdateBaseline; $gateExit=$LASTEXITCODE
if(-not(Test-Path (Join-Path $root '.ai-state\perf-regression-baseline.json'))){& (Join-Path $root 'perf-regression-gate.ps1') -ReportPath $gatePath -UpdateBaseline; $gateExit=$LASTEXITCODE}
Copy-Item (Join-Path $outDir 'summary.txt') (Join-Path $root 'artifacts\perf\latest-summary.txt') -Force
Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
Write-Output ('PERF_AUDIT_OK report='+$latest+' dir='+$outDir)
if($gateExit -ne 0){exit $gateExit}if($DeepDiagnostics){
  Write-Output 'PERF_AUDIT_DEEP_DIAGNOSTICS_START'
  & (Join-Path $root 'quality-audit.ps1') -Deep
  if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
  Write-Output 'PERF_AUDIT_DEEP_DIAGNOSTICS_OK'
}
