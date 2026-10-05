$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$f=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.NeutralUi.cs'
$t=[IO.File]::ReadAllText($f)
$t=$t.Replace('            if (actions is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;','            var actionHost = new Border { Child = actions, VerticalAlignment = VerticalAlignment.Center };').Replace('Grid.SetRow(actions, 1);','Grid.SetRow(actionHost, 1);').Replace('Grid.SetColumnSpan(actions, 2);','Grid.SetColumnSpan(actionHost, 2);').Replace('if (actions is FrameworkElement narrowActions) narrowActions.Margin = new Thickness(0,14,0,0);','actionHost.Margin = new Thickness(0,14,0,0);').Replace('else Grid.SetColumn(actions, 1);','else Grid.SetColumn(actionHost, 1);').Replace('grid.Children.Add(actions);','grid.Children.Add(actionHost);')
[IO.File]::WriteAllText($f,$t)
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'modern-636' -Summary 'Build error CS1503 recovered: WinUI Grid attached setters require FrameworkElement; UIElement action content now wrapped in Border.' -NextAction 'Rebuild636 and verify all six suites and expanded visual captures.' -Done @('Layout saved','Build type error fixed') -Pending @('Validation','Release/install') -FilesTouched @('src\SpankyhoOptimizer\MainWindow.NeutralUi.cs','src\SpankyhoOptimizer\MainWindow.Shell.cs','src\SpankyhoOptimizer\MainWindow.NetworkDashboard.cs','src\SpankyhoOptimizer\MainWindow.VisualQa.cs')
& .\modern-636-build.ps1
