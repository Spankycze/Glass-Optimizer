$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$exe=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$env:SPANKY_VISUAL_DIR=Join-Path $PSScriptRoot 'artifacts\ui-voice-613\final'
New-Item $env:SPANKY_VISUAL_DIR -ItemType Directory -Force|Out-Null
foreach($flag in @('--theme-render-smoke','--visual-layout-smoke','--visual-size-smoke','--page-scroll-input-smoke','--dialog-lifecycle-smoke','--visual-capture-smoke')){
 $proc=Start-Process $exe -ArgumentList $flag -WorkingDirectory (Split-Path $exe) -PassThru
 if(-not $proc.WaitForExit(120000)){throw "$flag timeout"}
 if($proc.ExitCode -ne 0){throw "$flag exit=$($proc.ExitCode)"}
 Write-Output "$flag OK"
}
Write-Output 'UI613_VISUAL_GATES_OK'
