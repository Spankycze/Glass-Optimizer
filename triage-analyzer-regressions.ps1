$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$baselinePath=Join-Path $root '.ai-state\analyzer-baseline.json'
$pointer=Join-Path $root 'artifacts\quality\analyzers\extended-analyzers.latest.txt'
if(-not(Test-Path $pointer)){throw "Missing extended analyzer log pointer"}
$log=(Get-Content $pointer -Raw).Trim()
if(-not(Test-Path $log)){throw "Missing analyzer log $log"}
$baseline=Get-Content $baselinePath -Raw | ConvertFrom-Json
$regex=[regex]'^(.*)\((\d+),(\d+)\): (warning|error) ([A-Za-z0-9]+): (.*?)(?: \(https?://.*\))? \['
$unique=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$now=@{}
foreach($line in [IO.File]::ReadLines($log)){
    $m=$regex.Match($line)
    if(-not $m.Success){continue}
    $rule=$m.Groups[5].Value
    if($rule -notmatch '^(RCS|MA|VSTHRD|AsyncFixer|PH|CA)[0-9]+$'){continue}
    $file=$m.Groups[1].Value.Trim()
    $file=[regex]::Replace($file,'^\d+>','')
    if($file.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$file=$file.Substring($root.Length).TrimStart('\')}
    $msg=[regex]::Replace($m.Groups[6].Value.Trim(),'\s+',' ')
    $fingerprint=($rule+'|'+$file+'|'+$msg).ToLowerInvariant()
    if(-not $unique.Add($fingerprint)){continue}
    $key=($rule+'|'+$file).ToLowerInvariant()
    if(-not $now.ContainsKey($key)){$now[$key]=0}
    $now[$key]++
}
$before=@{}
foreach($f in @($baseline.findings)){
    $file=[string]$f.file
    $file=[regex]::Replace($file.Trim(),'^\d+>','')
    if($file.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$file=$file.Substring($root.Length).TrimStart('\')}
    $key=(([string]$f.rule)+'|'+$file).ToLowerInvariant()
    if(-not $before.ContainsKey($key)){$before[$key]=0}
    $before[$key]++
}
$rows=@(foreach($key in $now.Keys){
    $old=if($before.ContainsKey($key)){$before[$key]}else{0}
    $delta=$now[$key]-$old
    if($delta -gt 0){
        $split=$key.IndexOf('|')
        [pscustomobject]@{rule=$key.Substring(0,$split).ToUpperInvariant();file=$key.Substring($split+1);new=$delta;current=$now[$key];baseline=$old}
    }
})
$out=Join-Path $root 'artifacts\quality\analyzer-regression-triage.json'
$rows|Sort-Object new -Descending | ConvertTo-Json -Depth 5 | Set-Content $out -Encoding utf8
Write-Output ('TRIAGE currentUnique='+$unique.Count+' new='+[int](($rows|Measure-Object new -Sum).Sum)+' files='+$rows.Count)
$rows|Group-Object rule|ForEach-Object{[pscustomobject]@{Rule=$_.Name;New=[int](($_.Group|Measure-Object new -Sum).Sum);Files=$_.Count}}|Sort-Object New -Descending|Select-Object -First 22|Format-Table -AutoSize
$rows|Sort-Object new -Descending|Select-Object -First 18|Format-Table -AutoSize
Write-Output ('TRIAGE_REPORT '+$out)
