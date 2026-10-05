param(
  [string]$Task='',
  [string]$Area='general',
  [string]$Validation='build-test.ps1',
  [switch]$ResumeOnly
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$workflow=Join-Path $root 'ai-workflow.ps1'
if($ResumeOnly -or [string]::IsNullOrWhiteSpace($Task)){
  & $workflow -Action resume
  exit $LASTEXITCODE
}
$id=('task-'+(Get-Date -Format 'yyyyMMddHHmmss'))
& $workflow -Action start-task -TaskId $id -Title $Task -Area $Area -ValidationRequired @($Validation)
& $workflow -Action checkpoint -TaskId $id -Phase 'before-validation' -ValidationRequired @($Validation)
$cmd="powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Validation"
try {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'dc-smart.ps1') $cmd
  $validationExit=$LASTEXITCODE
  if($validationExit -ne 0){throw "validation exit=$validationExit"}
  & $workflow -Action checkpoint -TaskId $id -Phase 'validated' -ValidationRequired @($Validation)
  & $workflow -Action complete-task -TaskId $id
  Write-Output ('AGENT_LOOP_OK '+$id)
} catch {
  $err=$_.Exception.Message
  & $workflow -Action fail-task -TaskId $id -ErrorKey $err
  & $workflow -Action checkpoint -TaskId $id -Phase 'failed' -ErrorKey $err -ValidationRequired @($Validation)
  throw
}
