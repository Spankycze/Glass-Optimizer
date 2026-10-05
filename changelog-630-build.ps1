$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\changelog-630'
$note=[IO.File]::ReadAllText((Join-Path $dir 'release-note.txt'))
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'changelog-630' -Phase 'red-confirmed-fix-saved' -Summary 'Old code regression failed as expected: snapshot/history refresh overwrote release rows. Fix separates caption and removes batch-history writes to changelog rows.' -NextAction 'Fresh630 build and run regression via full visual capture plus navigation/theme/dialog tests.' -Done @('Root cause traced','Failing test confirmed','Single fix saved') -Pending @('Validation','Release and installation') -FilesTouched @('src\SpankyhoOptimizer\MainWindow.ChangelogRegression.cs','src\SpankyhoOptimizer\MainWindow.Changelog.cs','src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs','src\SpankyhoOptimizer\MainWindow.VisualQa.cs') -Evidence @('artifacts\changelog-630\red-regression.log')
& .\build-fast.ps1 *> (Join-Path $dir 'build-fast.log')
if(!$?){throw 'Build failed'}
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dir 'build-test.log')
if(!$?){throw 'Tests failed'}
$report=@()
foreach($flag in @('--visual-capture-smoke','--theme-render-smoke','--startup-isolated-smoke-bundle','--visual-size-smoke','--navigation-tail-smoke','--dialog-lifecycle-smoke')){
 $proc=Start-Process $exe -ArgumentList $flag -WorkingDirectory (Split-Path $exe) -PassThru
 if(!$proc.WaitForExit(120000)){throw "$flag timeout"}
 if($proc.ExitCode -ne 0){throw "$flag exit=$($proc.ExitCode)"}
 $report+=@{flag=$flag;exit=0;exe=$exe}
 $report|ConvertTo-Json|Set-Content (Join-Path $dir 'validation-result.json') -Encoding UTF8
 Write-Output "$flag OK"
}
New-Item (Join-Path $dir 'screens') -ItemType Directory -Force|Out-Null
Copy-Item 'C:\ProgramData\SpankyhoOptimizer\visual-current\*' (Join-Path $dir 'screens') -Recurse -Force
Write-Output 'CHANGELOG630_BUILD_VERIFIED'
