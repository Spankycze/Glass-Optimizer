param([string]$OutputPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputPath)){$OutputPath=Join-Path $root 'artifacts\perf\analyzers.txt'}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath)|Out-Null
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$out=@(dotnet build $app -c Release -r win-x64 --no-restore --nologo -m -v:minimal -p:RunAnalyzers=true -p:RunAnalyzersDuringBuild=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended -p:TreatWarningsAsErrors=false 2>&1)
$exit=$LASTEXITCODE
$out|Set-Content -LiteralPath $OutputPath -Encoding UTF8
$interesting=@($out|Where-Object{$_ -match '\b(CA18\d\d|CS4014|CS1998|VSTHRD\d+)\b'}|Sort-Object -Unique)
Write-Output ('ANALYZER_AUDIT exit={0} findings={1} output={2}' -f $exit,$interesting.Count,$OutputPath)
$interesting|Select-Object -First 40|ForEach-Object{Write-Output ('ANALYZER '+$_)}
if($exit -ne 0){exit $exit}