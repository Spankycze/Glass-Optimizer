$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PWD 'artifacts\changelog-630'
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$note=[IO.File]::ReadAllText((Join-Path $artifactDir 'release-note.txt'))
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\build-release.ps1 *> (Join-Path $artifactDir 'release.log')
if(!$?){throw 'Release failed'}
if([IO.File]::ReadAllText((Join-Path $artifactDir 'release.log')) -notmatch 'RELEASE_OK|CACHE_HIT'){throw 'Release marker missing'}
$src=Join-Path $PWD 'artifacts\release\Spankyho_Optimizer_V15_4_630_Setup_Compact.exe'
if(!(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.630*'){throw 'Installer version mismatch'}
$dest=Join-Path ([Environment]::GetFolderPath('Desktop')) (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop hash mismatch'}
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe'|Where-Object {$_.FullName -ne $dest}|Remove-Item -Force
[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8
$installText=[IO.File]::ReadAllText((Join-Path $PWD 'black-ui-621-install.ps1')).Replace('black-ui-621','changelog-630').Replace('15.4.621','15.4.630')
[IO.File]::WriteAllText((Join-Path $PWD 'changelog-630-install.ps1'),$installText)
& .\changelog-630-install.ps1
if(!$?){throw 'Install verification failed'}
& .\ai-workflow.ps1 -Action complete-task -TaskId 'changelog-630' -Summary 'Installed630: changelog remains stable after actual hardware and tweak history refresh. Failing regression reproduced before fix, all six smoke suites and gated release/install passed.' -NextAction 'Changelog correction complete and installed630. Continue with next user request; source631 is only automatic bump.' -Done @('Failing regression confirmed before fix','Actual refresh keeps release rows stable','Fresh630 six smoke suites and gated release installed') -Evidence @('artifacts\changelog-630\validation-result.json','artifacts\changelog-630\deployment-result.json','artifacts\changelog-630\release-result.json')
& .\update-project-state.ps1 -Reason 'changelog-630-installed-verified'
Write-Output 'CHANGELOG630_RELEASE_INSTALLED_OK'
