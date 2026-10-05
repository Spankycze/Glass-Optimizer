$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\telemetry-631'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.631*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
$setups=@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File)
if($setups.Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT631: RAM summary used initial hardware snapshot while gauge refreshed every2sec, screenshot630 had48/49 mismatch. Actual telemetry loop regression marked stale summary then waited for live update; old code failed RAM_TELEMETRY_REGRESSION before fix. Live callback now updates RAM used/total/percent and gauge from same native sample; latest sample retained across late snapshot completion, cleared on page invalidation. Fresh631 build-test plus visual-capture/theme/startup-isolated/visual-size/navigation-tail/dialog-lifecycle passed, real loop and late snapshot assertions green. Overview screenshot631 shows57% in both summary/gauge. Full gated release/install verified payload, live healthy HID fan agent PID'+$d.processId+'. OnlyDesktop631 installer '+$r.sha256+'. Source632 automatic bump untested. All work complete; artifacts/telemetry-631 contains red and green evidence. Fix helper non-idempotent, do not rerun. No pending implementation/release work.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$text=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.631','Source advanced automatically to 15.4.632').Replace('latest verified app release is 15.4.630','latest verified app release is 15.4.631')
[IO.File]::WriteAllText($handoff,$text)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'RAM631_FINAL_VERIFIED'
