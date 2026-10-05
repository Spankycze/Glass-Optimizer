$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item 'artifacts\ui-audit-629\before' -ItemType Directory -Force|Out-Null
& .\ai-workflow.ps1 -Action start-task -TaskId 'ui-audit-629' -Title 'Cold navigation popup contrast and state consistency' -Area 'winui' -Summary 'User authorized cold-open latency, popup contrast, remaining UI consistency and visible operation states.' -NextAction 'Measure installed628 cold/warm navigation and inspect shared dialog/control brushes.' -ValidationRequired @('cold-warm performance','theme contrast','dialog lifecycle','build-test','release')
$ix=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $PWD 'PROJECT_INDEX.json')))
$ix.PSObject.Properties|Select-Object Name|Format-Table -HideTableHeaders
$exe='C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe'
$perf=Start-Process $exe -ArgumentList '--performance-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
if(!$perf.WaitForExit(120000)){throw 'Baseline timeout'}
if($perf.ExitCode -ne 0){throw ('Baseline exit '+$perf.ExitCode)}
Get-Content 'C:\ProgramData\SpankyhoOptimizer\performance.log' -Tail 20|Set-Content 'artifacts\ui-audit-629\baseline-perf.log' -Encoding UTF8
Write-Output 'BASELINE629_CAPTURED'
