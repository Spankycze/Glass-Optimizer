$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$b=[IO.File]::ReadAllText((Join-Path $PWD 'minimal-638-build.ps1')).Replace('minimal-638','glass21-639').Replace('MINIMAL638','GLASS21639')
[IO.File]::WriteAllText((Join-Path $PWD 'glass21-639-build.ps1'),$b)
$r=[IO.File]::ReadAllText((Join-Path $PWD 'minimal-638-release.ps1')).Replace('minimal-638','glass21-639').Replace('15.4.638','15.4.639').Replace('V15_4_638','V15_4_639').Replace('MINIMAL638','GLASS21639')
[IO.File]::WriteAllText((Join-Path $PWD 'glass21-639-release.ps1'),$r)
& .\ai-workflow.ps1 -Action checkpoint -TaskId glass21-639 -Phase source-glass -Summary 'Native acrylic and glass chrome; responsive System/Privacy groups preserve virtual lifecycle and real states.' -NextAction 'Fresh build, regression and actual visual review.' -FilesTouched @('src\SpankyhoOptimizer\MainWindow.Glass21.cs','src\SpankyhoOptimizer\MainWindow.Shell.cs','src\SpankyhoOptimizer\MainWindow.Theme.cs','src\SpankyhoOptimizer\MainWindow.NeutralUi.cs','src\SpankyhoOptimizer\MainWindow.MaterialButtons.cs','src\SpankyhoOptimizer\MainWindow.TweakVirtualization.cs','src\SpankyhoOptimizer\MainWindow.cs','src\SpankyhoOptimizer\App.xaml','src\SpankyhoOptimizer\MainWindow.VisualQa.cs') -Pending @('Build and visual validation','Release/install') -Evidence @('docs\superpowers\plans\2026-10-04-glass21.md','artifacts\glass21-639\before')
& .\glass21-639-build.ps1
