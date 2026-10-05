$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$files=@('src\SpankyhoOptimizer\MainWindow.NeutralUi.cs','src\SpankyhoOptimizer\MainWindow.cs','src\SpankyhoOptimizer\MainWindow.Shell.cs','src\SpankyhoOptimizer\MainWindow.Motion.cs','src\SpankyhoOptimizer\MainWindow.NetworkDashboard.cs')
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'simple-637' -Summary 'Fresh637 tests and all six suites passed; compact UI actual captures checked.' -NextAction 'Gated release637/install then compare three installed Release samples with636.' -Done @('Compact panels','Immediate navigation','Six suites') -Pending @('Release/install','Release performance comparison') -FilesTouched $files -Evidence @('artifacts\simple-637\validation-result.json','artifacts\simple-637\before-performance.json')
$t=[IO.File]::ReadAllText((Join-Path $PWD 'modern-636-release.ps1')).Replace('modern-636','simple-637').Replace('15.4.636','15.4.637').Replace('V15_4_636','V15_4_637').Replace('MODERN636','SIMPLE637')
$t=[regex]::Replace($t,'(?m)^& \.\\ai-workflow\.ps1 -Action complete-task[^\r\n]*\r?\n','')
[IO.File]::WriteAllText((Join-Path $PWD 'simple-637-release.ps1'),$t)
& .\simple-637-release.ps1
if(!$?){throw 'Release/install failed'}
& .\simple-637-measure.ps1 -Label 'after'
if(!$?){throw 'Release performance comparison failed'}
Write-Output 'SIMPLE637_INSTALLED_MEASURED'
