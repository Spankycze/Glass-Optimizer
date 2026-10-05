$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\modern-636'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.636*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
if(@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File).Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT636: User requested more substantial modern UI redesign. Removed shared196px heading side-column: section header and separator now above full-width content. Workspace panels radius12 padding22, gradient14/5/1. Page intro has bottom separator, header actions responsive into second row below1180width; Network wide header actions now right toolbar. Sidebar selected full rounded8 material30/13 with neutral top edge68/34 instead blue left line. Colored icons and stronger635 fonts preserved. Added GPU/Startup/Storage to both-theme capture targets (18 images total). Actual636 Network/GPU/Settings captures checked: no overlap/clipping. Fresh build-test and six capture/theme/startup/size/navigation/dialog suites passed, full gated release/install verified; healthy HID fan agent PID'+$d.processId+'. Desktop636 only SHA256 '+$r.sha256+'. Source637 auto bump untested. First helper attempt failed due PS alias R, no product writes; renamed ReplaceChecked. First build CS1503 due Grid setter UIElement, fixed using Border FrameworkElement action wrapper, then all checks passed. Helpers modern-636-edit non-idempotent. Next design scope if requested: Overview and section-specific tool composition. No performance speed claim.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$t=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.636','Source advanced automatically to 15.4.637').Replace('latest verified app release is 15.4.635','latest verified app release is 15.4.636')
[IO.File]::WriteAllText($handoff,$t)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'MODERN636_FINAL_VERIFIED'
