param(
  [switch]$Force,
  [ValidateSet('Quiet','Normal','Verbose')]
  [string]$Mode='Quiet'
)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=$PSScriptRoot
$src=Join-Path $root 'src'
$ai=Join-Path $root '.ai-state'
$out=Join-Path $root 'PROJECT_INDEX.json'
$mirror=Join-Path $ai 'project-index.json'
$cachePath=Join-Path $ai 'project-index-cache.json'
$metaPath=Join-Path $ai 'project-index-meta.txt'
New-Item -ItemType Directory -Force -Path $ai|Out-Null
$sw=[Diagnostics.Stopwatch]::StartNew()

$hotPatterns=[ordered]@{
  process='ProcessStartInfo|Process\.Start\('
  syncwait='\.Result\b|\.Wait\('
  wmi='Get-CimInstance|Get-WmiObject|System\.Management|ManagementObject'
  registry='Registry(Key)?\.|Microsoft\.Win32'
  taskrun='Task\.Run\('
  dispatcher='DispatcherQueue'
  powershell='powershell\.exe|pwsh\.exe'
  netadapter='Get-NetAdapter|Get-NetIP|Get-DnsClient|netsh\.exe'
  locks='\block\s*\(|SemaphoreSlim|Monitor\.'
}
$declRx='^\s*(?:public|internal|private|protected)?\s*(?:sealed\s+|static\s+|partial\s+|abstract\s+)*(class|record|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)'
$methodRx='^\s*(?:public|internal|private|protected)\s+(?:static\s+|async\s+|virtual\s+|override\s+|sealed\s+)*(?:Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?|void|bool|int|long|string|double|float|decimal|[A-Za-z_][A-Za-z0-9_<>\[\]\?., ]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\('

function Get-SourceFiles {
  $list=[Collections.Generic.List[IO.FileInfo]]::new()
  if(-not(Test-Path $src)){return @()}
  $stack=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
  $stack.Push([IO.DirectoryInfo]::new($src))
  while($stack.Count){
    $dir=$stack.Pop()
    foreach($f in $dir.EnumerateFiles()){
      if($f.Extension -in '.cs','.xaml','.ps1'){$list.Add($f)}
    }
    foreach($child in $dir.EnumerateDirectories()){
      if($child.Name -in 'bin','obj'){continue}
      $stack.Push($child)
    }
  }
  return $list.ToArray()
}

function Parse-File([IO.FileInfo]$f,[string]$rel){
  $classes=@();$methods=@();$xaml=@();$services=@();$hot=@()
  $lines=[IO.File]::ReadAllLines($f.FullName)
  for($i=0;$i -lt $lines.Length;$i++){
    $line=[string]$lines[$i]
    if($f.Extension -eq '.cs'){
      if($line -match $declRx){
        $classes+=,[pscustomobject]@{name=$matches[2];kind=$matches[1];file=$rel;line=$i+1}
        if($matches[2] -like '*Service*'){$services+=,[pscustomobject]@{name=$matches[2];file=$rel;line=$i+1}}
      }
      if($line -match $methodRx){$methods+=,[pscustomobject]@{name=$matches[1];file=$rel;line=$i+1}}
    }elseif($f.Extension -eq '.xaml' -and $line -match 'x:Class="([^"]+)"'){
      $xaml+=,[pscustomobject]@{class=$matches[1];file=$rel;line=$i+1}
    }
    foreach($name in $hotPatterns.Keys){
      if($line -match $hotPatterns[$name]){
        $hot+=,[pscustomobject]@{category=$name;file=$rel;line=$i+1;text=$line.Trim()}
      }
    }
  }
  return [pscustomobject]@{
    path=$rel;length=$f.Length;ticks=$f.LastWriteTimeUtc.Ticks
    classes=$classes;methods=$methods;xaml=$xaml;services=$services;hot=$hot;calls=@()
  }
}

$files=@(Get-SourceFiles)
$metaLines=foreach($f in ($files|Sort-Object FullName)){
  $rel=$f.FullName.Substring($root.Length+1)
  $rel+'|'+$f.Length+'|'+$f.LastWriteTimeUtc.Ticks
}
$metaText=[string]::Join([Environment]::NewLine,[string[]]$metaLines)
if(-not $Force -and (Test-Path $metaPath) -and (Test-Path $out)){
  try{
    if([IO.File]::ReadAllText($metaPath) -eq $metaText){
      $sw.Stop()
      Write-Output ('[INDEX] cache-hit files='+$files.Count+' '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+' ms')
      return
    }
  }catch{}
}

$cacheMap=@{}
if(-not $Force -and (Test-Path $cachePath)){
  try{
    $old=Get-Content $cachePath -Raw -Encoding UTF8|ConvertFrom-Json
    foreach($rec in @($old.files)){$cacheMap[[string]$rec.path]=$rec}
  }catch{$cacheMap=@{}}
}
$oldMethodNames=@($cacheMap.Values|ForEach-Object{@($_.methods)}|ForEach-Object{$_.name}|Sort-Object -Unique)

$currentPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$changed=[Collections.Generic.List[string]]::new()
$fileMap=@{}
foreach($f in $files){
  $rel=$f.FullName.Substring($root.Length+1)
  [void]$currentPaths.Add($rel)
  $fileMap[$rel]=$f
  $rec=$cacheMap[$rel]
  if($Force -or $null -eq $rec -or [int64]$rec.length -ne $f.Length -or [int64]$rec.ticks -ne $f.LastWriteTimeUtc.Ticks){
    $cacheMap[$rel]=Parse-File $f $rel
    $changed.Add($rel)
  }
}
$deleted=@($cacheMap.Keys|Where-Object{-not $currentPaths.Contains($_)})
foreach($rel in $deleted){$cacheMap.Remove($rel)}
if($deleted.Count){$changed.AddRange([string[]]$deleted)}

if(-not $Force -and $changed.Count -eq 0 -and (Test-Path $out)){
  [IO.File]::WriteAllText($metaPath,$metaText,[Text.UTF8Encoding]::new($false))
  $sw.Stop()
  Write-Output ('[INDEX] cache-hit files='+$files.Count+' '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+' ms')
  return
}

$newMethodNames=@($cacheMap.Values|ForEach-Object{@($_.methods)}|ForEach-Object{$_.name}|Sort-Object -Unique)
$oldNamesText=[string]::Join([Environment]::NewLine,[string[]]$oldMethodNames)
$newNamesText=[string]::Join([Environment]::NewLine,[string[]]$newMethodNames)
$methodSetChanged=($oldNamesText -ne $newNamesText)
$methodSet=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($n in $newMethodNames){[void]$methodSet.Add([string]$n)}

$callTargets=if($Force -or $methodSetChanged){
  @($cacheMap.Keys|Where-Object{[IO.Path]::GetExtension($_) -eq '.cs'})
}else{
  @($changed|Where-Object{[IO.Path]::GetExtension($_) -eq '.cs' -and $currentPaths.Contains($_)})
}
foreach($rel in $callTargets){
  $rec=$cacheMap[$rel]
  $f=$fileMap[$rel]
  if($null -eq $f -or $null -eq $rec){continue}
  $calls=[Collections.Generic.List[object]]::new()
  $edgeSet=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  $caller=$null
  $lines=[IO.File]::ReadAllLines($f.FullName)
  for($i=0;$i -lt $lines.Length;$i++){
    $line=[string]$lines[$i]
    if($line -match $methodRx){$caller=$matches[1]}
    if(-not $caller){continue}
    foreach($cm in [regex]::Matches($line,'([A-Za-z_][A-Za-z0-9_]*)\s*\(')){
      $callee=$cm.Groups[1].Value
      if($callee -eq $caller -or -not $methodSet.Contains($callee)){continue}
      $key=$caller+'|'+$callee
      if($edgeSet.Add($key)){$calls.Add([pscustomobject]@{caller=$caller;callee=$callee;file=$rel;line=$i+1})}
    }
  }
  $rec.calls=$calls.ToArray()
}

$records=@($cacheMap.Values|Sort-Object path)
$classes=@($records|ForEach-Object{@($_.classes)})
$methods=@($records|ForEach-Object{@($_.methods)})
$xaml=@($records|ForEach-Object{@($_.xaml)})
$services=@($records|ForEach-Object{@($_.services)})
$calls=@($records|ForEach-Object{@($_.calls)})
$hot=@($records|ForEach-Object{@($_.hot)})
$summary=@($hot|Group-Object category|Sort-Object Name|ForEach-Object{[pscustomobject]@{category=$_.Name;count=$_.Count}})
$latest=if($files.Count){($files|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 1).LastWriteTimeUtc}else{[datetime]::MinValue}
$result=[ordered]@{
  schema=3
  generatedAt=(Get-Date).ToString('o')
  latestSourceUtc=$latest.ToString('o')
  counts=[ordered]@{files=$files.Count;classes=$classes.Count;methods=$methods.Count;xaml=$xaml.Count;services=$services.Count;calls=$calls.Count;hotPatterns=$hot.Count}
  classes=$classes;methods=$methods;xaml=$xaml;services=$services;calls=$calls
  hotSummary=$summary;hotPatterns=$hot
}
$cache=[ordered]@{schema=1;generatedAt=(Get-Date).ToString('o');files=$records}
$json=$result|ConvertTo-Json -Depth 8 -Compress
$cacheJson=$cache|ConvertTo-Json -Depth 9 -Compress

function Write-IfChanged([string]$path,[string]$text){
  if(Test-Path $path){
    try{if([IO.File]::ReadAllText($path) -eq $text){return}}catch{}
  }
  $tmp=$path+'.tmp'
  [IO.File]::WriteAllText($tmp,$text,[Text.UTF8Encoding]::new($false))
  Move-Item $tmp $path -Force
}
Write-IfChanged $out $json
Write-IfChanged $mirror $json
Write-IfChanged $cachePath $cacheJson
Write-IfChanged $metaPath $metaText
$sw.Stop()
$parsedCount=if($Force -or $methodSetChanged){$callTargets.Count}else{$changed.Count}
Write-Output ('[INDEX] changed='+$changed.Count+' deleted='+$deleted.Count+' scanned='+$files.Count+' parsed='+$parsedCount+' '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+' ms')
if($Mode -eq 'Verbose'){
  $changed|Select-Object -First 30|ForEach-Object{Write-Output ('[INDEX_FILE] '+$_)}
}
