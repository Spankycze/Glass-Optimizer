$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$build=[IO.File]::ReadAllText((Join-Path $PWD 'fonts-634-build.ps1')).Replace('fonts-634','weight-635').Replace('Fresh634','Fresh635').Replace('FONTS634','WEIGHT635')
$build=$build.Replace('Variable Text/Display font saved, stronger page/dialog titles, readable metric digits and12.5action labels.','Global body Medium and explicit Normal overrides raised; sidebar13SemiBold and all material actions SemiBold.')
[IO.File]::WriteAllText((Join-Path $PWD 'weight-635-build.ps1'),$build)
& .\weight-635-build.ps1
