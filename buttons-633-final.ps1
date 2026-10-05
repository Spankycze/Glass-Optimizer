$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\buttons-633'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.633*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
$setups=@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File)
if($setups.Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT633: User rejected outline-only text buttons. Shared ApplyMaterialButtonAppearance now solid black26/13 secondary and42/22 primary gradient surfaces, neutral58/26 and82/38 edges, radius7. Applied QuickButton via pill factory, PrimaryQuickButton and both styled-dialog action factories. Shared theme Button hover36/21 and pressed14/9 neutral surfaces, primary own brighter hover/darker pressed resources. Disabled readable resources retained. Light theme uses filled light-neutral buttons. No action handlers changed; no sidebar/Overview layout change. Fresh633 build-test plus six visual/theme/startup/size/navigation/dialog suites passed; actual Network/dialog screenshots show full filled surfaces without colored outlines. Full gated release/install payload verified and healthy HID fan agent PID'+$d.processId+'. Desktop633 only '+$r.sha256+'. Source634 automatic bump untested. Reports artifacts/buttons-633. Fix helper non-idempotent do not rerun. Button redesign batch complete; next requested UI audit GPU/Startup/Storage.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$text=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.633','Source advanced automatically to 15.4.634').Replace('latest verified app release is 15.4.632','latest verified app release is 15.4.633')
[IO.File]::WriteAllText($handoff,$text)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'BUTTONS633_FINAL_VERIFIED'
