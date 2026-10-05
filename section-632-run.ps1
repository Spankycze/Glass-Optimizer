$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.NetworkDashboard.cs'
$text=[IO.File]::ReadAllText($path).Replace('FontWeight = UseCompactSectionChrome ? FontWeights.Light : FontWeights.SemiBold,','FontWeight = FontWeights.SemiBold,')
$text=$text.Replace('FontSize = UseCompactSectionChrome ? 32 : 22,'+[Environment]::NewLine+'            FontWeight = FontWeights.SemiBold,','FontSize = UseCompactSectionChrome ? 32 : 22,'+[Environment]::NewLine+'            FontWeight = UseCompactSectionChrome ? FontWeights.Light : FontWeights.SemiBold,')
$text=[regex]::Replace($text,'(FontSize = UseCompactSectionChrome \? 32 : 22,\s*)FontWeight = FontWeights.SemiBold,','$1FontWeight = UseCompactSectionChrome ? FontWeights.Light : FontWeights.SemiBold,')
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.Tools.cs'
$text=[IO.File]::ReadAllText($path)
$text=[regex]::Replace($text,'Motiv a animace[^"]*cinematic UI',('Motiv a animace rozhran'+[char]0x00ED))
[IO.File]::WriteAllText($path,$text)
$build=[IO.File]::ReadAllText((Join-Path $PWD 'telemetry-631-build.ps1')).Replace('telemetry-631','section-632').Replace('Fresh631','Fresh632').Replace('RAM631','SECTION632')
$build=$build.Replace('Old code real telemetry regression failed. RAM summary now shares live sample with gauge and late snapshots retain latest sample.','Visual pass saved: unified Network header, readable helper/status labels, explicitly named motion toggle, subtly recessed black workspace surfaces.')
$build=$build.Replace('red-confirmed-fix-saved','visual-pass-saved').Replace("'artifacts\section-632\red-regression.log'","'artifacts\section-632\release-note.txt'")
[IO.File]::WriteAllText((Join-Path $PWD 'section-632-build.ps1'),$build)
& .\section-632-build.ps1
