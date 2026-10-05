$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PWD 'artifacts\telemetry-631'
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$note=[IO.File]::ReadAllText((Join-Path $artifactDir 'release-note.txt'))
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\build-release.ps1 *> (Join-Path $artifactDir 'release.log')
if(!$?){throw 'Release failed'}
if([IO.File]::ReadAllText((Join-Path $artifactDir 'release.log')) -notmatch 'RELEASE_OK|CACHE_HIT'){throw 'Release marker missing'}
$src=Join-Path $PWD 'artifacts\release\Spankyho_Optimizer_V15_4_631_Setup_Compact.exe'
if(!(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.631*'){throw 'Installer version mismatch'}
$dest=Join-Path ([Environment]::GetFolderPath('Desktop')) (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop hash mismatch'}
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe'|Where-Object {$_.FullName -ne $dest}|Remove-Item -Force
[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8
$installText=[IO.File]::ReadAllText((Join-Path $PWD 'black-ui-621-install.ps1')).Replace('black-ui-621','telemetry-631').Replace('15.4.621','15.4.631')
[IO.File]::WriteAllText((Join-Path $PWD 'telemetry-631-install.ps1'),$installText)
& .\telemetry-631-install.ps1
if(!$?){throw 'Install verification failed'}
& .\ai-workflow.ps1 -Action complete-task -TaskId 'telemetry-631' -Summary 'Installed631: RAM summary and gauge update from same live sample; late snapshot retains latest data. Red regression confirmed, six green suites and gated release/install passed.' -NextAction 'RAM telemetry correction complete and installed631. Source632 is automatic bump; await next user request.' -Done @('Failing regression confirmed before fix','Live RAM summary/gauge and late snapshot verified','Fresh631 six smoke suites and gated release installed') -Evidence @('artifacts\telemetry-631\validation-result.json','artifacts\telemetry-631\deployment-result.json','artifacts\telemetry-631\release-result.json')
& .\update-project-state.ps1 -Reason 'telemetry-631-installed-verified'
Write-Output 'RAM631_RELEASE_INSTALLED_OK'
