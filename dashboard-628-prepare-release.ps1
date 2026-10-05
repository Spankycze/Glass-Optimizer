$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$text=[IO.File]::ReadAllText((Join-Path $PWD 'settings-cleanup-627-release.ps1')).Replace('settings-cleanup-627','dashboard-628').Replace('15.4.627','15.4.628').Replace('V15_4_627','V15_4_628').Replace('SETTINGS627','DASHBOARD628')
[IO.File]::WriteAllText((Join-Path $PWD 'dashboard-628-release.ps1'),$text)
Write-Output 'RELEASE628_PREPARED'
