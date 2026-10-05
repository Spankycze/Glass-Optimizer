$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$changes=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $PWD 'artifacts\ui-audit-629\edits.json')))
foreach($group in ($changes|Group-Object f)){
 $path=Join-Path $PWD ('src\SpankyhoOptimizer\'+$group.Name)
 Copy-Item $path (Join-Path $PWD ('artifacts\ui-audit-629\before\'+$group.Name)) -Force
 $text=[IO.File]::ReadAllText($path).Replace([string][char]13,'')
 foreach($c in $group.Group){if(!$text.Contains($c.old)){throw ('Anchor '+$group.Name)};$text=$text.Replace($c.old,$c.neu)}
 [IO.File]::WriteAllText($path,$text)
}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.TweakVirtualization.cs'
Copy-Item $path 'artifacts\ui-audit-629\before' -Force
$text=[IO.File]::ReadAllText($path).Replace([string][char]13,'')
$start=$text.IndexOf('            string newStatusText;')
$end=$text.IndexOf('            bool statusChanged', $start)
if($start -lt 0 -or $end -lt $start){throw 'VM anchor'}
$text=$text.Remove($start,$end-$start).Insert($start,@'
            var uiState = TweakUiState(_source);
            string newStatusText = uiState.Text;
            Brush newStatusBrush = UiBrush(uiState.Resource);

'@)
[IO.File]::WriteAllText($path,$text)
Write-Output 'UI629_PATCH_OK'
