param([switch]$Quick)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$app=Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'
$outDir=Join-Path $root 'artifacts\quality\analyzer-timing'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null

$modes=@(
  @{Name='builtin'; Props=@()},
  @{Name='meziantou'; Props=@('-p:AnalyzerMeziantou=true')},
  @{Name='threading'; Props=@('-p:AnalyzerThreading=true')},
  @{Name='asyncfixer'; Props=@('-p:AnalyzerAsyncFixer=true')},
  @{Name='philips'; Props=@('-p:AnalyzerPhilips=true')},
  @{Name='all'; Props=@('-p:ExtendedAnalyzerAudit=true')}
)
if($Quick){$modes=@($modes|Where-Object Name -in @('builtin','all'))}
$rows=@()
foreach($mode in $modes){
  $props=@($mode.Props)
  $restoreArgs=@('restore',$app,'--nologo')+$props
  $rout=@(& dotnet @restoreArgs 2>&1)
  if($LASTEXITCODE -ne 0){throw ('Analyzer restore failed: '+$mode.Name)}

  $buildArgs=@('build',$app,'-c','Release','--no-restore','--nologo','-m','-v:minimal',
    '-p:RunAnalyzersDuringBuild=true','-p:ReportAnalyzer=true','-p:TreatWarningsAsErrors=false')+$props
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $bout=@(& dotnet @buildArgs 2>&1)
  $code=$LASTEXITCODE;$sw.Stop()
  ($rout+$bout)|Set-Content (Join-Path $outDir ($mode.Name+'.log')) -Encoding UTF8
  if($code -ne 0){throw ('Analyzer build failed: '+$mode.Name)}
  $findings=@($bout|Where-Object{$_ -match '\b(CA\d+|MA\d+|VSTHRD\d+|AsyncFixer\d+|PH\d+)\b'}|Sort-Object -Unique)
  $timing=@($bout|Where-Object{$_ -match '(?i)analyzer.*(time|execution)|Total analyzer'})
  $rows+=[pscustomobject]@{
    name=$mode.Name
    ms=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
    findings=$findings.Count
    timingLines=$timing.Count
  }
  Write-Output ('ANALYZER_MODE name={0} ms={1:n1} findings={2}' -f $mode.Name,$sw.Elapsed.TotalMilliseconds,$findings.Count)
}
$base=[double]($rows|Where-Object name -eq 'builtin').ms
foreach($row in $rows){
  Add-Member -InputObject $row -NotePropertyName overheadMs -NotePropertyValue ([math]::Round(([double]$row.ms-$base),1))
}
$report=Join-Path $outDir 'latest.json'
$rows|ConvertTo-Json -Depth 5|Set-Content $report -Encoding UTF8
$all=$rows|Where-Object name -eq 'all'
if($all -and [double]$all.overheadMs -gt 5000){
  throw ('Analyzer overhead regression: '+$all.overheadMs+' ms > 5000 ms')
}
Write-Output ('ANALYZER_TIMING_OK modes={0} allOverheadMs={1} report={2}' -f $rows.Count,$all.overheadMs,$report)
