$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$exe=& '.\ui-select-build.ps1'
foreach($flag in @('--visual-layout-smoke','--visual-size-smoke','--page-scroll-input-smoke','--release-ui-smoke')){
 $p=Start-Process $exe -ArgumentList $flag -PassThru -WorkingDirectory (Split-Path $exe)
 if(-not $p.WaitForExit(120000)){Stop-Process $p.Id -Force;throw "$flag timeout"}
 if($p.ExitCode -ne 0){throw "$flag exit=$($p.ExitCode)"}
 Write-Output "UI_FINAL_OK $flag"
}
& '.\ui-holistic-audit.ps1'
if(-not $?){throw 'Holistic UI audit failed'}
& '.\black-surface-audit.ps1'
if(-not $?){throw 'Black surface audit failed'}
Write-Output 'UI_FINAL_VALIDATION_OK'