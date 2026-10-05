$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$build=[IO.File]::ReadAllText((Join-Path $PWD 'buttons-633-build.ps1')).Replace('buttons-633','fonts-634').Replace('Fresh633','Fresh634').Replace('BUTTONS633','FONTS634')
$build=$build.Replace('Solid material Quick/primary/dialog buttons saved: neutral edges, stronger filled surfaces and hover/pressed state resources.','Variable Text/Display font saved, stronger page/dialog titles, readable metric digits and12.5action labels.')
[IO.File]::WriteAllText((Join-Path $PWD 'fonts-634-build.ps1'),$build)
& .\fonts-634-build.ps1
