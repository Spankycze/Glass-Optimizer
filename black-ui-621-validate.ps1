$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dest=Join-Path $PSScriptRoot 'artifacts\black-ui-621'
New-Item $dest -ItemType Directory -Force|Out-Null
& .\build-fast.ps1 *> (Join-Path $dest 'build-fast.log')
if(!$?){throw 'Fast build failed'}
$exe=(& .\resolve-debug-exe.ps1 | Select-Object -Last 1)
Write-Output "VALIDATION_EXE $exe"
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dest 'build-test.log')
if(!$?){throw 'Build test failed'}
$report=@()
$env:SPANKY_VISUAL_DIR=Join-Path $dest 'screens'
foreach($flag in @('--theme-render-smoke','--visual-layout-smoke','--visual-size-smoke','--dialog-lifecycle-smoke','--visual-capture-smoke')){
 $sw=[Diagnostics.Stopwatch]::StartNew()
 $proc=Start-Process $exe -ArgumentList $flag -WorkingDirectory (Split-Path $exe) -PassThru
 if(!$proc.WaitForExit(120000)){throw "$flag timeout"}
 if($proc.ExitCode -ne 0){throw "$flag exit=$($proc.ExitCode)"}
 $report+=[ordered]@{flag=$flag;exit=0;ms=$sw.ElapsedMilliseconds}
 $report|ConvertTo-Json|Set-Content (Join-Path $dest 'ui-gates.json') -Encoding UTF8
 Write-Output "$flag OK"
}
$globalVisual='C:\ProgramData\SpankyhoOptimizer\visual-current'
if(Test-Path $globalVisual){Copy-Item $globalVisual (Join-Path $dest 'screens') -Recurse -Force}
Write-Output 'BLACK_UI_VALIDATION_OK'
