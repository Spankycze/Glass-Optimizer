Set-Location $PSScriptRoot
rg -n 'FontWeights.Light|FontFamily|FontSize = 2[4-9]' src\SpankyhoOptimizer\MainWindow.StyledDialogs.cs src\SpankyhoOptimizer\MainWindow.Shell.cs src\SpankyhoOptimizer\MainWindow.BlackUi.cs
