$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\weight-635'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.635*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
if(@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File).Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT635: User rejected thin overall font634 and unchanged sidebar. Implicit TextBlock weight Medium now covers body text, explicit FontWeights.Normal replaced with Medium in changed MainWindow partials (sixfiles manifest). Sidebar title13SemiBold from12Normal; material action buttons all SemiBold. Kept Variable family and634strongpage/dialog headings. Actual635Network screenshot visibly thicker sidebar/body/action labels; no clipped sidebar names, longer Program/Launcher wraps2lines. Fresh635 build-test and six visual/theme/startup/size/navigation/dialog suites passed. Full gated release/install verified payload and healthy HID fan agent PID'+$d.processId+'. Desktop635 only '+$r.sha256+'. Source636 automatic bump untested. Reports artifacts/weight-635 and changed-files.json. Fix helper non-idempotent. Weight correction complete; next requested UI audit GPU/Startup/Storage.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$text=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.635','Source advanced automatically to 15.4.636').Replace('latest verified app release is 15.4.634','latest verified app release is 15.4.635')
[IO.File]::WriteAllText($handoff,$text)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'WEIGHT635_FINAL_VERIFIED'
