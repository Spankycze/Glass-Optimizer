param([ValidateRange(2,15)][int]$Rounds=5)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
if(!(Get-Command node.exe -ErrorAction SilentlyContinue)){throw 'Node not available'}
if(!(Get-Command bun.cmd -ErrorAction SilentlyContinue)){throw 'Bun not available'}
$nodeVer=(& node.exe --version).Trim()
$bunVer=(& bun.cmd --version).Trim()
$lock=Join-Path $root 'package-lock.json'
$lockBefore=(Get-FileHash -LiteralPath $lock -Algorithm SHA256).Hash
$results=@()
function FilesHash {
 $files=@(Get-ChildItem -LiteralPath (Join-Path $root 'devtools/graphics/generated') -File -Recurse | Sort-Object FullName)
 $hashes=@($files | ForEach-Object { [IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/')+' '+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
 $bytes=[Text.Encoding]::UTF8.GetBytes(($hashes -join "`n"))
 $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
 return [pscustomobject]@{Files=$files.Count;Digest=$digest}
}
function Execute([string]$label,[string]$runtime,[int]$round){
 $arguments=@()
 if($runtime -eq 'bun'){$exe='bun.cmd'}else{$exe='node.exe'}
 if($label -eq 'assets'){$arguments=@('devtools/graphics/generate-assets.mjs')}
 elseif($label -eq 'smoke'){$arguments=@('devtools/graphics/smoke.mjs')}
 elseif($label -eq 'addons'){
  if($runtime -eq 'bun'){$arguments=@('test','--path-ignore-patterns=artifacts','devtools/blerp-claim.test.cjs','devtools/chat-booster-markup.test.cjs')}
  else{$arguments=@('--test','devtools/blerp-claim.test.cjs','devtools/chat-booster-markup.test.cjs')}
 }else{throw 'Unsupported label'}
 $timer=[Diagnostics.Stopwatch]::StartNew()
 $output=@(& $exe @arguments 2>&1 | Out-String)
 $code=$LASTEXITCODE
 $timer.Stop()
 if($code -ne 0){throw ('BENCHMARK_FAILED '+$label+' '+$runtime+' round='+$round+' exit='+$code+' '+($output|Out-String))}
 $outText=[string]($output -join '')
 if($label -eq 'assets' -and !$outText.Contains('GRAPHICS_ASSETS_GENERATED')){throw 'Missing assets signal'}
 if($label -eq 'smoke' -and !$outText.Contains('GRAPHICS_TOOLCHAIN_SMOKE_PASS')){throw 'Missing smoke signal'}
 if($label -eq 'addons'){
  $clean=[regex]::Replace($outText, "\x1B\[[0-9;]*m", '')
  if($runtime -eq 'bun'){
   if($clean -notmatch '(?m)^\s*12 pass\b' -or $clean -notmatch '(?m)^\s*0 fail\b'){throw ('Bun addon count mismatch '+$clean)}
  }else{
   if($clean -notmatch '(?m)^\s*(?:ℹ\s*)?tests\s+12\s*$' -or $clean -notmatch '(?m)^\s*(?:ℹ\s*)?pass\s+12\s*$'){throw ('Node addon count mismatch '+$clean)}
  }
 }
 $assetHash=$null
 if($label -eq 'assets'){$assetHash=FilesHash}
 $results+= [pscustomobject]@{Task=$label;Runtime=$runtime;Round=$round;Ms=[Math]::Round($timer.Elapsed.TotalMilliseconds,2);Files=$assetHash.Files;Hash=$assetHash.Digest;Exit=$code}
 Write-Host ('BENCH '+$label+' '+$runtime+' round='+$round+' ms='+([Math]::Round($timer.Elapsed.TotalMilliseconds,2))+' files='+$assetHash.Files)
 return $results[-1]
}
$all=@()
$tasks=@('assets','smoke')
if((Split-Path $root -Leaf) -eq 'RAM Guard'){$tasks+= 'addons'}
foreach($task in $tasks){
 for($i=1;$i -le $Rounds;$i++){
  $order=if(($i%2) -eq 0){@('bun','node')}else{@('node','bun')}
  foreach($runtime in $order){$all+=Execute $task $runtime $i}
 }
}
foreach($task in $tasks){
 foreach($runtime in @('node','bun')){
  $a=@($all|Where-Object {$_.Task -eq $task -and $_.Runtime -eq $runtime}|Sort-Object Ms)
  $med=$a[[int][Math]::Floor($a.Count/2)].Ms
  Write-Host "MEDIAN $task $runtime $med"
 }
}
$assets=@($all|Where-Object {$_.Task -eq 'assets'})
$digests=@($assets|Select-Object -ExpandProperty Hash -Unique)
$counts=@($assets|Select-Object -ExpandProperty Files -Unique)
if($digests.Count -ne 1 -or $counts.Count -ne 1 -or $counts[0] -le 0){throw ('GRAPHICS_PARITY_FAIL hashes='+$digests.Count+' files='+($counts -join ','))}
if((Get-FileHash -LiteralPath $lock -Algorithm SHA256).Hash -ne $lockBefore){throw 'LOCKFILE_HASH_CHANGED'}
$report=[pscustomobject]@{Time=(Get-Date).ToString('o');Project=$root;Node=$nodeVer;Bun=$bunVer;Rounds=$Rounds;OutputsIdentical=$true;Files=$counts[0];OutputHash=$digests[0];LockfileUnchanged=$true;Runs=$all}
$path=Join-Path $PSScriptRoot 'js-runtime-benchmark-latest.json'
$report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding utf8
'BENCHMARK_COMPLETE '+$path
