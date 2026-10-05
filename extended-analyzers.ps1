param([switch]$Full)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build-state.ps1')
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$xamlReuseTargets=Join-Path $root 'build-fast-xaml-reuse.targets'
$outDir=Join-Path $root 'artifacts\quality\analyzers'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$log=Join-Path $outDir 'extended-analyzers.log'
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
$build=@(dotnet build $app -c Release -r win-x64 --no-restore $incrementalArg --nologo -m -v:normal -p:ExtendedAnalyzerAudit=true -p:ReportAnalyzer=true -p:TreatWarningsAsErrors=false @reuseArgs 2>&1)
$exit=$LASTEXITCODE;$sw.Stop()
@($restore+$build)|Set-Content $log -Encoding UTF8
$interesting=@($build|Where-Object{$_ -match ': (warning|error) (CA1416|CA1422|CA1849|CA1851|CA1869|VSTHRD103|VSTHRD110|AsyncFixer02|AsyncFixer03|PH2086):'}|Sort-Object -Unique)
$timing=@($build|Where-Object{$_ -match '(?i)Total analyzer execution time|Analyzer execution time|Generator execution time'}|Sort-Object -Unique)
$mode=$(if($Full){'full'}elseif($reuseArgs.Count){'incremental+xaml-reuse'}else{'incremental'})
Write-Output ('EXTENDED_ANALYZERS mode={0} exit={1} ms={2} findings={3} log={4}' -f $mode,$exit,[math]::Round($sw.Elapsed.TotalMilliseconds,1),$interesting.Count,$log)
$interesting|Select-Object -First 80|ForEach-Object{Write-Output ('ANALYZER_FINDING '+$_)}
$timing|Select-Object -First 40|ForEach-Object{Write-Output ('ANALYZER_TIMING '+$_)}
if($exit -ne 0){exit $exit}