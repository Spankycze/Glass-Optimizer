$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$helper=Join-Path $PWD 'glass21-641-close-preview.ps1'
$p=Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"')) -PassThru
if(!$p.WaitForExit(10000) -or $p.ExitCode -ne 0){throw 'Close preview failed'}
$dir=Join-Path $PWD 'artifacts\glass21-641'
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
if((Get-Item $exe).VersionInfo.ProductVersion -notlike '15.4.641*'){throw 'Wrong fresh EXE'}
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
Write-Output 'GLASS21641_TESTS_VERIFIED'
