param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$ExpectedVersion
)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $ExePath -PathType Leaf)){
  throw 'READY_ISOLATION_EXE_MISSING'
}
$exe=[IO.Path]::GetFullPath($ExePath)
$actual=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if(([version]$actual).ToString(3) -ne ([version]$ExpectedVersion).ToString(3)){
  throw "READY_ISOLATION_APP_VERSION_MISMATCH actual=$actual expected=$ExpectedVersion"
}
# The elevated WinUI probe resolves its temp root through GetTempPath2,
# which does not inherit this build process's Dev Drive TEMP override. Use the
# interactive user's real TEMP for the guarded marker.
$runtimeTemp=[Environment]::GetEnvironmentVariable('TEMP','User')
if([string]::IsNullOrWhiteSpace($runtimeTemp)){
  $runtimeTemp=Join-Path $env:LOCALAPPDATA 'Temp'
}
$runtimeTemp=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($runtimeTemp))
if(-not(Test-Path -LiteralPath $runtimeTemp -PathType Container)){
  throw 'READY_ISOLATION_USER_TEMP_MISSING'
}
$owned=Join-Path $runtimeTemp ('SpankyhoOptimizer-ReadyTest-'+[guid]::NewGuid().ToString('N'))
$foreign=Join-Path $runtimeTemp ('SpankyhoOptimizer-NotAllowed-'+[guid]::NewGuid().ToString('N'))
$crashFile=Join-Path $env:ProgramData 'SpankyhoOptimizer\startup-state.json'
$registry='HKCU:\Software\SpankyhoOptimizer\Runtime'
function Get-RegistryFingerprint {
  if(-not(Test-Path -LiteralPath $registry)){return 'KEY_ABSENT'}
  $v=Get-ItemProperty -LiteralPath $registry -ErrorAction Stop
  return ('{0}|{1}|{2}|{3}' -f $v.Version,$v.ConsecutiveFailures,$v.LastStartUtc,$v.LastCompleteUtc)
}
function Get-CrashFileFingerprint {
  if(-not(Test-Path -LiteralPath $crashFile)){return 'FILE_ABSENT'}
  return (Get-FileHash -LiteralPath $crashFile -Algorithm SHA256).Hash
}
function Assert-Probe([string]$path,[int]$expectedExit,[string]$scenario){
  $p=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList ('--runtime-ready-file="'+$path+'"') -WindowStyle Hidden -PassThru
  if(-not $p.WaitForExit(20000)){
    try {$p.Kill($true)} catch {}
    throw "READY_ISOLATION_PROCESS_HUNG $scenario"
  }
  $p.Refresh()
  if($p.ExitCode -ne $expectedExit){
    throw "READY_ISOLATION_EXIT_MISMATCH scenario=$scenario expected=$expectedExit actual=$($p.ExitCode)"
  }
  Write-Output "READY_ISOLATION_${scenario}_PASS exit=$($p.ExitCode)"
}
$beforeReg=Get-RegistryFingerprint
$beforeCrash=Get-CrashFileFingerprint
try{
  [IO.Directory]::CreateDirectory($owned)|Out-Null
  [IO.Directory]::CreateDirectory($foreign)|Out-Null
  $marker=Join-Path $owned 'ready.txt'
  Assert-Probe $marker 0 'FRESH_MARKER'
  $first=[IO.File]::ReadAllText($marker)
  if($first -notmatch ('version='+[regex]::Escape(([version]$ExpectedVersion).ToString(3))) -or
      $first -notmatch 'activatedMs=\d+' -or $first -notmatch 'pid=\d+'){
    throw 'READY_ISOLATION_MARKER_FORMAT_INVALID'
  }
  Assert-Probe $marker 74 'EXISTING_MARKER_REJECTED'
  if([IO.File]::ReadAllText($marker) -cne $first){throw 'READY_ISOLATION_EXISTING_FILE_CHANGED'}

  $foreignFile=Join-Path $foreign 'untouched.txt'
  [IO.File]::WriteAllText($foreignFile,'PRESERVE')
  Assert-Probe $foreignFile 74 'FOREIGN_DIRECTORY_REJECTED'
  if([IO.File]::ReadAllText($foreignFile) -cne 'PRESERVE'){throw 'READY_ISOLATION_FOREIGN_FILE_CHANGED'}

  $traversal=Join-Path $owned ('..\outside-'+[guid]::NewGuid().ToString('N')+'.txt')
  $outFull=[IO.Path]::GetFullPath($traversal)
  if(Test-Path -LiteralPath $outFull){throw 'READY_ISOLATION_OUTSIDE_ALREADY_EXISTS'}
  Assert-Probe $traversal 74 'PARENT_TRAVERSAL_REJECTED'
  if(Test-Path -LiteralPath $outFull){throw 'READY_ISOLATION_OUTSIDE_FILE_CREATED'}

  if((Get-RegistryFingerprint) -cne $beforeReg -or
     (Get-CrashFileFingerprint) -cne $beforeCrash){
    throw 'READY_ISOLATION_LIVE_CRASHLOOP_STATE_CHANGED'
  }
  Write-Output 'READY_ISOLATION_SUITE_PASS valid=1 rejected=3 crashloopUnchanged=true'
} finally {
  if(Test-Path -LiteralPath $owned){Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop}
  if(Test-Path -LiteralPath $foreign){Remove-Item -LiteralPath $foreign -Recurse -Force -ErrorAction Stop}
}
