$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
Copy-Item 'artifacts\ui-audit-629\build-fast.log' 'artifacts\ui-audit-629\build-fast-first-failed.log' -Force
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'ui-audit-629' -Phase 'test-fixture-fixed' -ErrorKey 'ui-state-test-init' -RootCause 'Regression fixture omitted required Group/Description and assigned init-only RestartRequired after construction.' -Fix 'Required fields initialized, RestartRequired fixed at construction; state changed through Status.' -Summary 'Test fixture compile error corrected; application changes unchanged.' -NextAction 'Run fresh build and all validation.' -Done @('Cold/warm measured','App changes saved','Fixture corrected') -Pending @('Build and tests','Release') -Evidence @('artifacts\ui-audit-629\build-fast-first-failed.log')
& .\ui-audit-629-build.ps1
if(!$?){throw 'Retry build failed'}
