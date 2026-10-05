$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\changelog-630'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.630*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
$setups=@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File)
if($setups.Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT630: Real changelog corruption confirmed after cached Overview refresh. CompleteOverviewDashboardAsync reused Changelog caption and rows for tweak batch history; earlier629 handoff fixture-only explanation was incorrect. Regression invokes actual snapshot/completion and compares release rows, old code failed CHANGELOG_REFRESH_REGRESSION before fix. Separate changelog caption, removed batch writes to release rows, stale page/cancellation guards. Green fresh630 build-test and visual capture/theme/startup-isolated/visual-size/navigation-tail/dialog-lifecycle passed. Actual dialog screenshot confirms630 release rows remain stable. Full gated release and silent install passed, installed payload verified and live healthy fan agent PID'+$d.processId+'. Desktop630 only '+$r.sha256+'. Source631 automatic bump, not a tested release. All requested work complete. Reports artifacts/changelog-630; red-regression.log records failing baseline. No pending implementation/release work.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$text=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.630','Source advanced automatically to 15.4.631').Replace('latest verified app release is 15.4.629','latest verified app release is 15.4.630')
[IO.File]::WriteAllText($handoff,$text)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'CHANGELOG630_FINAL_VERIFIED'
