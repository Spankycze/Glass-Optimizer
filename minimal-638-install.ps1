$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$release=Get-Content 'artifacts\minimal-638\release-result.json' -Raw|ConvertFrom-Json
$setup=$release.desktopInstaller
if($release.version -notlike '15.4.638*'){throw 'Release version mismatch'}
if((Get-FileHash $setup).Hash -ne $release.sha256){throw 'Installer hash mismatch'}
$healthPath=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SpankyhoOptimizer\FanAgent\fan-agent-health.json'
$oldHealth=$null
if(Test-Path $healthPath){$oldHealth=Get-Content $healthPath -Raw|ConvertFrom-Json}
$started=(Get-Date).ToUniversalTime()
$install=Start-Process $setup -ArgumentList '--silent-install' -Verb RunAs -PassThru
if(-not $install.WaitForExit(45000)){throw 'Installer still running; inspect before retry'}
if($install.ExitCode -ne 0){throw "Installer exit=$($install.ExitCode)"}
$dir='C:\Program Files\Spankyho Optimizer'
$core=Join-Path $dir 'SpankyhoOptimizer.Core.dll'
$hostExe=Join-Path $dir 'SpankyhoOptimizer.RuntimeHost.exe'
$version=(Get-Item $core).VersionInfo.ProductVersion
if($version -notlike '15.4.638*'){throw 'Installed core version mismatch'}
if((Get-Item $hostExe).VersionInfo.ProductVersion -notlike '15.4.638*'){throw 'Installed host version mismatch'}
$payload=Join-Path $PSScriptRoot 'artifacts\.cache\app-publish\SpankyhoOptimizer.Core.dll'
if((Get-FileHash $core).Hash -ne (Get-FileHash $payload).Hash){throw 'Installed payload hash mismatch'}
$health=Get-Content $healthPath -Raw|ConvertFrom-Json
$alreadyLive=$health.Version -eq '15.4.638' -and (Get-Process -Id $health.ProcessId -ErrorAction SilentlyContinue)
if(-not $alreadyLive){
 if(Get-Process -Id $health.ProcessId -ErrorAction SilentlyContinue){throw 'Old agent still alive; do not start duplicate'}
 Start-Process $hostExe -ArgumentList '--fan-agent' -WorkingDirectory $dir -Verb RunAs|Out-Null
}
$deadline=(Get-Date).AddSeconds(40)
do {
 Start-Sleep -Milliseconds 500
 $health=Get-Content $healthPath -Raw|ConvertFrom-Json
 $alive=Get-Process -Id $health.ProcessId -ErrorAction SilentlyContinue
 $fresh=[DateTime]::Parse($health.UpdatedUtc).ToUniversalTime() -gt $started
 $ready=$health.Version -eq '15.4.638' -and $health.Healthy -and $health.HidOk -and $alive -and $fresh
} while(-not $ready -and (Get-Date) -lt $deadline)
if(-not $ready){throw 'Fresh installed runtime health not ready'}
Start-Process (Join-Path $dir 'SpankyhoOptimizer.exe') -WorkingDirectory $dir -Verb RunAs|Out-Null
$report=[ordered]@{installedVersion=$version;liveVersion=$health.Version;processId=$health.ProcessId;updatedUtc=$health.UpdatedUtc;healthy=$health.Healthy;hidOk=$health.HidOk;payloadVerified=$true}
$report|ConvertTo-Json|Set-Content 'artifacts\minimal-638\deployment-result.json' -Encoding UTF8
Write-Output ("BLACK_UI_INSTALLED_OK pid="+$health.ProcessId+" healthy="+$health.Healthy+" hidOk="+$health.HidOk)
