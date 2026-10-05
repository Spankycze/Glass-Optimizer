$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PWD 'artifacts\buttons-633'
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$note=[IO.File]::ReadAllText((Join-Path $artifactDir 'release-note.txt'))
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\build-release.ps1 *> (Join-Path $artifactDir 'release.log')
if(!$?){throw 'Release failed'}
if([IO.File]::ReadAllText((Join-Path $artifactDir 'release.log')) -notmatch 'RELEASE_OK|CACHE_HIT'){throw 'Release marker missing'}
$src=Join-Path $PWD 'artifacts\release\Spankyho_Optimizer_V15_4_633_Setup_Compact.exe'
if(!(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.633*'){throw 'Installer version mismatch'}
$dest=Join-Path ([Environment]::GetFolderPath('Desktop')) (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop hash mismatch'}
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe'|Where-Object {$_.FullName -ne $dest}|Remove-Item -Force
[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8
$installText=[IO.File]::ReadAllText((Join-Path $PWD 'black-ui-621-install.ps1')).Replace('black-ui-621','buttons-633').Replace('15.4.621','15.4.633')
[IO.File]::WriteAllText((Join-Path $PWD 'buttons-633-install.ps1'),$installText)
& .\buttons-633-install.ps1
if(!$?){throw 'Install verification failed'}
& .\ai-workflow.ps1 -Action complete-task -TaskId 'buttons-633' -Summary 'Installed633: solid black Quick/primary/dialog buttons, neutral gradient edges and hover/pressed states. Six suites and gated release/install passed.' -NextAction 'Installed633 button redesign complete. Next UI batch: GPU/Startup/Storage consistency audit. Source634 is automatic bump.' -Done @('Material Quick/primary/dialog buttons','Neutral edges and hover/pressed surfaces','Fresh633 six smoke suites and gated release installed') -Evidence @('artifacts\buttons-633\validation-result.json','artifacts\buttons-633\deployment-result.json','artifacts\buttons-633\release-result.json')
& .\update-project-state.ps1 -Reason 'buttons-633-installed-verified'
Write-Output 'BUTTONS633_RELEASE_INSTALLED_OK'
