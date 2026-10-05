$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\ui-audit-629'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.629*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Verified agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
$setups=@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File)
if($setups.Count -ne 1){throw 'Multiple Desktop installers'}
$entry=[Environment]::NewLine+'CURRENT629: User authorized cold navigation and popup/control readability plus shared state consistency. Installed628 measured cold3-11ms warm0-2ms; fresh629 cold3-12ms warm0-1ms (single run, no global speedup claim). Shared TweakUiState used by classic create/refresh and virtual refresh. Priority busy,error,unavailable,restart,dirty,verified current. Verified only with readback timestamp and no error/pending/restart. Disabled standard/accent buttons readable both dark/light; contrast dark6.98:1, regression minimum4.5. Seven UI state fixtures validated classic+virtual, create+refresh. Tests six themes, three sizes, dialogs, navigation, performance and12captures passed. First compile failed only test fixture required/init fields; corrected and all repeated. Full gated release and install passed, payload/agent verified PID'+$d.processId+'. Desktop629 only '+$r.sha256+'. Source auto630. App updates block remains removed. Actual screenshots629power/overview/dialog captured; dialog smoke modifies underlying overview fixture labels, normal overview screenshot confirms current changelog629first. Patch helper non-idempotent. Reports artifacts/ui-audit-629. No pending implementation/release work.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$text=[IO.File]::ReadAllText($handoff).Replace('15.4.628','15.4.629').Replace('15.4.629; this','15.4.630; this')
[IO.File]::WriteAllText($handoff,$text)
& .\resume-state.ps1 -Action refresh
Write-Output ($r|ConvertTo-Json -Compress)
Write-Output ($d|ConvertTo-Json -Compress)
Write-Output 'UI629_FINAL_VERIFIED'
