$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\weight-635'
New-Item (Join-Path $dir 'before') -ItemType Directory -Force|Out-Null
& .\ai-workflow.ps1 -Action start-task -TaskId 'weight-635' -Summary 'User says overall font still thin especially sidebar. Raise body to Medium, sidebar/actions to SemiBold and sidebar13px.' -NextAction 'Save weight changes then verify real screenshots and six layout/navigation suites.'
$path=Join-Path $PWD 'src\SpankyhoOptimizer\App.xaml'
Copy-Item $path (Join-Path $dir 'before') -Force
$text=[IO.File]::ReadAllText($path)
$anchor='<Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>'
if(!$text.Contains($anchor)){throw 'Text style missing'}
$text=$text.Replace($anchor,$anchor+[Environment]::NewLine+'                <Setter Property="FontWeight" Value="Medium"/>')
[IO.File]::WriteAllText($path,$text)
$changed=@('App.xaml')
Get-ChildItem (Join-Path $PWD 'src\SpankyhoOptimizer') -Filter 'MainWindow*.cs' -File | ForEach-Object {
 $text=[IO.File]::ReadAllText($_.FullName)
 if($text.Contains('FontWeights.Normal')){
  Copy-Item $_.FullName (Join-Path $dir 'before') -Force
  $text=$text.Replace('FontWeights.Normal','FontWeights.Medium')
  [IO.File]::WriteAllText($_.FullName,$text)
  $changed+=$_.Name
 }
}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.Shell.cs'
if(!(Test-Path (Join-Path $dir 'before\MainWindow.Shell.cs'))){Copy-Item $path (Join-Path $dir 'before') -Force}
$text=[IO.File]::ReadAllText($path).Replace('Text = title, FontSize = 12, FontWeight = FontWeights.Medium,','Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,')
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.MaterialButtons.cs'
$text=[IO.File]::ReadAllText($path).Replace('button.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium;','button.FontWeight = FontWeights.SemiBold;')
[IO.File]::WriteAllText($path,$text)
$changed|ConvertTo-Json|Set-Content (Join-Path $dir 'changed-files.json') -Encoding UTF8
Write-Output ('WEIGHT635_FIX_SAVED files='+$changed.Count)
