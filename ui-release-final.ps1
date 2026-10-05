$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& '.\dc-smart.ps1' '.\build-release.ps1'
if($LASTEXITCODE -ne 0){throw 'Release gate failed'}
& '.\dc-smart.ps1' '.\build-installer-compact.ps1 -Version 15.4.603'
if($LASTEXITCODE -ne 0){throw 'Compact installer failed'}
$compact=Join-Path $PSScriptRoot 'artifacts\release\Spankyho_Optimizer_V15_4_603_Setup_Compact.exe'
if(-not(Test-Path $compact)){throw 'Compact output missing'}
$desktop=[Environment]::GetFolderPath('Desktop')
$target=Join-Path $desktop (Split-Path $compact -Leaf)
Copy-Item $compact $target -Force
if((Get-FileHash $compact).Hash -ne (Get-FileHash $target).Hash){throw 'Desktop copy mismatch'}
# Remove only superseded installers, after the verified replacement is on Desktop.
Get-ChildItem $desktop -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe' | Where-Object {$_.FullName -ne $target} | Remove-Item -Force
$report=[ordered]@{version=(Get-Item $target).VersionInfo.ProductVersion;desktopInstaller=$target;sizeMiB=[Math]::Round((Get-Item $target).Length/1MB,2);sha256=(Get-FileHash $target).Hash;utc=(Get-Date).ToUniversalTime().ToString('O')}
$report|ConvertTo-Json|Set-Content 'artifacts\ui-neutral-redesign\release-result.json'
Write-Output 'NEUTRAL_UI_RELEASE_OK desktopInstaller=15.4.603'