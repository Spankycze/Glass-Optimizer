param(
  [string]$BinlogPath='',
  [switch]$Rebuild
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss'
$outDir=Join-Path $root ('artifacts\quality\binlog-'+$stamp)
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$diag=Join-Path $root 'tools\DiagnosticsHarness\DiagnosticsHarness.csproj'

if([string]::IsNullOrWhiteSpace($BinlogPath)){
  $BinlogPath=Join-Path $outDir 'build.binlog'
  $args=@('build',$app,'-c','Debug','-r','win-x64','--nologo','-m','-v:minimal',('/bl:'+ $BinlogPath))
  if($Rebuild){$args+=('-t:Rebuild')}
  & dotnet @args
  if($LASTEXITCODE -ne 0){throw 'binlog build failed'}
}
$BinlogPath=[IO.Path]::GetFullPath($BinlogPath)

dotnet build $diag -c Release --nologo --no-restore
if($LASTEXITCODE -ne 0){throw 'DiagnosticsHarness build failed'}
$dll=Get-ChildItem (Split-Path $diag -Parent) -Recurse -File -Filter 'DiagnosticsHarness.dll' |
  Where-Object {$_.FullName -match '\\bin\\Release\\'} | Select-Object -First 1
$report=Join-Path $outDir 'binlog-report.json'
dotnet $dll.FullName binlog $BinlogPath | Set-Content $report -Encoding UTF8
if($LASTEXITCODE -ne 0){throw 'binlog parser failed'}

$j=Get-Content $report -Raw|ConvertFrom-Json
Write-Output 'BINLOG_TOP_TARGETS'
$j.topTargets|Select-Object -First 10|ForEach-Object{Write-Output ('  {0,8:n1} ms  {1}' -f $_.ms,$_.name)}
Write-Output 'BINLOG_TOP_TASKS'
$j.topTasks|Select-Object -First 10|ForEach-Object{Write-Output ('  {0,8:n1} ms  {1}' -f $_.ms,$_.name)}

$latest=Join-Path $root 'artifacts\quality\binlog-latest.json'
if(Test-Path $latest){
  try{
    $prev=Get-Content $latest -Raw|ConvertFrom-Json
    foreach($cur in @($j.topTargets|Select-Object -First 10)){
      $old=@($prev.topTargets|Where-Object name -eq $cur.name|Select-Object -First 1)
      if($old.Count){Write-Output ('BINLOG_DELTA target={0} {1:n1}->{2:n1} ms delta={3:n1}' -f $cur.name,$old[0].ms,$cur.ms,([double]$cur.ms-[double]$old[0].ms))}
    }
  }catch{}
}
Copy-Item $report $latest -Force
Write-Output ('BINLOG_REPORT_OK '+$report)
