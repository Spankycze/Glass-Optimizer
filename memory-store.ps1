param(
  [ValidateSet('add','search','prune','list')][string]$Action='list',
  [string]$Text='',[string]$Scope='project',[string]$TaskId='',[string[]]$Tags=@(),
  [int]$TtlDays=0,[string]$Query=''
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$path=Join-Path $root '.ai-state\MEMORY.json'
function Load-Db {
  if(Test-Path $path){try{return Get-Content $path -Raw -Encoding UTF8|ConvertFrom-Json}catch{}}
  return [pscustomobject][ordered]@{schema=1;updatedAt=(Get-Date).ToString('o');items=@()}
}
function Save-Db($db){
  $db.updatedAt=(Get-Date).ToString('o')
  $tmp=$path+'.tmp';[IO.File]::WriteAllText($tmp,($db|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false));Move-Item $tmp $path -Force
}
$db=Load-Db
$now=Get-Date
$db.items=@($db.items|Where-Object{-not $_.expiresAt -or [datetime]$_.expiresAt -gt $now})
switch($Action){
 'add' {
   if([string]::IsNullOrWhiteSpace($Text)){throw 'Text is required'}
   $item=[pscustomobject][ordered]@{
     id='mem-'+(Get-Date -Format 'yyyyMMddHHmmssfff');text=$Text;scope=$Scope;taskId=$TaskId
     tags=@($Tags);createdAt=$now.ToString('o');lastAccessedAt=$now.ToString('o')
     expiresAt=if($TtlDays -gt 0){$now.AddDays($TtlDays).ToString('o')}else{$null}
   }
   $db.items=@($db.items)+$item;Save-Db $db;Write-Output ('MEMORY_ADDED '+$item.id)
 }
 'search' {
   $terms=@($Query.ToLowerInvariant().Split(' ',[StringSplitOptions]::RemoveEmptyEntries))
   $hits=@($db.items|ForEach-Object{
     $hay=([string]$_.text+' '+(@($_.tags)-join ' ')+' '+[string]$_.scope+' '+[string]$_.taskId).ToLowerInvariant()
     $score=0;foreach($t in $terms){if($hay.Contains($t)){$score++}}
     if($score -gt 0){[pscustomobject]@{score=$score;item=$_}}
   }|Sort-Object score -Descending|Select-Object -First 20)
   foreach($h in $hits){$h.item.lastAccessedAt=$now.ToString('o');Write-Output ('MEMORY_HIT score='+$h.score+' id='+$h.item.id+' scope='+$h.item.scope+' text='+$h.item.text)}
   Save-Db $db
 }
 'prune' {Save-Db $db;Write-Output ('MEMORY_PRUNE_OK count='+@($db.items).Count)}
 'list' {$db.items|Sort-Object createdAt -Descending|Select-Object -First 20|ForEach-Object{Write-Output ('MEMORY '+$_.id+' | '+$_.scope+' | '+$_.text)}}
}
