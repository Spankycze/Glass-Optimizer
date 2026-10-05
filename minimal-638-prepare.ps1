$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\minimal-638'
Copy-Item (Join-Path $PWD 'artifacts\simple-637\after-performance.json') (Join-Path $dir 'before-performance.json') -Force
$m=[IO.File]::ReadAllText((Join-Path $PWD 'simple-637-measure.ps1')).Replace('artifacts\simple-637','artifacts\minimal-638')
[IO.File]::WriteAllText((Join-Path $PWD 'minimal-638-measure.ps1'),$m)
$t=[IO.File]::ReadAllText((Join-Path $PWD 'weight-635-release.ps1')).Replace('weight-635','minimal-638').Replace('15.4.635','15.4.638').Replace('V15_4_635','V15_4_638').Replace('WEIGHT635','MINIMAL638')
$t=[regex]::Replace($t,'(?m)^& \.\\ai-workflow\.ps1 -Action complete-task[^\r\n]*\r?\n','')
[IO.File]::WriteAllText((Join-Path $PWD 'minimal-638-release.ps1'),$t)
Write-Output 'MINIMAL638_RELEASE_HELPERS_READY'
