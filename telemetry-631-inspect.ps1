Set-Location $PSScriptRoot
rg -n 'ram\.Text|refs\.Ram|Ram\.Text|ramText|UsedGb|AnimateGauge|ValueText|class Gauge|GaugeParts' src\SpankyhoOptimizer -g 'MainWindow*.cs'
