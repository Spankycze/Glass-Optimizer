$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$f=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.ServicePrewarm.cs'
Copy-Item $f (Join-Path $PWD 'artifacts\glass21-639\before\MainWindow.ServicePrewarm.cs') -Force
$s=[IO.File]::ReadAllText($f).Replace('_ = expander.Content;','_ = expander is Microsoft.UI.Xaml.Controls.Expander control ? control.Content : (expander as Microsoft.UI.Xaml.Controls.Border)?.Child;')
[IO.File]::WriteAllText($f,$s)
$b=Join-Path $PWD 'glass21-639-build.ps1'
$s=[IO.File]::ReadAllText($b).Replace('Open compact sections replace large framed panels; tighter navigation and headings, redundant banner removed.','Concept21 native glass shell and responsive System/Privacy groups; Overview preserved.').Replace('Fresh638 build and run regression via full visual capture plus navigation/theme/dialog tests.','Fresh639 build and full visual/navigation/theme/dialog regression.')
[IO.File]::WriteAllText($b,$s)
& .\glass21-639-build.ps1
