param([switch]$UpdateBaseline,[string]$LogPath='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if(-not $LogPath){
  $analyzerDir=Join-Path $root 'artifacts\quality\analyzers'
  $pointer=Join-Path $analyzerDir 'extended-analyzers.latest.txt'
  if(Test-Path $pointer){$LogPath=(Get-Content $pointer -Raw).Trim()}
  if(-not $LogPath){$LogPath=Join-Path $analyzerDir 'extended-analyzers.log'}
}
$baselinePath=Join-Path $root '.ai-state\analyzer-baseline.json'
$pendingBaselinePath=$baselinePath+'.pending'
if(-not(Test-Path $LogPath)){throw 'Analyzer log missing: '+$LogPath}
$findings=New-Object Collections.Generic.List[object]
$trackedRules=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@('CA1416','CA1422','CA1849','CA1851','CA1869') | ForEach-Object {[void]$trackedRules.Add($_)}
$findingRegex=[regex]'^(.*)\((\d+),(\d+)\): (warning|error) ([A-Za-z0-9]+): (.*?)(?: \(https?://.*\))? \['
foreach($line in [IO.File]::ReadLines($LogPath)){
  $m=$findingRegex.Match($line)
  if(-not $m.Success){continue}
  $severity=$m.Groups[4].Value
  $rule=$m.Groups[5].Value
  $familyTracked=[regex]::IsMatch($rule,'^(RCS|MA|VSTHRD|AsyncFixer|PH|CA)[0-9]+$')
  if(-not $trackedRules.Contains($rule) -and -not $familyTracked){continue}
  $file=$m.Groups[1].Value.Trim()
  $file=[regex]::Replace($file,'^\d+>','').Trim()
  if($file.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$file=$file.Substring($root.Length).TrimStart('\')}
  $msg=$m.Groups[6].Value.Trim()
  $normalized=[regex]::Replace($msg,'\s+',' ')
  $fingerprint=($rule+'|'+$file+'|'+$normalized).ToLowerInvariant()
  $findings.Add([pscustomobject]@{severity=$severity;rule=$rule;file=$file;line=[int]$m.Groups[2].Value;message=$msg;fingerprint=$fingerprint})|Out-Null
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
function StableGroupKey($x){
  $f=([string]$x.file).Trim()
  $f=[regex]::Replace($f,'^\d+>','').Trim()
  if($f.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$f=$f.Substring($root.Length).TrimStart('\')}
  return ((([string]$x.rule)+'|'+$f).ToLowerInvariant())
}

$baselineGroups=@{}
foreach($x in @($baseline.findings)){
  $k=StableGroupKey $x
  if(!$baselineGroups.ContainsKey($k)){$baselineGroups[$k]=0}
  $baselineGroups[$k]++
}
$currentGroups=@{}
foreach($x in $unique){
  $k=StableGroupKey $x
  if(!$currentGroups.ContainsKey($k)){$currentGroups[$k]=0}
  $currentGroups[$k]++
}

$newCount=0
$resolvedCount=0
$regressedKeys=New-Object Collections.Generic.List[string]
foreach($k in $currentGroups.Keys){
  $oldCount=if($baselineGroups.ContainsKey($k)){[int]$baselineGroups[$k]}else{0}
  $delta=[int]$currentGroups[$k]-$oldCount
  if($delta -gt 0){$newCount+=$delta;$regressedKeys.Add($k)|Out-Null}
}
foreach($k in $baselineGroups.Keys){
  $now=if($currentGroups.ContainsKey($k)){[int]$currentGroups[$k]}else{0}
  $delta=[int]$baselineGroups[$k]-$now
  if($delta -gt 0){$resolvedCount+=$delta}
}
$new=@($unique|Where-Object{$regressedKeys.Contains((StableGroupKey $_))})
$hot=$unique|Group-Object rule|Sort-Object Count -Descending|Select-Object -First 8
Write-Output ('ANALYZER_REGRESSION_SUMMARY current='+$unique.Count+' baseline='+$baseline.count+' new='+$newCount+' resolved='+$resolvedCount)
$hot|ForEach-Object{Write-Output ('ANALYZER_HOTSPOT '+$_.Name+'='+$_.Count)}
if($resolvedCount){Write-Output ('ANALYZER_IMPROVED resolved='+$resolvedCount)}
if($newCount){
  $new|Select-Object -First 20|ForEach-Object{Write-Output ('ANALYZER_NEW '+$_.rule+' '+$_.file+':'+$_.line+' '+$_.message)}
  exit 42
}
Write-Output 'ANALYZER_REGRESSION_OK'
