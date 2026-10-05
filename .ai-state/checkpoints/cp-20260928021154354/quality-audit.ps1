param([switch]$IncludeMutation,[switch]$IncludeBenchmarks,[switch]$IncludeEtwprof,[switch]$IncludeOpenTelemetryBuild)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir=Join-Path $root ('artifacts\quality\run-'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
function Step([string]$Name,[scriptblock]$Action){
  $sw=[Diagnostics.Stopwatch]::StartNew(); Write-Output ('QUALITY_STEP_START '+$Name)
  & $Action; $exit=$LASTEXITCODE; $sw.Stop()
  Write-Output ('QUALITY_STEP_END '+$Name+' exit='+$exit+' ms='+[math]::Round($sw.Elapsed.TotalMilliseconds,1))
  if($exit -ne 0){throw ($Name+' failed exit='+$exit)}
}
Step 'Roslyn custom audit' { dotnet run --project (Join-Path $root 'tools\RoslynAudit\RoslynAudit.csproj') -c Release --no-build -- $root (Join-Path $outDir 'roslyn-audit.json') }
Step 'Extended analyzers' { & (Join-Path $root 'extended-analyzers.ps1') | Tee-Object -FilePath (Join-Path $outDir 'extended-analyzers.txt') }
Step 'Architecture tests' { dotnet test (Join-Path $root 'tools\ArchitectureTests\ArchitectureTests.csproj') -c Release --no-build --nologo }
Step 'Fuzz smoke' { dotnet run --project (Join-Path $root 'fuzz\SpankyhoOptimizer.Fuzz\SpankyhoOptimizer.Fuzz.csproj') -c Release --no-build -- --smoke }
Step 'Coyote build' { dotnet build (Join-Path $root 'tools\ConcurrencyTests\ConcurrencyTests.csproj') -c Release --nologo }
Step 'Coyote rewrite+systematic' {
  $src=Join-Path $root 'tools\ConcurrencyTests\bin\Release\net10.0-windows10.0.26100.0\ConcurrencyTests.dll'
  $rewritten=Join-Path $outDir 'coyote-rewritten'; New-Item -ItemType Directory -Force -Path $rewritten|Out-Null
  dotnet (Join-Path $root 'tools\CoyoteRewriter\bin\Release\net10.0\CoyoteRewriter.dll') $src $rewritten
  if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
  dotnet (Join-Path $rewritten 'ConcurrencyTests.dll')
}
Step 'Binlog parse' { & (Join-Path $root 'binlog-report.ps1') | Tee-Object -FilePath (Join-Path $outDir 'binlog-summary.txt') }
Step 'ILSpy release verification' { & (Join-Path $root 'ilspy-verify.ps1') | Tee-Object -FilePath (Join-Path $outDir 'ilspy-summary.txt') }
Step 'Framework classifier smoke' { & (Join-Path $root 'framework-bug-classifier.ps1') -Text 'SpankyhoOptimizer MainWindow Microsoft.UI.Xaml DispatcherQueue' | Set-Content (Join-Path $outDir 'classifier.json') -Encoding UTF8 }
if($IncludeOpenTelemetryBuild){ Step 'OpenTelemetry conditional build' { dotnet build (Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj') -c Release -r win-x64 --nologo -p:EnableOpenTelemetry=true } }
if($IncludeBenchmarks){ Step 'BenchmarkDotNet DNS' { Push-Location (Join-Path $root 'benchmarks\SpankyhoOptimizer.Benchmarks'); try{dotnet run -c Release --no-build -- --filter '*DnsReadback*'} finally{Pop-Location} } }
if($IncludeEtwprof){ Step 'etwprof performance smoke' { & (Join-Path $root 'etwprof-capture.ps1') | Tee-Object -FilePath (Join-Path $outDir 'etwprof.txt') } }
if($IncludeMutation){ Step 'Stryker mutation' { & (Join-Path $root 'mutation-audit.ps1') -Mutate '**/OperationCoordinator.cs' } }
Write-Output ('QUALITY_AUDIT_OK '+$outDir)