$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dest=Join-Path $PWD 'artifacts\black-depth-624'
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
$report=@()
foreach($flag in @('--theme-render-smoke','--dialog-lifecycle-smoke','--visual-capture-smoke')){
 $proc=Start-Process $exe -ArgumentList $flag -WorkingDirectory (Split-Path $exe) -PassThru
 if(!$proc.WaitForExit(120000)){throw "$flag timeout"}
 if($proc.ExitCode -ne 0){throw "$flag exit=$($proc.ExitCode)"}
 $report+=[ordered]@{flag=$flag;exit=0}
 $report|ConvertTo-Json|Set-Content (Join-Path $dest 'validation-result.json') -Encoding UTF8
 Write-Output "$flag OK"
}
Copy-Item 'C:\ProgramData\SpankyhoOptimizer\visual-current' (Join-Path $dest 'screens') -Recurse -Force
Write-Output 'BLACK624_VALIDATION_OK'
