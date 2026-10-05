$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$outDir=Join-Path $root 'artifacts\quality\analyzers'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$log=Join-Path $outDir 'extended-analyzers.log'
$sw=[Diagnostics.Stopwatch]::StartNew()
$restore=@(dotnet restore $app -p:ExtendedAnalyzerAudit=true --nologo 2>&1)
if($LASTEXITCODE -ne 0){$restore|Set-Content $log -Encoding UTF8;throw 'extended analyzer restore failed'}
$build=@(dotnet build $app -c Release -r win-x64 --no-restore --nologo -m -v:normal -p:ExtendedAnalyzerAudit=true -p:ReportAnalyzer=true -p:TreatWarningsAsErrors=false 2>&1)
$exit=$LASTEXITCODE;$sw.Stop()
@($restore+$build)|Set-Content $log -Encoding UTF8
$interesting=@($build|Where-Object{$_ -match '\b(MA\d+|VSTHRD\d+|AsyncFixer\d+)\b'})
$timing=@($build|Where-Object{$_ -match '(?i)analyzer.*(time|execution)|Total analyzer'})
Write-Output ('EXTENDED_ANALYZERS exit={0} ms={1} findings={2} log={3}' -f $exit,[math]::Round($sw.Elapsed.TotalMilliseconds,1),$interesting.Count,$log)
$interesting|Select-Object -First 80|ForEach-Object{Write-Output ('ANALYZER_FINDING '+$_)}
$timing|Select-Object -First 40|ForEach-Object{Write-Output ('ANALYZER_TIMING '+$_)}
if($exit -ne 0){exit $exit}