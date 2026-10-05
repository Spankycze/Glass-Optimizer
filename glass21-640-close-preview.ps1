$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$report=@()
foreach($proc in @(Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue)){
 $report+=@{id=$proc.Id;path=$proc.Path;window=$proc.MainWindowHandle.ToInt64()}
 if($proc.Path -like '*\.buildcache\staged-debug\SpankyhoOptimizer.exe' -and $proc.MainWindowHandle -ne 0){
  $null=$proc.CloseMainWindow()
  if(!$proc.WaitForExit(5000)){throw 'Preview did not close gracefully'}
 }
}
$report|ConvertTo-Json|Set-Content (Join-Path $PWD 'artifacts\glass21-640\preview-close-report.json')
