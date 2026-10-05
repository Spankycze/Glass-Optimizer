$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PWD 'artifacts\depth-polish-625'
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$note=[IO.File]::ReadAllText((Join-Path $artifactDir 'release-note.txt'))
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\build-release.ps1 *> (Join-Path $artifactDir 'release.log')
if(!$?){throw 'Release failed'}
if([IO.File]::ReadAllText((Join-Path $artifactDir 'release.log')) -notmatch 'RELEASE_OK|CACHE_HIT'){throw 'Release marker missing'}
$src=Join-Path $PWD 'artifacts\release\Spankyho_Optimizer_V15_4_625_Setup_Compact.exe'
if(!(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.625*'){throw 'Installer version mismatch'}
$dest=Join-Path ([Environment]::GetFolderPath('Desktop')) (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop hash mismatch'}
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe'|Where-Object {$_.FullName -ne $dest}|Remove-Item -Force
[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8
$installText=[IO.File]::ReadAllText((Join-Path $PWD 'black-ui-621-install.ps1')).Replace('black-ui-621','depth-polish-625').Replace('15.4.621','15.4.625')
[IO.File]::WriteAllText((Join-Path $PWD 'depth-polish-625-install.ps1'),$installText)
& .\depth-polish-625-install.ps1
if(!$?){throw 'Install verification failed'}
& .\ai-workflow.ps1 -Action complete-task -TaskId 'depth-polish-625'
& .\update-project-state.ps1 -Reason 'depth-polish-625-installed-verified'
Write-Output 'DEPTH625_RELEASE_INSTALLED_OK'
