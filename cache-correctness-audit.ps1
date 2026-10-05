param([switch]$Quick)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(-not(Test-Path $exe)){throw 'Debug EXE missing'}
$args=@('--resource-cache-smoke-bundle')
if(-not $Quick){$args+=@('--cache-cleanup-smoke','--debloat-cache-smoke','--bcd-cache-smoke')}
$rows=@()
foreach($arg in $args){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $p=Start-Process $exe -ArgumentList $arg -Wait -PassThru
  $sw.Stop()
  $rows+=[pscustomobject]@{smoke=$arg;exit=$p.ExitCode;ms=[math]::Round($sw.Elapsed.TotalMilliseconds,1)}
  if($p.ExitCode -ne 0){throw ('CACHE_CORRECTNESS_FAIL '+$arg+' exit='+$p.ExitCode)}
}
$cacheFiles=@(Get-ChildItem (Join-Path $root 'src\SpankyhoOptimizer') -File -Filter '*PageCache.cs')
$parity=@()
foreach($f in $cacheFiles){
  $t=[IO.File]::ReadAllText($f.FullName)
  $parity+=[pscustomobject]@{file=$f.Name;attach=($t -match 'TryAttach');capture=($t -match 'Capture');invalidate=($t -match 'Invalidate')}
}
$weak=@($parity|Where-Object{-not $_.attach -or -not $_.capture})
if($weak.Count){Write-Output ('CACHE_PARITY_WARN files='+(($weak.file)-join ','))}
$rows|Format-Table -AutoSize|Out-String|Write-Output
Write-Output ('CACHE_CORRECTNESS_OK smokes='+$rows.Count+' pageCacheFiles='+$cacheFiles.Count+' parityWarnings='+$weak.Count)
