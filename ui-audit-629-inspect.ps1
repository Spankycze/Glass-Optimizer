Set-Location $PSScriptRoot
$ix=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $PWD 'PROJECT_INDEX.json')))
$ix.classes|Where-Object {$_.file -match 'Dialog|Popup|Neutral|Theme|PerformanceSmoke|Tweak'}|Select-Object -First 15|ConvertTo-Json -Depth 3
rg -n 'ButtonForegroundDisabled|TextFillColorDisabled|IsEnabled.*Opacity|Opacity.*IsEnabled|PopupText|DialogText|Configure.*Dialog|Create.*Dialog|SpankyTextSecondary' src\SpankyhoOptimizer -g 'MainWindow*.cs' -g '!**/obj/**' -g '!**/bin/**'
