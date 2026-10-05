param(
  [switch]$Deep,
  [string]$DumpPath=''
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss'
$outDir=Join-Path $root ('artifacts\quality\audit-'+$stamp)
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$steps=New-Object System.Collections.Generic.List[object]

function Step([string]$Name,[scriptblock]$Action,[string]$BlockedPattern='',[switch]$AllowWarn){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $status='PASS'; $msg=''
  try{
    $global:LASTEXITCODE=0
    $out=@(& $Action 2>&1)
    $code=if($null -eq $LASTEXITCODE){0}else{[int]$LASTEXITCODE}
    $msg=($out|Select-Object -Last 60)-join [Environment]::NewLine
    if($code -ne 0){
      if($BlockedPattern -and $msg -match $BlockedPattern){$status='BLOCKED'}
      elseif($AllowWarn){$status='WARN'}
      else{$status='FAIL'}
    }
  }catch{
    $msg=$_.Exception.Message
    if($BlockedPattern -and $msg -match $BlockedPattern){$status='BLOCKED'}
    elseif($AllowWarn){$status='WARN'}
    else{$status='FAIL'}
  }finally{$sw.Stop()}
  $steps.Add([pscustomobject]@{name=$Name;status=$status;ms=[math]::Round($sw.Elapsed.TotalMilliseconds,1);message=$msg})|Out-Null
  Write-Output ('QUALITY_STEP '+$Name+' status='+$status+' ms='+[math]::Round($sw.Elapsed.TotalMilliseconds,1))
  if($status -eq 'FAIL'){throw ($Name+' failed: '+$msg)}
}

Step 'project-index' { & (Join-Path $root 'project-index.ps1') }
Step 'roslyn-custom' { dotnet run --project (Join-Path $root 'tools\RoslynAudit\RoslynAudit.csproj') -c Release -- $root (Join-Path $outDir 'roslyn-audit.json') }
Step 'extended-analyzers' { & (Join-Path $root 'extended-analyzers.ps1') }
Step 'analyzer-timing' { & (Join-Path $root 'analyzer-timing.ps1') -Quick }
Step 'api-compat' { & (Join-Path $root 'api-compat-gate.ps1') }
Step 'sdk-upgrade-gate' { & (Join-Path $root 'sdk-upgrade-gate.ps1') }
Step 'platform-compat' { & (Join-Path $root 'platform-compat-gate.ps1') }
Step 'architecture-tests' { dotnet test (Join-Path $root 'tools\ArchitectureTests\ArchitectureTests.csproj') -c Release --nologo --no-restore }
Step 'behavior-tests' { dotnet test (Join-Path $root 'tools\BehaviorTests\BehaviorTests.csproj') -c Release --nologo --no-restore }
Step 'fuzz-smoke' { & (Join-Path $root 'run-fuzz.ps1') -Mode smoke }
Step 'fault-injection' { & (Join-Path $root 'fault-audit.ps1') }
Step 'coyote-concurrency' { & (Join-Path $root 'run-coyote.ps1') }
Step 'flaky-critical' { & (Join-Path $root 'flaky-audit.ps1') -Iterations 3 }
Step 'rollback-matrix' { & (Join-Path $root 'rollback-matrix.ps1') -NoRestore }
Step 'diagnostics-harness' { dotnet build (Join-Path $root 'tools\DiagnosticsHarness\DiagnosticsHarness.csproj') -c Release --nologo --no-restore }
Step 'binlog-report' { & (Join-Path $root 'binlog-report.ps1') }
Step 'framework-classifier' { & (Join-Path $root 'framework-bug-classifier.ps1') -Text 'SpankyhoOptimizer MainWindow Microsoft.UI.Xaml DispatcherQueue' | Set-Content (Join-Path $outDir 'classifier.json') -Encoding UTF8 }

Step 'build-test' {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Set-Location '$root'; .\build-test.ps1 -Full -Force"
}
if($Deep){
  Step 'visual-regression' { & (Join-Path $root 'visual-regression.ps1') }
}

$releaseDll=Join-Path $root 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.dll'
if(Test-Path $releaseDll){ Step 'ilspy-verify' { & (Join-Path $root 'ilspy-verify.ps1') -AssemblyPath $releaseDll } }

if($DumpPath -and (Test-Path $DumpPath)){
  Step 'dump-triage' { & (Join-Path $root 'dump-triage.ps1') -DumpPath $DumpPath -SkipGcDump }
}

if($Deep){
  Step 'analyzer-timing' { & (Join-Path $root 'analyzer-timing.ps1') }
  Step 'opentelemetry-build' { & (Join-Path $root 'build-otel.ps1') }
  Step 'benchmarkdotnet' { & (Join-Path $root 'run-benchmarks.ps1') -Filter '*DnsReadback*' }
  $statsPath=Join-Path $root 'artifacts\perf\statistics-latest.json'
  $hasRegression=$false
  if(Test-Path $statsPath){
    try{$stats=Get-Content $statsPath -Raw|ConvertFrom-Json;$hasRegression=@($stats.regressions).Count -gt 0}catch{}
  }
  if($hasRegression){
    Step 'etwprof' { & (Join-Path $root 'etwprof-capture.ps1') } '(OpenProcess failed|Unable to open process HANDLE|etwprof failed exit=262|needs elevation)'
  }else{
    Write-Output 'QUALITY_STEP etwprof status=SKIP reason=no-performance-regression'
  }
  Step 'stryker' {
    Push-Location (Join-Path $root 'tools\BehaviorTests')
    try{
      dotnet stryker -s '..\MutationLegacy.sln' -tp 'BehaviorTests.csproj' -p 'SpankyhoOptimizer.csproj' -m '**/OperationCoordinator.cs' -b 0 -r ClearText --configuration Release --concurrency 4
    }finally{Pop-Location}
  } '(simulated build failed|Failed to analyze project builds|can''t be mutated)'
}

$summary=[ordered]@{
  generatedAt=(Get-Date).ToString('o')
  pass=@($steps|Where-Object status -eq 'PASS').Count
  warn=@($steps|Where-Object status -eq 'WARN').Count
  blocked=@($steps|Where-Object status -eq 'BLOCKED').Count
  fail=@($steps|Where-Object status -eq 'FAIL').Count
  steps=$steps
}
$report=Join-Path $outDir 'quality-report.json'
$summary|ConvertTo-Json -Depth 10|Set-Content $report -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\quality')|Out-Null
Copy-Item $report (Join-Path $root 'artifacts\quality\latest.json') -Force
$steps|ForEach-Object{Write-Output ('QUALITY_RESULT {0} {1} {2:n1}ms' -f $_.status,$_.name,$_.ms)}
Write-Output ('QUALITY_AUDIT_OK pass='+$summary.pass+' warn='+$summary.warn+' blocked='+$summary.blocked+' fail='+$summary.fail+' dir='+$outDir)
if($summary.fail -gt 0){exit 42}
