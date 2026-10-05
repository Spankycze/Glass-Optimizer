$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\modern-636'
$note=[IO.File]::ReadAllText((Join-Path $dir 'release-note.txt'))
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'modern-636' -Phase 'visual-pass-saved' -Summary 'Full-width panels, separated header toolbar and filled rounded navigation saved.' -NextAction 'Fresh636 build and run regression via full visual capture plus navigation/theme/dialog tests.' -Done @('Full-width panels','Responsive header actions','Dimensional navigation') -Pending @('Validation','Release and installation') -FilesTouched @('src\SpankyhoOptimizer\MainWindow.RamTelemetryRegression.cs','src\SpankyhoOptimizer\MainWindow.Changelog.cs','src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs','src\SpankyhoOptimizer\MainWindow.VisualQa.cs') -Evidence @('artifacts\modern-636\release-note.txt')
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
Write-Output 'MODERN636_BUILD_VERIFIED'
