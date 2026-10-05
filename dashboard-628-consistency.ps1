$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$changes=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $PWD 'artifacts\dashboard-628\consistency-changes.json')))
foreach($group in ($changes|Group-Object file)){
 $path=Join-Path $PWD ('src\SpankyhoOptimizer\'+$group.Name)
 Copy-Item $path (Join-Path $PWD ('artifacts\dashboard-628\before\consistency-'+$group.Name)) -Force
 $text=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
 foreach($change in $group.Group){if(!$text.Contains($change.old)){throw ('Anchor missing '+$group.Name)}; $text=$text.Replace($change.old,$change.neu)}
 [IO.File]::WriteAllText($path,$text)
}
Write-Output 'CONSISTENCY628_PATCH_OK'