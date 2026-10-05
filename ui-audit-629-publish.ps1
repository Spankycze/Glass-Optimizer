$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'ui-audit-629' -Phase 'validated-before-release' -Summary 'Fresh629 build/tests passed. Seven shared states and disabled contrast6.98 dark passed; cold3-12ms warm0-1ms similar to baseline, no global speedup claim.' -NextAction 'Run gated629 release, install and verify fresh fan agent; then attach actual screenshots.' -Done @('Build and all seven validation flags passed','State/contrast regression passed','Actual screenshots reviewed') -Pending @('Release','Installation and agent verification') -Evidence @('artifacts\ui-audit-629\validation-result.json','artifacts\ui-audit-629\after-perf.log')
& .\ui-audit-629-release.ps1
if(!$?){throw 'Release/install failed'}
