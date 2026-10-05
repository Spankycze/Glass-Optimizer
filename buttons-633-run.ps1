$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$build=[IO.File]::ReadAllText((Join-Path $PWD 'section-632-build.ps1')).Replace('section-632','buttons-633').Replace('Fresh632','Fresh633').Replace('SECTION632','BUTTONS633')
$build=$build.Replace('Visual pass saved: unified Network header, readable helper/status labels, explicitly named motion toggle, subtly recessed black workspace surfaces.','Solid material Quick/primary/dialog buttons saved: neutral edges, stronger filled surfaces and hover/pressed state resources.')
[IO.File]::WriteAllText((Join-Path $PWD 'buttons-633-build.ps1'),$build)
& .\buttons-633-build.ps1
