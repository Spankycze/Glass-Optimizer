param([switch]$Build)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$proj=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
if($Build){
  $output=(& dotnet build $proj -c Release --no-restore --nologo -v:q 2>&1 | Out-String)
  if($LASTEXITCODE -ne 0){throw ('Application build failed: '+$output)}
  $warnings=@($output -split "`r?`n" | Where-Object {$_ -match ': warning [A-Z]+\d+:'})
  $sourceWarnings=@($warnings | Where-Object {$_ -notmatch '[\\/]obj[\\/]'})
  if($sourceWarnings.Count -gt 0){throw ('Application warnings: '+($sourceWarnings -join ' | '))}
  $sdkWarnings=@($warnings | Where-Object {$_ -match 'CS8305' -and $_ -match 'XamlTypeInfo\.g\.cs' -and $_ -match 'XamlControls(Charts|Tabular)XamlMetaDataProvider'})
  $unexpected=@($warnings | Where-Object {$_ -notin $sdkWarnings})
  if($unexpected.Count -gt 0){throw ('Unexpected generated warnings: '+($unexpected -join ' | '))}
  $uniqueSdk=@($sdkWarnings | ForEach-Object {if($_ -match 'XamlControls(Charts|Tabular)XamlMetaDataProvider'){$matches[1]}} | Sort-Object -Unique)
  if($uniqueSdk.Count -gt 0){throw ('Experimental SDK warnings returned: '+($uniqueSdk -join ','))}
  Write-Output ('APP_WARNING_AUDIT_OK source=0 generatedSdkTypes='+($uniqueSdk -join ','))
}
$file=Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Glass21.cs'
$text=[IO.File]::ReadAllText($file)
if($text.Contains('ContentExternalBackdropLink')){throw 'Direct experimental backdrop link type is back'}
if(([regex]::Matches($text,'_elementTarget = target as IDisposable;')).Count -ne 2){throw 'Backdrop target ownership regression'}
if(([regex]::Matches($text,'target\?\.Dispose\(\);')).Count -ne 2){throw 'Backdrop target release regression'}
Write-Output 'GLASS_SDK_API_GUARD_OK backdrops=2 directExperimentalTypes=0'
