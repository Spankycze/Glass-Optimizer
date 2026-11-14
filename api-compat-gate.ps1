param([switch]$UpdateBaseline,[string]$AssemblyPath='',[string]$BaselinePath='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$core=Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'
$probe=Join-Path $root 'tools\PublicApiProbe\PublicApiProbe.csproj'
$baseline=if([string]::IsNullOrWhiteSpace($BaselinePath)){
  Join-Path $root '.ai-state\public-api-core.txt'
}else{$BaselinePath}
$outDir=Join-Path $root 'artifacts\quality\api'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null

if([string]::IsNullOrWhiteSpace($AssemblyPath)){
  dotnet build $core -c Release --nologo
  if($LASTEXITCODE -ne 0){throw 'Core build failed for API compatibility gate.'}
  $AssemblyPath=Join-Path $root 'src\SpankyhoOptimizer.Core\bin\Release\net10.0-windows10.0.26100.0\SpankyhoOptimizer.Core.dll'
}
if(-not(Test-Path $AssemblyPath)){throw ('Core assembly missing: '+$AssemblyPath)}
dotnet build $probe -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'PublicApiProbe build failed.'}
$probeDll=Join-Path $root 'tools\PublicApiProbe\bin\Release\net10.0-windows10.0.26100.0\PublicApiProbe.dll'
$current=@(& dotnet $probeDll $AssemblyPath --manifest 2>&1)
if($LASTEXITCODE -ne 0){throw 'Public API manifest generation failed.'}
$current=@($current|Where-Object{$_ -ne ''}|Sort-Object -Unique)
$currentPath=Join-Path $outDir 'current.txt'
$current|Set-Content $currentPath -Encoding UTF8

if($UpdateBaseline){
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $baseline)|Out-Null
  $current|Set-Content $baseline -Encoding UTF8
  Write-Output ('API_COMPAT_BASELINE_SAVED entries='+$current.Count)
  exit 0
}
if(-not(Test-Path $baseline)){
  throw ('API_COMPAT_BASELINE_MISSING: '+$baseline+' (an explicit -UpdateBaseline is required)')
}
$expected=@(Get-Content $baseline|Where-Object{$_ -ne ''}|Sort-Object -Unique)
if($expected.Count -eq 0){throw ('API_COMPAT_BASELINE_EMPTY: '+$baseline)}
$removed=@($expected|Where-Object{$_ -notin $current})
$added=@($current|Where-Object{$_ -notin $expected})

$removed|Set-Content (Join-Path $outDir 'removed.txt') -Encoding UTF8
$added|Set-Content (Join-Path $outDir 'added.txt') -Encoding UTF8
$added|Select-Object -First 20|ForEach-Object{Write-Output ('API_COMPAT_ADDED '+$_)}

$retiredPath=Join-Path $root 'quality-reviewed-api-retirements.txt'
$reviewed=if(Test-Path $retiredPath){
  @(Get-Content $retiredPath | Where-Object {$_ -and -not $_.TrimStart().StartsWith('#')} | Sort-Object -Unique)
}else{@()}
$invalidReviewed=@($reviewed|Where-Object{$_ -notin $expected})
if($invalidReviewed.Count -gt 0){throw ('Reviewed API retirement is absent from baseline: '+$invalidReviewed[0])}
$retired=@($removed|Where-Object{$_ -in $reviewed})
$unreviewed=@($removed|Where-Object{$_ -notin $reviewed})
$retired|Set-Content (Join-Path $outDir 'reviewed-retirements.txt') -Encoding UTF8
if($retired.Count){Write-Output ('API_COMPAT_REVIEWED_RETIREMENTS count='+$retired.Count+' manifest='+$retiredPath)}
if($unreviewed.Count -gt 0){
  $unreviewed|Select-Object -First 40|ForEach-Object{Write-Output ('API_COMPAT_REMOVED_UNREVIEWED '+$_)}
  Write-Output ('API_COMPAT_BREAK unreviewed='+$unreviewed.Count+' reviewed='+$retired.Count+' added='+$added.Count)
  exit 42
}
Write-Output ('API_COMPAT_OK unreviewed=0 reviewedRetirements='+$retired.Count+' added='+$added.Count+' entries='+$current.Count)
