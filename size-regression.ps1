param([ValidateSet('record','check')][string]$Mode='check',[double]$RegressionPct=12)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$dir=Join-Path $root '.size-baseline';New-Item -ItemType Directory -Force $dir|Out-Null
$targets=@(
 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.dll',
 'src\SpankyhoOptimizer.RuntimeHost\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.RuntimeHost.dll',
 'src\SpankyhoOptimizer.Core\bin\Release\net10.0-windows10.0.26100.0\SpankyhoOptimizer.Core.dll'
)
$rows=@()
foreach($rel in $targets){$p=Join-Path $root $rel;if(Test-Path $p){$rows+=[pscustomobject]@{path=$rel;bytes=(Get-Item $p).Length}}}
$latest=Join-Path $dir 'latest.json';$base=Join-Path $dir 'baseline.json'
$rows|ConvertTo-Json|Set-Content $latest -Encoding UTF8
if($Mode -eq 'record' -or !(Test-Path $base)){Copy-Item $latest $base -Force;Write-Host 'SPANKY_SIZE_BASELINE_RECORDED';exit 0}
$b=@(Get-Content $base -Raw|ConvertFrom-Json)
$fail=$false
foreach($r in $rows){$x=$b|Where-Object path -eq $r.path|Select-Object -First 1;if(!$x){continue};$pct=(($r.bytes-$x.bytes)/[double]$x.bytes)*100;Write-Host ('SIZE '+$r.path+' '+$r.bytes+' delta='+[math]::Round($pct,1)+'%');if($pct -gt $RegressionPct){$fail=$true}}
if($fail){exit 41};Write-Host 'SPANKY_SIZE_REGRESSION_OK'
