$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$artifactDir=Join-Path $PSScriptRoot 'artifacts\black-ui-621'
& .\build-release.ps1 *> (Join-Path $artifactDir 'release.log')
if(!$?){throw 'Release gate failed'}
$releaseLog=[IO.File]::ReadAllText((Join-Path $artifactDir 'release.log'))
if($releaseLog -notmatch 'RELEASE_OK|CACHE_HIT'){throw 'Release completion marker missing'}
& .\build-installer-compact.ps1 -Version 15.4.621 *> (Join-Path $artifactDir 'installer.log')
if(!$?){throw 'Compact installer failed'}
$src=Join-Path $PSScriptRoot 'artifacts\release\Spankyho_Optimizer_V15_4_621_Setup_Compact.exe'
if(!(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.621*'){throw 'Installer version mismatch'}
$desktop=[Environment]::GetFolderPath('Desktop')
$dest=Join-Path $desktop (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop copy mismatch'}
Get-ChildItem $desktop -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe'|Where-Object {$_.FullName -ne $dest}|Remove-Item -Force
$report=[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}
$report|ConvertTo-Json|Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8
& .\ai-workflow.ps1 -Action complete-task -TaskId 'black-ui-621'
& .\update-project-state.ps1 -Reason 'black-ui-621-release-complete'
Write-Output ("BLACK_UI_RELEASE_OK bytes="+$report.bytes+" installer="+$dest)
