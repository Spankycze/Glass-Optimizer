param([switch]$Full)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build-state.ps1')
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$xamlReuseTargets=Join-Path $root 'build-fast-xaml-reuse.targets'
$outDir=Join-Path $root 'artifacts\quality\analyzers'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$canonicalLog=Join-Path $outDir 'extended-analyzers.log'
$log=Join-Path $outDir ('extended-analyzers-'+$PID+'.log')
$latestPointer=Join-Path $outDir 'extended-analyzers.latest.txt'
$sw=[Diagnostics.Stopwatch]::StartNew()
$restore=@(dotnet restore $app -p:ExtendedAnalyzerAudit=true --nologo 2>&1)
if($LASTEXITCODE -ne 0){$restore|Set-Content $log -Encoding UTF8;throw 'extended analyzer restore failed'}
$incrementalArg=$(if($Full){'--no-incremental'}else{$null})
$reuseArgs=@()
if(-not $Full -and (Test-SpankyStructuralFresh) -and (Test-SpankyXamlReuseSafe) -and (Test-Path $xamlReuseTargets)){
  $releaseIntermediate=@(Get-ChildItem (Join-Path $root 'src\SpankyhoOptimizer\obj\Release') -Recurse -File -Filter 'SpankyhoOptimizer.dll' -ErrorAction SilentlyContinue | Where-Object {$_.DirectoryName -match '\\intermediatexaml$'} | Select-Object -First 1)
  if($releaseIntermediate.Count){
    $reuseArgs=@('-p:SpankyFastXamlReuse=true',('-p:CustomAfterMicrosoftCommonTargets='+$xamlReuseTargets))
  }
}
$build=@(dotnet build $app -c Release -r win-x64 --no-restore $incrementalArg --nologo -m:1 -v:normal -p:UseSharedCompilation=false -p:ExtendedAnalyzerAudit=true -p:ReportAnalyzer=true -p:TreatWarningsAsErrors=false @reuseArgs 2>&1)
$exit=$LASTEXITCODE;$sw.Stop()
$compileErrors=@($build|Where-Object{$_ -match ': error (CS[0-9]+|MSB[0-9]+|NETSDK[0-9]+):'})
@($restore+$build)|Set-Content $log -Encoding UTF8
$interesting=@($build|Where-Object{$_ -match ': (warning|error) (CA1416|CA1422|CA1849|CA1851|CA1869|VSTHRD[0-9]+|AsyncFixer[0-9]+|PH[0-9]+|MA[0-9]+|RCS[0-9]+):'}|Sort-Object -Unique)
$timing=@($build|Where-Object{$_ -match '(?i)Total analyzer execution time|Analyzer execution time|Generator execution time'}|Sort-Object -Unique)
$mode=$(if($Full){'full'}elseif($reuseArgs.Count){'incremental+xaml-reuse'}else{'incremental'})
Write-Output ('EXTENDED_ANALYZERS mode={0} exit={1} compileErrors={2} ms={3} findings={4} log={5}' -f $mode,$exit,$compileErrors.Count,[math]::Round($sw.Elapsed.TotalMilliseconds,1),$interesting.Count,$log)
$interesting|Select-Object -First 80|ForEach-Object{Write-Output ('ANALYZER_FINDING '+$_)}
$timing|Select-Object -First 40|ForEach-Object{Write-Output ('ANALYZER_TIMING '+$_)}
# Restore normal assets so audit-only analyzers never leak into ordinary builds.
$normalRestore=@(dotnet restore $app -p:ExtendedAnalyzerAudit=false --nologo 2>&1)
$normalRestoreExit=$LASTEXITCODE
if($normalRestoreExit -ne 0){$normalRestore|Select-Object -Last 20|ForEach-Object{Write-Output ('ANALYZER_NORMAL_RESTORE '+$_)}}
if($Full){
  try{dotnet build-server shutdown | Out-Null}catch{}
}
if($compileErrors.Count -gt 0){
  $compileErrors|Select-Object -First 20|ForEach-Object{Write-Output ('ANALYZER_COMPILE_ERROR '+$_)}
  exit $exit
}
if($normalRestoreExit -ne 0){exit $normalRestoreExit}
if($exit -ne 0){Write-Output 'EXTENDED_ANALYZERS_BASELINED_DIAGNOSTIC_EXIT_IGNORED'}
try{Copy-Item $log $canonicalLog -Force -ErrorAction Stop}catch{}
$log|Set-Content $latestPointer -Encoding UTF8
[pscustomobject]@{completedAt=(Get-Date).ToString('o');log=$log;exit=$exit;compileErrors=$compileErrors.Count;findings=$interesting.Count} |
  ConvertTo-Json | Set-Content ($log+'.complete.json') -Encoding UTF8