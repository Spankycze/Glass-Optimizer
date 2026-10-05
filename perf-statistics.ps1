param([string]$ReportPath,[int]$Window=8,[switch]$NoAppend)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($ReportPath)){$ReportPath=Join-Path $root 'artifacts\perf\latest.json'}
$historyPath=Join-Path $root '.ai-state\perf-history.json'
$outPath=Join-Path $root 'artifacts\perf\statistics-latest.json'
if(-not(Test-Path $ReportPath)){throw 'Missing report: '+$ReportPath}
$current=Get-Content $ReportPath -Raw|ConvertFrom-Json
if(Test-Path $historyPath){try{$history=Get-Content $historyPath -Raw|ConvertFrom-Json}catch{$history=$null}}
if($null -eq $history){$history=[pscustomobject]@{schema=1;runs=@()}}
if(@($history.runs).Count -eq 0){
  $seed=@()
  Get-ChildItem (Join-Path $root 'artifacts\perf') -Recurse -File -Filter 'report.json' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime |
    ForEach-Object {
      try{
        $candidate=Get-Content $_.FullName -Raw|ConvertFrom-Json
        if($candidate.generatedAt -and $candidate.startup.samples){$seed+=$candidate}
      }catch{}
    }
  if($seed.Count -gt 30){$seed=@($seed|Select-Object -Last 30)}
  $history=[pscustomobject]@{schema=1;runs=$seed}
}
function Median([double[]]$v){if(-not $v -or $v.Count -eq 0){return 0};$s=@($v|Sort-Object);if($s.Count%2){return [double]$s[[int]($s.Count/2)]};return ([double]$s[$s.Count/2-1]+[double]$s[$s.Count/2])/2}
function Mad([double[]]$v){if(-not $v -or $v.Count -eq 0){return 0};$m=Median $v;return Median @($v|ForEach-Object{[math]::Abs([double]$_-$m)})}
function Percentile([double[]]$v,[double]$p){if(-not $v -or $v.Count -eq 0){return 0};$s=@($v|Sort-Object);if($s.Count -eq 1){return [double]$s[0]};$rank=($p/100.0)*($s.Count-1);$lo=[math]::Floor($rank);$hi=[math]::Ceiling($rank);if($lo -eq $hi){return [double]$s[[int]$lo]};$w=$rank-$lo;return ([double]$s[[int]$lo]*(1-$w))+([double]$s[[int]$hi]*$w)}
function Variance([double[]]$v){if(-not $v -or $v.Count -lt 2){return 0};$mean=($v|Measure-Object -Average).Average;$sum=0.0;foreach($x in $v){$d=[double]$x-[double]$mean;$sum+=$d*$d};return $sum/($v.Count-1)}
function Phi([double]$z){$x=[math]::Abs($z);$t=1/(1+0.2316419*$x);$d=0.3989422804014327*[math]::Exp(-$x*$x/2);$p=1-$d*$t*(0.319381530+$t*(-0.356563782+$t*(1.781477937+$t*(-1.821255978+$t*1.330274429))));if($z -lt 0){return 1-$p};return $p}
function MannWhitneyP([double[]]$a,[double[]]$b){
  if($a.Count -lt 2 -or $b.Count -lt 2){return 1.0}
  $rows=@();foreach($x in $a){$rows+=[pscustomobject]@{v=[double]$x;g='a'}};foreach($x in $b){$rows+=[pscustomobject]@{v=[double]$x;g='b'}}
  $rows=@($rows|Sort-Object v);$rankSum=0.0;$i=0
  while($i -lt $rows.Count){$j=$i;while($j+1 -lt $rows.Count -and [double]$rows[$j+1].v -eq [double]$rows[$i].v){$j++};$rank=(($i+1)+($j+1))/2.0;for($k=$i;$k -le $j;$k++){if($rows[$k].g -eq 'a'){$rankSum+=$rank}};$i=$j+1}
  $n1=$a.Count;$n2=$b.Count;$u=$rankSum-$n1*($n1+1)/2.0;$mean=$n1*$n2/2.0;$sd=[math]::Sqrt($n1*$n2*($n1+$n2+1)/12.0);if($sd -le 0){return 1.0};$z=($u-$mean)/$sd;$p=2*(1-(Phi ([math]::Abs($z))));return [math]::Max(0,[math]::Min(1,$p))
}
function GetSamples($r,[string]$metric){
  if($metric -eq 'startup'){return @($r.startup.samples|ForEach-Object{[double]$_})}
  $parts=$metric.Split('.');$bucket=$parts[0];$name=$parts[1];return @($r.$bucket.$name.samples|ForEach-Object{[double]$_})
}
$metrics=@('startup')+@('network','storage','gpu','startup','services','defender','debloat','power'|ForEach-Object{'cold.'+$_})+@('network','storage','gpu','startup','services','defender','debloat','power'|ForEach-Object{'warm.'+$_})
$prior=@($history.runs|Where-Object {$_.generatedAt -ne $current.generatedAt}|Select-Object -Last $Window)
$results=@()
foreach($m in $metrics){
  $cur=@(GetSamples $current $m);$base=@();foreach($run in $prior){$base+=@(GetSamples $run $m)}
  $curMed=Median $cur;$baseMed=Median $base;$curP95=Percentile $cur 95;$baseP95=Percentile $base 95;$baseMad=Mad $base;$baseVar=Variance $base;$p=if($base.Count -ge 6){MannWhitneyP $cur $base}else{1.0}
  $pct=if($baseMed -gt 0){(($curMed-$baseMed)/$baseMed)*100}else{0}
  $p95Pct=if($baseP95 -gt 0){(($curP95-$baseP95)/$baseP95)*100}else{0}
  $noisePct=if($baseMed -gt 0){(3*$baseMad/$baseMed)*100}else{0}
  $variancePct=if($baseMed -gt 0){([math]::Sqrt([math]::Max(0,$baseVar))/$baseMed)*100}else{0}
  $threshold=[math]::Max(5.0,[math]::Max($noisePct,2*$variancePct))
  $medianDeltaMs=$curMed-$baseMed;$p95DeltaMs=$curP95-$baseP95
  # Sub-10 ms section timings are integer-quantized; a 1 ms move is normal timer noise
  # (e.g. 5 -> 6 ms looks like +20%). Require at least 2 ms absolute median drift.
  $reg=($base.Count -ge 6 -and $p -lt 0.05 -and (($pct -gt $threshold -and $medianDeltaMs -ge 2.0) -or ($p95Pct -gt ($threshold*1.25) -and $p95DeltaMs -ge 2.0)))
  $series=@($prior|ForEach-Object{[double](Median @(GetSamples $_ $m))})+$curMed
  $cp=$null
  if($series.Count -ge 6){$best=$null;for($s=3;$s -le $series.Count-3;$s++){ $left=@($series[0..($s-1)]);$right=@($series[$s..($series.Count-1)]);$lm=Median $left;$rm=Median $right;$mad=[math]::Max(0.1,(Mad @($left+$right)));$score=[math]::Abs($rm-$lm)/$mad;$delta=if($lm -gt 0){(($rm-$lm)/$lm)*100}else{0};if($null -eq $best -or $score -gt $best.score){$best=[pscustomobject]@{split=$s;before=$lm;after=$rm;deltaPct=$delta;score=$score}}};if($best -and ([math]::Abs([double]$best.after-[double]$best.before) -ge 2.0) -and $best.deltaPct -gt 8 -and $best.score -ge 3){$cp=$best}}
  $results+=[pscustomobject]@{metric=$m;currentMedian=[math]::Round($curMed,3);baselineMedian=[math]::Round($baseMed,3);currentP95=[math]::Round($curP95,3);baselineP95=[math]::Round($baseP95,3);baselineMad=[math]::Round($baseMad,3);baselineVariance=[math]::Round($baseVar,4);deltaPct=[math]::Round($pct,2);p95DeltaPct=[math]::Round($p95Pct,2);pValue=[math]::Round($p,4);thresholdPct=[math]::Round($threshold,2);regression=$reg;changePoint=$cp}
}
$summary=[pscustomobject]@{generatedAt=(Get-Date).ToString('o');window=$Window;priorRuns=$prior.Count;regressions=@($results|Where-Object regression);changePoints=@($results|Where-Object {$null -ne $_.changePoint});metrics=$results}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath)|Out-Null
$summaryJson=$summary|ConvertTo-Json -Depth 10
try{$summaryJson|Set-Content $outPath -Encoding UTF8}
catch [IO.IOException]{$fallback=[IO.Path]::ChangeExtension($outPath,'.pending.json');$summaryJson|Set-Content $fallback -Encoding UTF8;Write-Output ('PERF_STAT_OUTPUT_LOCKED fallback='+$fallback)}
foreach($x in @($summary.regressions)){Write-Output ('PERF_STAT_REGRESSION metric={0} delta={1:n1}% p={2} baseline={3} current={4}' -f $x.metric,$x.deltaPct,$x.pValue,$x.baselineMedian,$x.currentMedian)}
foreach($x in @($summary.changePoints)){Write-Output ('PERF_CHANGE_POINT metric={0} delta={1:n1}% score={2:n2}' -f $x.metric,$x.changePoint.deltaPct,$x.changePoint.score)}
if(-not $NoAppend){$newRuns=@($history.runs|Where-Object {$_.generatedAt -ne $current.generatedAt})+$current;if($newRuns.Count -gt 30){$newRuns=@($newRuns|Select-Object -Last 30)};$history=[pscustomobject]@{schema=1;runs=$newRuns};$history|ConvertTo-Json -Depth 12|Set-Content $historyPath -Encoding UTF8}
Write-Output ('PERF_STAT_OK priorRuns={0} regressions={1} changePoints={2} output={3}' -f $prior.Count,@($summary.regressions).Count,@($summary.changePoints).Count,$outPath)