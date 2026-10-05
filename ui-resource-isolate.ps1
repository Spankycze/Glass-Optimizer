$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$theme='src\SpankyhoOptimizer\MainWindow.Theme.cs'
$neutral='src\SpankyhoOptimizer\MainWindow.NeutralUi.cs'
foreach($path in @($theme,$neutral)){
 $text=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $path))
 $count=0
 $text=[regex]::Replace($text,'resources\["([^"]+)"\] = (.+);',{param($m)
  if($m.Groups[2].Value.StartsWith('Windows.UI.Color.')){return $m.Value}
  $script:count++
  'SetOwnedThemeBrush(resources, "'+$m.Groups[1].Value+'", '+$m.Groups[2].Value+');'
 })
 if($count -lt 5){throw "Unexpected resource count $path $count"}
 [IO.File]::WriteAllText((Join-Path $PSScriptRoot $path),$text)
 Write-Output "$path isolatedBrushKeys=$count"
}
