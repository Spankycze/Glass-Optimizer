$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$text=[IO.File]::ReadAllText((Join-Path $PWD 'dashboard-628-release.ps1')).Replace('dashboard-628','ui-audit-629').Replace('15.4.628','15.4.629').Replace('V15_4_628','V15_4_629').Replace('DASHBOARD628','UI629')
# Release helper completes with concrete continuity fields after installation.
$text=$text.Replace("-Action complete-task -TaskId 'ui-audit-629'","-Action complete-task -TaskId 'ui-audit-629' -Summary 'Installed629: readable disabled controls and unified operation states. Cold/warm measured, tests and release passed.' -NextAction 'All authorized UI audit work complete; follow next user request.' -Done @('Cold/warm measured','Contrast and seven classic/virtual states tested','Fresh629 build and release installed') -Evidence @('artifacts\ui-audit-629\validation-result.json','artifacts\ui-audit-629\deployment-result.json','artifacts\ui-audit-629\release-result.json')")
[IO.File]::WriteAllText((Join-Path $PWD 'ui-audit-629-release.ps1'),$text)
Write-Output 'RELEASE629_PREPARED'
