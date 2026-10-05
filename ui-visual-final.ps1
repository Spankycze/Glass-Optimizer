$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$exe=& '.\ui-select-build.ps1'
foreach($flag in @('--visual-layout-smoke','--visual-size-smoke','--page-scroll-input-smoke','--release-ui-smoke')){
 $p=Start-Process $exe -ArgumentList $flag -PassThru -WorkingDirectory (Split-Path $exe)
 if(-not $p.WaitForExit(120000)){Stop-Process $p.Id -Force;throw "$flag timeout"}
 if($p.ExitCode -ne 0){throw "$flag exit=$($p.ExitCode)"}
 Write-Output "UI_FINAL_OK $flag"
}
& '.\ui-redesign-audit.ps1'
# ui-redesign-audit is a PowerShell script and throws on a protected hash mismatch.
Write-Output 'UI_FINAL_VALIDATION_OK'