$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$t=[IO.File]::ReadAllText((Join-Path $PWD 'simple-637-final.ps1'))
$t=$t.Substring(0,$t.IndexOf('& .\ai-workflow.ps1 -Action complete-task'))
$t=$t.Replace('simple-637','minimal-638').Replace('15.4.637','15.4.638').Replace('15.4.636','15.4.637')
$t=$t.Replace('Three fewer always-visible UI nodes per workspace surface; no page/navigation composition animations or their dispatcher callbacks.','Open workspace sections with one bottom separator; no large fill/outer frame or gradient brush; compact navigation and headings. Immediate page switching from637 retained.')
$t+=@'
& .\ai-workflow.ps1 -Action complete-task -TaskId 'minimal-638' -Summary 'Installed638: large workspace cards removed, open black sections with single separators, title26/section16, compact sidebar, redundant DNS banner removed. Six suites and gated release/install passed; fan agent healthy/HID verified; installed Release637/638 three-sample comparison saved.' -NextAction 'Minimal638 UI correction complete. Source639 automatic bump untested. Next user-directed design iteration, preserve Overview hardware/header. Do not claim overall speedup from small noisy measurements.' -Done @('Large outer workspace cards removed','Compact headings and sidebar','Redundant network banner removed','Six suites and gated release installed','Release timings compared') -Evidence @('artifacts\minimal-638\validation-result.json','artifacts\minimal-638\deployment-result.json','artifacts\minimal-638\release-result.json','artifacts\minimal-638\comparison.json','artifacts\minimal-638\screens\dark-network.png')
$entry=[Environment]::NewLine+'CURRENT638: User rejected637 as nearly identical; now structural visual correction. Shared workspace no fill/outer framed card/radius: only bottom1px separator, padding0/10/0/18 margin8. Title26 section16, sidebar padding8/3 andmin32; colored icons readableSemiBold retained. Removed redundant DNS&MTU flat banner. Network capture visibly open black sections, full MTU input/actions visible, sidebar PowerShell/Cursor visible. Overview hardware/header preserved. Removed unused workspace gradient brush cache and reset. Six capture/theme/startup/size/navigation/dialog suites + build-test + full gated release passed. Installed638 payload/HID/fanagent verified PID'+$d.processId+'. Desktop only638 SHA256 '+$r.sha256+'. Source639 auto bump untested. Installed Release637/638 three samples comparison.json; no broad speed claim, view simplification retained for explicit user request. minimal-638-edit non-idempotent. Task complete.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$v=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.638','Source advanced automatically to 15.4.639').Replace('latest verified app release is 15.4.637','latest verified app release is 15.4.638')
[IO.File]::WriteAllText($handoff,$v)
& .\update-project-state.ps1 -Reason 'minimal-638-installed-measured'
& .\resume-state.ps1 -Action refresh
Write-Output 'MINIMAL638_FINAL_VERIFIED'
'@
[IO.File]::WriteAllText((Join-Path $PWD 'minimal-638-final.ps1'),$t)
Write-Output 'MINIMAL638_FINAL_HELPER_READY'
