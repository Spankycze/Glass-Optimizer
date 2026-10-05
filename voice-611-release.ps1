$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& '.\dc-smart.ps1' '.\ui-redesign-audit.ps1'
if($LASTEXITCODE -ne 0){throw 'Protected UI audit failed'}
& '.\dc-smart.ps1' '.\build-release.ps1'
if($LASTEXITCODE -ne 0){throw 'Release gate failed'}
& '.\dc-smart.ps1' '.\build-installer-compact.ps1 -Version 15.4.611'
if($LASTEXITCODE -ne 0){throw 'Compact installer failed'}
$src=Join-Path $PSScriptRoot 'artifacts\release\Spankyho_Optimizer_V15_4_611_Setup_Compact.exe'
$desktop=[Environment]::GetFolderPath('Desktop')
$dest=Join-Path $desktop (Split-Path $src -Leaf)
if(-not(Test-Path $src)){throw 'Installer missing'}
if((Get-Item $src).VersionInfo.ProductVersion -notlike '15.4.611*'){throw 'Installer version mismatch'}
Copy-Item $src $dest -Force
$hash=(Get-FileHash $src).Hash
if((Get-FileHash $dest).Hash -ne $hash){throw 'Desktop copy mismatch'}
Get-ChildItem $desktop -File -Filter 'Spankyho_Optimizer_V*_Setup*.exe' |
    Where-Object {$_.FullName -ne $dest} | Remove-Item -Force
$report=[ordered]@{version=(Get-Item $dest).VersionInfo.ProductVersion;desktopInstaller=$dest;bytes=(Get-Item $dest).Length;sha256=$hash;utc=(Get-Date).ToUniversalTime().ToString('O')}
$report|ConvertTo-Json|Set-Content 'artifacts\voice-611\release-result.json' -Encoding UTF8
& '.\ai-workflow.ps1' -Action checkpoint -Phase 'release-passed' -FilesTouched 'src\SpankyhoOptimizer\Services\GeminiSegmentTranscriptionService.cs','tools\Spankyho.QualityTests\GeminiTranscriptionResponseTests.cs','WORK_PROGRESS.md' -ValidationRequired 'release','compact-installer'
& '.\ai-workflow.ps1' -Action complete-task
& '.\update-project-state.ps1' -Reason 'voice-parser-611-release-complete'
Write-Output ("VOICE611_RELEASE_OK bytes="+$report.bytes+" sha256="+$hash)




