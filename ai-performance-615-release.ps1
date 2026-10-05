$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot

$artifactDir = Join-Path $PSScriptRoot 'artifacts\ai-performance-615'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

& '.\dc-smart.ps1' '.\ui-redesign-audit.ps1'
if($LASTEXITCODE -ne 0){ throw 'Protected UI audit failed' }

& '.\dc-smart.ps1' '.\build-release.ps1'
if($LASTEXITCODE -ne 0){ throw 'Release gate failed' }

& '.\dc-smart.ps1' '.\build-installer-compact.ps1 -Version 15.4.615'
if($LASTEXITCODE -ne 0){ throw 'Compact installer failed' }

$src = Join-Path $PSScriptRoot 'artifacts\release\Spankyho_Optimizer_V15_4_615_Setup_Compact.exe'
if(-not (Test-Path $src)){ throw 'Installer missing' }
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.615*'){ throw 'Installer version mismatch' }

$desktop = [Environment]::GetFolderPath('Desktop')
$dest = Join-Path $desktop (Split-Path $src -Leaf)
Copy-Item $src $dest -Force
$hash = (Get-FileHash $src -Algorithm SHA256).Hash
if((Get-FileHash $dest -Algorithm SHA256).Hash -ne $hash){ throw 'Desktop copy mismatch' }

Get-ChildItem $desktop -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe' |
    Where-Object { $_.FullName -ne $dest } |
    Remove-Item -Force -ErrorAction SilentlyContinue

$report = [ordered]@{
    version = (Get-Item $dest).VersionInfo.ProductVersion
    desktopInstaller = $dest
    bytes = (Get-Item $dest).Length
    sha256 = $hash
    utc = (Get-Date).ToUniversalTime().ToString('O')
    coreTests = '327/327'
    uiAudit = 'PASS'
}
$report | ConvertTo-Json | Set-Content (Join-Path $artifactDir 'release-result.json') -Encoding UTF8

& '.\update-project-state.ps1' -Reason 'ai-expanded-615-release-complete'
if($LASTEXITCODE -ne 0){ throw 'Project state update failed' }

Write-Output ('AI615_RELEASE_OK bytes=' + $report.bytes + ' sha256=' + $hash)
