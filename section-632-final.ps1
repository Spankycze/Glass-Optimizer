$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\section-632'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.632*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
$setups=@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File)
if($setups.Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT632: User asked continue and objected to stopping. Visual audit of Network/Settings/Power showed mismatched network header, invisible MTU text due to button fill used as foreground, unlabeled motion switch and flat workspace surfaces. Shared BuildWorkspaceSurface now cached black10/3/0 gradient with edge, radius8 and16/20padding; Network title32Light like other pages. Text secondary replaces button-fill foreground in PowerNetwork/PowerPlan/Spicetify/Winget/AiProgress. Settings explicit motion label and accessible name retained verified toggle handler. No Overview layout change. Fresh632 build-test and six visual/theme/startup/size/navigation/dialog suites passed; actual12captures checked Network and Settings. Full gated release/install verified payload and healthy HID fan agent PID'+$d.processId+'. OnlyDesktop632 installer '+$r.sha256+'. Source633 automatic bump untested. Reports artifacts/section-632. Fix helper non-idempotent do not rerun. This batch completed. Next UI audit targets GPU/Startup/Storage; not claimed as finished redesign of entire application.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$text=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.632','Source advanced automatically to 15.4.633').Replace('latest verified app release is 15.4.631','latest verified app release is 15.4.632')
[IO.File]::WriteAllText($handoff,$text)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'SECTION632_FINAL_VERIFIED'
