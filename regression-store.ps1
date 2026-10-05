param(
  [ValidateSet('add','resolve','list')][string]$Action='list',
  [string]$Kind='',
  [string]$Key='',
  [string]$Message='',
  [string]$Artifact=''
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$path=Join-Path $root '.ai-state\regressions.json'
New-Item -ItemType Directory -Force -Path (Split-Path $path)|Out-Null

$db=[ordered]@{schema=1;updatedAt=(Get-Date).ToString('o');items=@()}
if(Test-Path $path){
  try{$old=[IO.File]::ReadAllText($path)|ConvertFrom-Json;$db.items=@($old.items)}catch{}
}
function Normalize([string]$s){
  $x=$s.Trim().ToLowerInvariant()
  $x=$x -replace '\b\d{4}-\d{2}-\d{2}[t ][0-9:.+-z]+\b','<time>'
  $x=$x -replace '\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b','<guid>'
  $x=$x -replace '\b0x[0-9a-f]+\b','0x#'
  $x=$x -replace '\b\d{2,}\b','#'
  $x=$x -replace '\s+',' '
  return $x
}
if($Action -eq 'add'){
  if(-not $Key){$Key=Normalize ($Kind+'|'+$Message)}
  $item=@($db.items|Where-Object key -eq $Key)|Select-Object -First 1
  if($null -eq $item){
    $item=[pscustomobject][ordered]@{
      key=$Key;kind=$Kind;message=$Message;artifact=$Artifact
      firstSeen=(Get-Date).ToString('o');lastSeen=(Get-Date).ToString('o')
      count=1;resolved=$false;resolvedAt=$null
    }
    $db.items+=,$item
  }else{
    $item.kind=$Kind;$item.message=$Message
    if($Artifact){$item.artifact=$Artifact}
    $item.lastSeen=(Get-Date).ToString('o')
    $item.count=[int]$item.count+1
    $item.resolved=$false;$item.resolvedAt=$null
  }
}elseif($Action -eq 'resolve'){
  foreach($item in @($db.items|Where-Object key -eq $Key)){
    $item.resolved=$true;$item.resolvedAt=(Get-Date).ToString('o')
  }
}elseif($Action -eq 'list'){
  @($db.items|Where-Object{-not [bool]$_.resolved}|Sort-Object lastSeen -Descending|Select-Object -First 20)|ForEach-Object{
    Write-Output ('REGRESSION '+$_.kind+' count='+$_.count+' key='+$_.key+' artifact='+$_.artifact)
  }
  return
}
$db.updatedAt=(Get-Date).ToString('o')
$json=$db|ConvertTo-Json -Depth 6 -Compress
$tmp=$path+'.tmp'
[IO.File]::WriteAllText($tmp,$json,[Text.UTF8Encoding]::new($false))
Move-Item $tmp $path -Force
Write-Output ('REGRESSION_STORE_OK action='+$Action+' key='+$Key)
