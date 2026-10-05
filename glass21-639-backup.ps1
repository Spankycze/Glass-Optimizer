$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item 'artifacts\glass21-639\before' -ItemType Directory -Force|Out-Null
Copy-Item 'src\SpankyhoOptimizer\MainWindow.Shell.cs' 'artifacts\glass21-639\before\MainWindow.Shell.cs' -Force
Copy-Item 'src\SpankyhoOptimizer\MainWindow.cs' 'artifacts\glass21-639\before\MainWindow.cs' -Force
Copy-Item 'src\SpankyhoOptimizer\MainWindow.Theme.cs' 'artifacts\glass21-639\before\MainWindow.Theme.cs' -Force
Copy-Item 'src\SpankyhoOptimizer\MainWindow.NeutralUi.cs' 'artifacts\glass21-639\before\MainWindow.NeutralUi.cs' -Force
Copy-Item 'src\SpankyhoOptimizer\MainWindow.MaterialButtons.cs' 'artifacts\glass21-639\before\MainWindow.MaterialButtons.cs' -Force
Copy-Item 'src\SpankyhoOptimizer\MainWindow.TweakVirtualization.cs' 'artifacts\glass21-639\before\MainWindow.TweakVirtualization.cs' -Force
Copy-Item 'src\SpankyhoOptimizer\App.xaml' 'artifacts\glass21-639\before\App.xaml' -Force