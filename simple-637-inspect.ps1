Set-Location $PSScriptRoot
Get-ChildItem -File -Filter '*perf*.ps1'|Select-Object Name
Get-ChildItem -File -Filter '*bench*.ps1'|Select-Object Name
rg -n 'navigation-bench|navigation-perf|cold.*smoke|AnimateSelectionPulse|Animate.*Section' src\SpankyhoOptimizer\App.xaml.cs src\SpankyhoOptimizer\MainWindow*.cs
