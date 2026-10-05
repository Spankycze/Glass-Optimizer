$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PWD 'artifacts\section-632'
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$note=[IO.File]::ReadAllText((Join-Path $artifactDir 'release-note.txt'))
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\build-release.ps1 *> (Join-Path $artifactDir 'release.log')
if(!$?){throw 'Release failed'}
if([IO.File]::ReadAllText((Join-Path $artifactDir 'release.log')) -notmatch 'RELEASE_OK|CACHE_HIT'){throw 'Release marker missing'}
$src=Join-Path $PWD 'artifacts\release\Spankyho_Optimizer_V15_4_632_Setup_Compact.exe'
if(!(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.632*'){throw 'Installer version mismatch'}
$dest=Join-Path ([Environment]::GetFolderPath('Desktop')) (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop hash mismatch'}
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe'|Where-Object {$_.FullName -ne $dest}|Remove-Item -Force
[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8
$installText=[IO.File]::ReadAllText((Join-Path $PWD 'black-ui-621-install.ps1')).Replace('black-ui-621','section-632').Replace('15.4.621','15.4.632')
[IO.File]::WriteAllText((Join-Path $PWD 'section-632-install.ps1'),$installText)
& .\section-632-install.ps1
if(!$?){throw 'Install verification failed'}
& .\ai-workflow.ps1 -Action complete-task -TaskId 'section-632' -Summary 'Installed632: black workspace depth, consistent Network title, visible UI motion label and readable helper/status text. Six suites and gated release/install passed.' -NextAction 'Installed632 visual pass complete. Next UI batch: inspect GPU, Startup and storage screens for remaining inconsistent controls. Source633 is automatic bump.' -Done @('Black workspace depth and unified section header','Helper text and UI motion label improved','Fresh632 six smoke suites and gated release installed') -Evidence @('artifacts\section-632\validation-result.json','artifacts\section-632\deployment-result.json','artifacts\section-632\release-result.json')
& .\update-project-state.ps1 -Reason 'section-632-installed-verified'
Write-Output 'SECTION632_RELEASE_INSTALLED_OK'
