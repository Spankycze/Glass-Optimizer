$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$backup=Join-Path $PWD 'artifacts\continuity-memory\before'
New-Item $backup -ItemType Directory -Force|Out-Null
foreach($f in @('ai-workflow.ps1','continue-project.ps1','update-project-state.ps1','AGENTS.md','SESSION_HANDOFF.md')){Copy-Item $f (Join-Path $backup $f) -Force}
$file=Join-Path $PWD 'ai-workflow.ps1'
$text=[IO.File]::ReadAllText($file)
$text=$text.Replace("  [switch]"+'$RefreshIndex', "  [string]"+'$Summary'+"='',"+[Environment]::NewLine+"  [string]"+'$NextAction'+"='',"+[Environment]::NewLine+"  [string[]]"+'$Done'+"=@(),"+[Environment]::NewLine+"  [string[]]"+'$Pending'+"=@(),"+[Environment]::NewLine+"  [string[]]"+'$Evidence'+"=@(),"+[Environment]::NewLine+"  [switch]"+'$RefreshIndex')
$text+=[Environment]::NewLine+@'
# Continuity is updated on every workflow transition, without stale build promotion.
$continuity=Join-Path $root 'resume-state.ps1'
if(Test-Path $continuity){
 if($Action -eq 'resume'){& $continuity -Action show}
 elseif($Action -in @('start-task','checkpoint','complete-task','fail-task')){
  if($Summary -and $NextAction){& $continuity -Action save -Summary $Summary -NextAction $NextAction -Done $Done -Pending $Pending -FilesTouched $FilesTouched -Evidence $Evidence}
  else{& $continuity -Action refresh}
 }
}
'@
# Existing build-test status may describe a different version. Never promote it.
$text=$text.Replace("if("+ '$status -and $status.entries.'+"'build-test'.outcome -eq 'OK')","if("+ '$status -and $status.entries.'+"'build-test'.outcome -eq 'OK' -and "+'$status.entries.'+"'build-test'.version -eq (Get-Version))")
[IO.File]::WriteAllText($file,$text)
$file=Join-Path $PWD 'continue-project.ps1'
$text=[IO.File]::ReadAllText($file)
$text=$text.Replace('$sw=[Diagnostics.Stopwatch]::StartNew()',@'
$sw=[Diagnostics.Stopwatch]::StartNew()
& (Join-Path $root 'resume-state.ps1') -Action refresh
'@)
$text=$text.Replace("-Reason 'continue-project' -Light","-Reason 'continue-project' -Light")
# Keep legacy status explicitly historical; exact next action comes from RESUME.
$text=$text.Replace("'[BUILD] fast='","'[HISTORICAL_BUILD_STATUS] fast='")
$text=$text.Replace("Resume from PROJECT_STATE.md; deep logs/index only when needed.","Resume from RESUME.md first, then PROJECT_STATE.md; never use historical logs as current evidence.")
[IO.File]::WriteAllText($file,$text)
$file=Join-Path $PWD 'update-project-state.ps1'
$text=[IO.File]::ReadAllText($file)
$text=$text.Replace('$notes=@(Get-Content $work -Tail 24 -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object {$_} | Select-Object -Last 12)', '$notes=@(Get-Content $work -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object {$_} | Select-Object -First 8)')
$text=$text.Replace("$"+"lines+='# PROJECT_STATE'", "$"+"lines+='# PROJECT_STATE'"+[Environment]::NewLine+"$"+"lines+='Read RESUME.md first for current task, next action and verified release evidence.'")
$text=$text.Replace('Fresh build/test status comes from .ai-state\status.json, not historical build logs.','Prefer current release/deployment/validation reports in RESUME.md; .ai-state\status.json can be historical and must match the tested version.')
[IO.File]::WriteAllText($file,$text)
Copy-Item 'SESSION_HANDOFF.md' 'SESSION_HISTORY.md' -Force
[IO.File]::WriteAllText((Join-Path $PWD 'SESSION_HANDOFF.md'),@'
# Current handoff
Read RESUME.md first. It contains the current task, exact next action, completed and pending work, actual source/installed versions and evidence.
Then read PROJECT_STATE.md for code/build context.
Older session entries are preserved in SESSION_HISTORY.md. Do not read all history on each continuation.
The latest verified app release is 15.4.628. Source advanced automatically to 15.4.629; this is not a verified new release.
Update progress through ai-workflow.ps1 checkpoint with Summary, NextAction, Done, Pending, FilesTouched and Evidence.
On task completion, record concrete finished behavior and state whether any implementation or release work remains.
Never infer test freshness from a legacy OK status, or claim arbitrary cross-chat memory. The on-disk state is authoritative after reading it.
'@)
$rules=@'
# Spankyho Optimizer Agent Rules
- Work through normal ChatGPT + DC Remote; do not use Work mode.
- On continuation run continue-project.ps1, then read RESUME.md FIRST, PROJECT_STATE.md second and compact SESSION_HANDOFF.md third.
- RESUME.md / .ai-state/RESUME_STATE.json are current work. SESSION_HISTORY.md and WORK_PROGRESS.md are history; read only relevant excerpts.
- Before editing: start-task with concrete Summary and NextAction. After each meaningful batch and before ending a turn: checkpoint with Summary, NextAction, Done, Pending, FilesTouched, Evidence.
- Save failed attempts and exact recovery action; never silently skip pending work. Checkpoint before long build/install operations.
- complete-task must record final behavior, validation evidence, released/installed version and any remaining next action.
- Distinguish current SOURCE version, last TESTED version, RELEASED version and INSTALLED version. Automatic version bumps are not tested builds.
- Verify resume evidence exists. Never promote old status.json OK entries to current validation.
- Use PROJECT_INDEX.json for code navigation before broad recursive scans; do not dump its whole one-line JSON.
- Use TASK_STATE.json / CHECKPOINT.json through ai-workflow.ps1 for multi-step work.
- Load only the skill relevant to the current task from skills/.
- For verbose commands use dc-smart.ps1; invoke custom ps1 explicitly in PowerShell.
- Measure before optimizing; keep only measurable improvements.
- Prefer safe C#/.NET/native read paths over new PowerShell/WMI subprocesses when practical.
- Every mutation path needs timeout/cancellation/read-back/rollback where relevant.
- Validate significant app changes with build-fast, targeted smoke and build-test; resolve the FRESH staged EXE after locked builds.
- Update PERFORMANCE_BASELINE.md only from fresh measured runs.
- Keep releases gated by build-release.ps1 and leave only the current Desktop installer.
- User requires actual screenshots embedded in chat, EXE installers only on Desktop, no GitHub/source ZIP, no repeated confirmations.
- Current UI: deep black surfaces, restrained blue controls, colored hardware pictures/icons; app-update Settings block removed.
- Preserve intentional session0 bridge; update/restart the actual fan agent after installation. Do not kill all RuntimeHosts.
'@
[IO.File]::WriteAllText((Join-Path $PWD 'AGENTS.md'),$rules)
& .\ai-workflow.ps1 -Action start-task -TaskId 'continuity-memory' -Title 'Reliable current-work continuity' -Area 'workflow' -Summary 'Improving resume state and workflow persistence.' -NextAction 'Verify isolated continuity regression and resume readback.'
Write-Output 'CONTINUITY_UPGRADE_APPLIED'
