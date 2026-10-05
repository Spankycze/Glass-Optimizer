$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PSScriptRoot 'artifacts\ai-performance-615'
$release=Get-Content (Join-Path $artifactDir 'release-result.json') -Raw | ConvertFrom-Json
$setup=$release.desktopInstaller
if($release.version -notlike '15.4.615*'){throw 'Release version mismatch'}
if((Get-FileHash $setup -Algorithm SHA256).Hash -ne $release.sha256){throw 'Installer hash mismatch'}

$started=(Get-Date).ToUniversalTime()
$healthPath=Join-Path $env:LOCALAPPDATA 'SpankyhoOptimizer\FanAgent\fan-agent-health.json'
$oldPid=0
if(Test-Path $healthPath){
  try{$oldPid=[int](Get-Content $healthPath -Raw|ConvertFrom-Json).ProcessId}catch{}
}

$install=Start-Process $setup -ArgumentList '--silent-install' -Verb RunAs -PassThru
if(-not $install.WaitForExit(45000)){throw 'Installer still running'}
if($install.ExitCode -ne 0){throw "Installer exit=$($install.ExitCode)"}

$dir='C:\Program Files\Spankyho Optimizer'
$core=Join-Path $dir 'SpankyhoOptimizer.Core.dll'
$hostExe=Join-Path $dir 'SpankyhoOptimizer.RuntimeHost.exe'
if((Get-Item $core).VersionInfo.ProductVersion -notlike '15.4.615*'){throw 'Installed core version mismatch'}
if((Get-Item $hostExe).VersionInfo.ProductVersion -notlike '15.4.615*'){throw 'Installed host version mismatch'}
$payload=Join-Path $PSScriptRoot 'artifacts\.cache\app-publish\SpankyhoOptimizer.Core.dll'
if((Get-FileHash $core).Hash -ne (Get-FileHash $payload).Hash){throw 'Installed payload hash mismatch'}

$deadline=(Get-Date).AddSeconds(12)
while($oldPid -gt 0 -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline){
  Start-Sleep -Milliseconds 300
}
if($oldPid -gt 0 -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)){
  throw "Old fan agent still alive pid=$oldPid"
}

Start-Process $hostExe -ArgumentList '--fan-agent' -WorkingDirectory $dir -Verb RunAs | Out-Null
$deadline=(Get-Date).AddSeconds(45)
$ready=$false
do {
  Start-Sleep -Milliseconds 500
  if(Test-Path $healthPath){
    try {
      $health=Get-Content $healthPath -Raw|ConvertFrom-Json
      $alive=Get-Process -Id ([int]$health.ProcessId) -ErrorAction SilentlyContinue
      $fresh=[DateTime]::Parse($health.UpdatedUtc).ToUniversalTime() -gt $started
      $ready=$health.Version -eq '15.4.615' -and $health.Healthy -and $health.HidOk -and $alive -and $fresh
    } catch {$ready=$false}
  }
} while(-not $ready -and (Get-Date) -lt $deadline)
if(-not $ready){throw 'Fresh 15.4.615 fan agent health not ready'}

$taskDeadline=(Get-Date).AddSeconds(15)
$taskXml=''
do {
  $taskXml = (& schtasks.exe /Query /TN 'SpankyhoOptimizer-FanAgent' /XML 2>$null) -join [Environment]::NewLine
  if($taskXml -match 'RestartOnFailure' -and $taskXml -match '--fan-agent'){break}
  Start-Sleep -Milliseconds 500
} while((Get-Date) -lt $taskDeadline)
if($taskXml -notmatch 'RestartOnFailure' -or $taskXml -notmatch '--fan-agent'){throw 'Fan Agent restart-on-failure task missing'}
if($taskXml -match 'fan-watchdog-loop'){throw 'Fan Agent task still launches resident watchdog'}

$cleanup=Join-Path $PSScriptRoot '.ai-state\cleanup-runtimehost-615.ps1'
$cleanArgs=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$cleanup,'-KeepPid',[string]$health.ProcessId)
$clean=Start-Process 'powershell.exe' -ArgumentList $cleanArgs -Verb RunAs -PassThru
if(-not $clean.WaitForExit(15000)){throw 'Runtime cleanup still running'}
if($clean.ExitCode -ne 0){throw "Runtime cleanup exit=$($clean.ExitCode)"}
Start-Sleep -Seconds 2

$bridgePath='C:\Program Files\SpankyhoOptimizer\HardwareBridge\hardware-telemetry-bridge.json'
$bridgeDeadline=(Get-Date).AddSeconds(20)
$bridgeReady=$false
do {
  if(Test-Path $bridgePath){
    try {
      $bridge=Get-Content $bridgePath -Raw|ConvertFrom-Json
      $bridgeAlive=Get-Process -Id ([int]$bridge.ProcessId) -ErrorAction SilentlyContinue
      $bridgeReady=$bridge.Version -eq '15.4.615' -and $bridge.Elevated -and $bridgeAlive
    } catch {$bridgeReady=$false}
  }
  if(-not $bridgeReady){Start-Sleep -Milliseconds 500}
} while(-not $bridgeReady -and (Get-Date) -lt $bridgeDeadline)
if(-not $bridgeReady){throw 'Hardware bridge 15.4.615 not ready'}

$runtime=@(Get-Process 'SpankyhoOptimizer.RuntimeHost' -ErrorAction SilentlyContinue)
$session1=@($runtime|Where-Object SessionId -eq 1)
$session0=@($runtime|Where-Object SessionId -eq 0)
if($session1.Count -ne 1){throw "Expected 1 user RuntimeHost, found $($session1.Count)"}
if($session0.Count -ne 1){throw "Expected 1 system RuntimeHost, found $($session0.Count)"}
if($session1[0].Id -ne [int]$health.ProcessId){throw 'User RuntimeHost is not current Fan Agent'}
if($session0[0].Id -ne [int]$bridge.ProcessId){throw 'System RuntimeHost is not current Hardware Bridge'}

$runKey=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$watchdogRun=$runKey.'Spankyho Fan Watchdog'
if($null -ne $watchdogRun){throw 'Legacy watchdog Run value still present'}

$report=[ordered]@{
 installedVersion=(Get-Item $core).VersionInfo.ProductVersion
 fanAgentPid=[int]$health.ProcessId
 fanHealthy=[bool]$health.Healthy
 hidOk=[bool]$health.HidOk
 bridgePid=[int]$bridge.ProcessId
 bridgeVersion=$bridge.Version
 permanentRuntimeHosts=$runtime.Count
 session1Hosts=$session1.Count
 session0Hosts=$session0.Count
 restartOnFailureTask=$true
 watchdogRunRemoved=$true
 payloadVerified=$true
 utc=(Get-Date).ToUniversalTime().ToString('O')
}
$report|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'deployment-result.json') -Encoding UTF8
Write-Output ("AI615_INSTALLED_OK fanPid="+$report.fanAgentPid+" bridgePid="+$report.bridgePid+" runtimeHosts="+$report.permanentRuntimeHosts)
