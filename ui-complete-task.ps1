$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$r=Get-Content '.\artifacts\ui-neutral-redesign\release-result.json' -Raw | ConvertFrom-Json
if(-not(Test-Path $r.desktopInstaller)){throw 'Desktop installer missing'}
if((Get-FileHash $r.desktopInstaller -Algorithm SHA256).Hash -ne $r.sha256){throw 'Desktop installer hash mismatch'}
& '.\ai-workflow.ps1' -Action checkpoint -TaskId neutral-ui-redesign -Phase release-passed -FilesTouched @('WORK_PROGRESS.md','docs\superpowers\plans\2026-10-03-neutral-ui.md')
& '.\ai-workflow.ps1' -Action complete-task -TaskId neutral-ui-redesign
& '.\continue-project.ps1' -Mode Quiet -RefreshState -RefreshIndex
Write-Output ('FINAL_INSTALLER_VERIFIED '+$r.version+' '+$r.sizeMiB+' MiB')
