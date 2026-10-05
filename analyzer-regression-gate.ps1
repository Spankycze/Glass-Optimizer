param([switch]$UpdateBaseline,[string]$LogPath='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if(-not $LogPath){$LogPath=Join-Path $root 'artifacts\quality\analyzers\extended-analyzers.log'}
$baselinePath=Join-Path $root '.ai-state\analyzer-baseline.json'
$pendingBaselinePath=$baselinePath+'.pending'
if(-not(Test-Path $LogPath)){throw 'Analyzer log missing: '+$LogPath}
$findings=New-Object Collections.Generic.List[object]
$trackedRules=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@('CA1416','CA1422','CA1849','CA1851','CA1869','VSTHRD103','VSTHRD110','AsyncFixer02','AsyncFixer03','PH2086') | ForEach-Object {[void]$trackedRules.Add($_)}
foreach($line in Get-Content $LogPath){
  if($line -notmatch '^(.*)\((\d+),(\d+)\): warning ([A-Za-z0-9]+): (.*?)(?: \(https?://.*\))? \['){continue}
  if(-not $trackedRules.Contains($matches[4])){continue}
  $file=$matches[1].Trim()
  $file=[regex]::Replace($file,'^\d+>','').Trim()
  if($file.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$file=$file.Substring($root.Length).TrimStart('\')}
  $rule=$matches[4]
  $msg=$matches[5].Trim()
  $normalized=[regex]::Replace($msg,'\s+',' ')
  $fingerprint=($rule+'|'+$file+'|'+$normalized).ToLowerInvariant()
  $findings.Add([pscustomobject]@{rule=$rule;file=$file;line=[int]$matches[2];message=$msg;fingerprint=$fingerprint})|Out-Null
}
$unique=@($findings|Sort-Object fingerprint -Unique)
if($UpdateBaseline -or -not(Test-Path $baselinePath)){
  New-Item -ItemType Directory -Force -Path (Split-Path $baselinePath)|Out-Null
  $json=[ordered]@{generatedAt=(Get-Date).ToString('o');count=$unique.Count;findings=$unique} | ConvertTo-Json -Depth 6
  try{
    $json | Set-Content $baselinePath -Encoding UTF8
    Write-Output ('ANALYZER_BASELINE_SAVED count='+$unique.Count+' path='+$baselinePath)
  }catch [IO.IOException]{
    $fallback=$baselinePath+'.pending'
    $json | Set-Content $fallback -Encoding UTF8
    Write-Output ('ANALYZER_BASELINE_LOCKED fallback='+$fallback)
  }
  exit 0
}
$effectiveBaseline=$baselinePath
if((Test-Path $pendingBaselinePath) -and (!(Test-Path $baselinePath) -or (Get-Item $pendingBaselinePath).LastWriteTimeUtc -gt (Get-Item $baselinePath).LastWriteTimeUtc)){
  $effectiveBaseline=$pendingBaselinePath
  Write-Output ('ANALYZER_BASELINE_PENDING_ACTIVE '+$effectiveBaseline)
}
$baseline=Get-Content $effectiveBaseline -Raw|ConvertFrom-Json
function CanonicalFingerprint($x){
  $f=([string]$x.file).Trim()
  $f=[regex]::Replace($f,'^\d+>','').Trim()
  if($f.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$f=$f.Substring($root.Length).TrimStart('\')}
  $m=[regex]::Replace(([string]$x.message).Trim(),'\s+',' ')
  return ((([string]$x.rule)+'|'+$f+'|'+$m).ToLowerInvariant())
}
$old=@{}
foreach($x in @($baseline.findings)){$old[(CanonicalFingerprint $x)]=$true}
$new=@($unique|Where-Object{-not $old.ContainsKey($_.fingerprint)})
$current=@{};foreach($x in $unique){$current[$x.fingerprint]=$true}
$resolved=@($baseline.findings|Where-Object{-not $current.ContainsKey((CanonicalFingerprint $_))})
$hot=$unique|Group-Object rule|Sort-Object Count -Descending|Select-Object -First 8
Write-Output ('ANALYZER_REGRESSION_SUMMARY current='+$unique.Count+' baseline='+$baseline.count+' new='+$new.Count+' resolved='+$resolved.Count)
$hot|ForEach-Object{Write-Output ('ANALYZER_HOTSPOT '+$_.Name+'='+$_.Count)}
if($resolved.Count){Write-Output ('ANALYZER_IMPROVED resolved='+$resolved.Count)}
if($new.Count){
  $new|Select-Object -First 20|ForEach-Object{Write-Output ('ANALYZER_NEW '+$_.rule+' '+$_.file+':'+$_.line+' '+$_.message)}
  exit 42
}
Write-Output 'ANALYZER_REGRESSION_OK'
