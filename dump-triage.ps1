param(
  [int]$ProcessId=0,
  [string]$DumpPath='',
  [switch]$SkipGcDump
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss'
$outDir=Join-Path $root ('artifacts\diagnostics\dump-'+$stamp)
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$diag=Join-Path $root 'tools\DiagnosticsHarness\DiagnosticsHarness.csproj'

if($ProcessId -le 0 -and [string]::IsNullOrWhiteSpace($DumpPath)){throw 'Use -ProcessId or -DumpPath'}

if($ProcessId -gt 0){
  $DumpPath=Join-Path $outDir 'process.dmp'
  dotnet-dump collect -p $ProcessId -o $DumpPath
  if($LASTEXITCODE -ne 0){throw 'dotnet-dump collect failed'}
}

$DumpPath=[IO.Path]::GetFullPath($DumpPath)
dotnet build $diag -c Release --nologo --no-restore
if($LASTEXITCODE -ne 0){throw 'DiagnosticsHarness build failed'}
$dll=Get-ChildItem (Split-Path $diag -Parent) -Recurse -File -Filter 'DiagnosticsHarness.dll' |
  Where-Object {$_.FullName -match '\\bin\\Release\\'} | Select-Object -First 1
if(-not $dll){throw 'DiagnosticsHarness.dll missing'}

$clrmd=Join-Path $outDir 'clrmd.json'
dotnet $dll.FullName dump $DumpPath | Set-Content $clrmd -Encoding UTF8
if($LASTEXITCODE -ne 0){throw 'ClrMD triage failed'}

$dumpForDumpling=$DumpPath
if(-not $SkipGcDump -and $ProcessId -gt 0){
  $gc=Join-Path $outDir 'heap.gcdump'
  dotnet-gcdump collect -p $ProcessId -o $gc
  if($LASTEXITCODE -eq 0 -and (Test-Path $gc)){$dumpForDumpling=$gc}
}

$dumpling=Join-Path $outDir 'dumpling.txt'
if(Get-Command dumpling -ErrorAction SilentlyContinue){
  $dumplingExe=(Get-Command dumpling -ErrorAction Stop).Source
  $dumplingErr=$dumpling+'.stderr.txt'
  $quotedDump='"'+$dumpForDumpling.Replace('"','\"')+'"'
  $p=Start-Process -FilePath $dumplingExe -ArgumentList @('analyze',$quotedDump,'--suspects','--dominators','-t','20') -NoNewWindow -Wait -PassThru -RedirectStandardOutput $dumpling -RedirectStandardError $dumplingErr
  if(Test-Path $dumplingErr){Get-Content $dumplingErr | Add-Content $dumpling; Remove-Item $dumplingErr -Force}
  if($p.ExitCode -ne 0){Write-Output ('DUMPLING_WARN exit='+$p.ExitCode)}
}
Write-Output ('DUMP_TRIAGE_OK dump='+$DumpPath+' clrmd='+$clrmd+' dumpling='+$dumpling)
