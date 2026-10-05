$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\dc-smart.ps1 '.\build-fast.ps1'
if($LASTEXITCODE -ne 0){throw 'build-fast failed'}
$exe=& '.\ui-select-build.ps1'
$testCommand='.\build-test.ps1 -NoBuild -ForceTests -ExePath "'+$exe+'"'
& .\dc-smart.ps1 $testCommand
if($LASTEXITCODE -ne 0){throw 'build-test failed'}
Write-Output ('UI_VALIDATION_BINARY '+$exe)
$work=Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign'
New-Item -ItemType Directory -Force "$work\after" | Out-Null
foreach($flag in @('--theme-render-smoke','--visual-capture-smoke')){
 $p=Start-Process $exe -ArgumentList $flag -PassThru -WorkingDirectory (Split-Path $exe)
 if(-not $p.WaitForExit(120000)){Stop-Process -Id $p.Id -Force;throw "$flag timeout"}
 if($p.ExitCode -ne 0){throw "$flag exit=$($p.ExitCode)"}
 Write-Output "UI_SMOKE_OK $flag"
}
Copy-Item 'C:\ProgramData\SpankyhoOptimizer\visual-current\*.png' "$work\after" -Force
[IO.File]::WriteAllText("$work\validation.txt",'BUILD_FAST_OK BUILD_TEST_OK THEME_RENDER_OK VISUAL_CAPTURE_OK')
Write-Output 'UI_VALIDATION_OK'
