$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$files=@(
  'continue-project.ps1','update-project-state.ps1','project-index.ps1',
  'dc-smart.ps1','dev-check.ps1','fast-fingerprint.ps1','quality-audit.ps1',
  'flaky-audit.ps1','rollback-matrix.ps1','promote-fuzz-regression.ps1','regression-store.ps1','run-fuzz.ps1',
  'visual-layout-audit.ps1','visual-size-audit.ps1','page-scroll-input-audit.ps1','visual-regression.ps1',
  'keyboard-accessibility-audit.ps1','keyboard-activation-audit.ps1','section-tab-audit.ps1','navigation-race-audit.ps1',
  'change-impact.ps1','smart-quality.ps1','background-interference-audit.ps1','ui-thread-budget-audit.ps1',
  'cache-correctness-audit.ps1','performance-diagnose.ps1','cleanup-audit.ps1','analyzer-regression-gate.ps1','memory-retention-audit.ps1','build-speed-audit.ps1','xaml-reuse-audit.ps1','build-installer-compact.ps1','installer-size-audit.ps1'
)
foreach($name in $files){
  $tokens=$null
  $errors=$null
  [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $name),[ref]$tokens,[ref]$errors)|Out-Null
  if($errors.Count){
    Write-Output ('PARSE_FAIL '+$name+' '+(($errors|ForEach-Object Message)-join '; '))
    exit 42
  }
  Write-Output ('PARSE_OK '+$name)
}
Write-Output 'TOOLING_PARSE_OK'
