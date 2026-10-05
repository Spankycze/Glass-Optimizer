$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$m=[IO.File]::ReadAllText((Join-Path $PWD 'minimal-638-measure.ps1')).Replace('artifacts\minimal-638','artifacts\glass21-639')
[IO.File]::WriteAllText((Join-Path $PWD 'glass21-639-measure.ps1'),$m)
Copy-Item 'artifacts\minimal-638\after-performance.json' 'artifacts\glass21-639\before-performance.json' -Force
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
& .\glass21-639-measure.ps1 -Label after -Exe $exe
