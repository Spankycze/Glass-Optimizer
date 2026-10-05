$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\fonts-634'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.634*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
$setups=@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File)
if($setups.Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT634: User requested better font. Verified SegUIVar.ttf installed. App.xaml ContentControlThemeFontFamily and ControlContentThemeFontFamily now Variable Text with Segoe UI fallback, implicit TextBlock style family. Page titles30SemiBold Variable Display instead32Light in shared workspace, Network and Overview; dialog24SemiBold Display instead25Light. Gauge digits34Normal insteadLight, action labels12.5 and explicit Variable Text. Explicit FontIcon and monospace families untouched. Fresh634 build-test and six visual/theme/startup/size/navigation/dialog suites passed; Network/dialog actual captures checked. Full gated release/install payload verified and healthy HID fan agent PID'+$d.processId+'. Desktop634 only '+$r.sha256+'. Source635 automatic bump untested. Reports artifacts/fonts-634. Fix helper non-idempotent do not rerun. Typography batch complete; next UI audit GPU/Startup/Storage.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$text=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.634','Source advanced automatically to 15.4.635').Replace('latest verified app release is 15.4.633','latest verified app release is 15.4.634')
[IO.File]::WriteAllText($handoff,$text)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'FONTS634_FINAL_VERIFIED'
