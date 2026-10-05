$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\ui-audit-629'
$note=[IO.File]::ReadAllText((Join-Path $dir 'release-note.txt'))
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'ui-audit-629' -Phase 'before-build' -Summary 'Measured cold3-11ms warm0-2ms. Implemented shared states and readable disabled control resources plus state/contrast regressions.' -NextAction 'Build fresh629 and run theme, navigation, dialog, layout and capture tests.' -Done @('Baseline performance measured','Shared state and contrast changes saved') -Pending @('Build and tests','Release and installation') -FilesTouched @('src\SpankyhoOptimizer\MainWindow.cs','src\SpankyhoOptimizer\MainWindow.NeutralUi.cs','src\SpankyhoOptimizer\MainWindow.TweakVerificationMeta.cs','src\SpankyhoOptimizer\MainWindow.TweakVirtualization.cs','src\SpankyhoOptimizer\MainWindow.NeutralUiRegression.cs') -Evidence @('artifacts\ui-audit-629\baseline-perf.log')
& .\build-fast.ps1 *> (Join-Path $dir 'build-fast.log')
if(!$?){throw 'Build failed'}
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dir 'build-test.log')
if(!$?){throw 'Tests failed'}
$report=@()
foreach($flag in @('--performance-smoke','--theme-render-smoke','--startup-isolated-smoke-bundle','--visual-size-smoke','--navigation-tail-smoke','--dialog-lifecycle-smoke','--visual-capture-smoke')){
 $proc=Start-Process $exe -ArgumentList $flag -WorkingDirectory (Split-Path $exe) -PassThru
 if(!$proc.WaitForExit(120000)){throw "$flag timeout"}
 if($proc.ExitCode -ne 0){throw "$flag exit=$($proc.ExitCode)"}
 $report+=@{flag=$flag;exit=0;exe=$exe}
 $report|ConvertTo-Json|Set-Content (Join-Path $dir 'validation-result.json') -Encoding UTF8
 Write-Output "$flag OK"
 if($flag -eq '--performance-smoke'){Get-Content 'C:\ProgramData\SpankyhoOptimizer\performance.log' -Tail 20|Set-Content (Join-Path $dir 'after-perf.log') -Encoding UTF8}
}
New-Item (Join-Path $dir 'screens') -ItemType Directory -Force|Out-Null
Copy-Item 'C:\ProgramData\SpankyhoOptimizer\visual-current\*' (Join-Path $dir 'screens') -Recurse -Force
Write-Output 'UI629_BUILD_VERIFIED'
