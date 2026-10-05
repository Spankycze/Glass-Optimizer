$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$fixture=Join-Path $PWD 'artifacts\continuity-memory\fixture'
if(Test-Path $fixture){Remove-Item $fixture -Recurse -Force}
New-Item (Join-Path $fixture '.ai-state') -ItemType Directory -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'Directory.Build.props'),'<Project><SpankyVersion>15.4.629</SpankyVersion></Project>')
function SetTask($id,$status){
 [IO.File]::WriteAllText((Join-Path $fixture '.ai-state\TASK_STATE.json'),(@{currentTask=$id;tasks=@(@{id=$id;status=$status;title='Fixture task'})}|ConvertTo-Json -Depth 5))
}
SetTask 'fixture-one' 'in-progress'
& .\resume-state.ps1 -Root $fixture -Action save -Summary 'Changed telemetry, not yet tested.' -NextAction 'Run targeted telemetry test.' -Done 'Patch saved' -Pending 'Tests' -FilesTouched 'fake.cs' -Evidence 'missing.log'|Out-Null
$first=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $fixture '.ai-state\RESUME_STATE.json')))
if($first.nextAction -ne 'Run targeted telemetry test.' -or $first.pending[0] -ne 'Tests'){throw 'Save roundtrip failed'}
& .\resume-state.ps1 -Root $fixture -Action refresh|Out-Null
$second=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $fixture '.ai-state\RESUME_STATE.json')))
if($second.nextAction -ne $first.nextAction){throw 'Refresh lost exact next step'}
if(!(@($second.warnings) -match 'Evidence missing')){throw 'Missing evidence not reported'}
SetTask 'fixture-two' 'in-progress'
& .\resume-state.ps1 -Root $fixture -Action refresh|Out-Null
$third=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $fixture '.ai-state\RESUME_STATE.json')))
if($third.nextAction -eq $first.nextAction -or @($third.pending).Count){throw 'Old task progress leaked'}
if(@(Get-Content (Join-Path $fixture '.ai-state\resume-history.jsonl')).Count -lt 3){throw 'History missing'}
# Invalid state must fail visibly instead of silently replacing previous progress.
[IO.File]::WriteAllText((Join-Path $fixture '.ai-state\RESUME_STATE.json'),'{broken')
$rejected=$false
try{& .\resume-state.ps1 -Root $fixture -Action refresh|Out-Null}catch{$rejected=$true}
if(!$rejected){throw 'Corrupt state was silently replaced'}
foreach($script in @('resume-state.ps1','ai-workflow.ps1','continue-project.ps1','update-project-state.ps1')){
 $tokens=$null;$errors=$null
 [Management.Automation.Language.Parser]::ParseFile((Join-Path $PWD $script),[ref]$tokens,[ref]$errors)|Out-Null
 if($errors.Count){throw ('Syntax errors in '+$script+': '+$errors)}
}
'CONTINUITY_REGRESSION_OK save=true refresh=true taskSwitch=true missingEvidence=true corruptStateRejected=true syntax=true'|Tee-Object (Join-Path $PWD 'artifacts\continuity-memory\regression.log')
