$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$files=@('resume-state.ps1','ai-workflow.ps1','continue-project.ps1','update-project-state.ps1','AGENTS.md','SESSION_HANDOFF.md')
$done=@('Current resume state saves exact stop point and next action.','Workflow transitions refresh state; explicit checkpoint preserves completed and pending work.','History archived separately; user preferences retained in AGENTS.md.','Installed app15.4.628 distinguished from automatically bumped source15.4.629.','Old613-621 build status flagged historical, never current proof.','Isolated regression passed: refresh, task change, missing evidence, corrupt state and syntax.','App628 UI/telemetry/changelog release and installation already complete.')
$evidence=@('artifacts\continuity-memory\regression.log','artifacts\dashboard-628\validation-result.json','artifacts\dashboard-628\release-result.json','artifacts\dashboard-628\deployment-result.json')
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'continuity-memory' -Phase 'continuity-verified' -Summary 'Continuity scripts implemented and isolated regression passed; no app source changes.' -NextAction 'Complete continuity task, then verify resume output after task closure.' -Done $done -Pending 'Final resume readback' -FilesTouched $files -Evidence $evidence
if(!$?){throw 'Checkpoint failed'}
& .\ai-workflow.ps1 -Action complete-task -TaskId 'continuity-memory' -Summary 'Continuity improvements complete. Latest app release628 is installed; UI and telemetry work finished. No unfinished implementation or release work.' -NextAction 'Read latest user request and start a new concrete task; do not rebuild or repeat completed628 changes.' -Done $done -Pending @() -FilesTouched $files -Evidence $evidence
if(!$?){throw 'Completion failed'}
& .\update-project-state.ps1 -Reason 'continuity-memory-complete'
& .\continue-project.ps1 -Mode Quiet
if(!$?){throw 'Resume failed'}
$s=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $PWD '.ai-state\RESUME_STATE.json')))
if($s.currentTask -or @($s.pending).Count -or $s.done.Count -lt 7 -or $s.nextAction -notlike 'Read latest user request*'){throw 'Final state lost progress'}
if($s.latestRelease.version -ne '15.4.628' -or !$s.latestRelease.payloadVerified){throw 'Release evidence missing'}
$md=[IO.File]::ReadAllText((Join-Path $PWD 'RESUME.md'))
if($md -notmatch 'Source: 15.4.629' -or $md -notmatch 'Installed: 15.4.628'){throw 'Readable version lines missing'}
'CONTINUITY_FINAL_READBACK_OK task=none pending=0 completed=7 release=628 source=629'|Tee-Object (Join-Path $PWD 'artifacts\continuity-memory\final-readback.log')
