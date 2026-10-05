param([int]$CriticalRepeats=3,[int]$NavRepeats=2)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$log='C:\ProgramData\SpankyhoOptimizer\perf.log'
if(-not(Test-Path $exe)){throw 'Debug EXE missing'}

function Run-Smoke([string]$arg,[string]$marker,[int]$repeat){
  $rows=@()
  for($i=1;$i -le $repeat;$i++){
    $start=if(Test-Path $log){(Get-Item $log).Length}else{0}
    $p=Start-Process $exe -ArgumentList $arg -Wait -PassThru
    if($p.ExitCode -ne 0){throw ($arg+' exit='+$p.ExitCode)}
    $fs=[IO.File]::Open($log,'Open','Read','ReadWrite')
    try{[void]$fs.Seek($start,'Begin');$sr=[IO.StreamReader]::new($fs);$text=$sr.ReadToEnd();$sr.Dispose()}finally{$fs.Dispose()}
    $line=($text -split "\r?\n"|Where-Object{$_ -match $marker}|Select-Object -Last 1)
    if([string]::IsNullOrWhiteSpace([string]$line)){throw ('Missing '+$marker+' log line')}
    $m=[ordered]@{run=$i;ws=0;private=0;managed=0;handles=0}
    if($line -match 'wsDelta=(-?\d+)MB'){$m.ws=[int]$matches[1]}
    if($line -match 'privateDelta=(-?\d+)MB'){$m.private=[int]$matches[1]}
    if($line -match 'managedDelta=(-?\d+)MB'){$m.managed=[int]$matches[1]}
    if($line -match 'handleDelta=(-?\d+)'){$m.handles=[int]$matches[1]}
    $rows+=[pscustomobject]$m
  }
  return $rows
}

function Stats($rows,[string]$name){
  $vals=@($rows|ForEach-Object{[double]$_.$name}|Sort-Object)
  if(-not $vals.Count){return [pscustomobject]@{median=0;p95=0;max=0}}
  $mid=$vals[[math]::Floor(($vals.Count-1)/2)]
  $p95Index=[math]::Max(0,[math]::Min($vals.Count-1,[math]::Ceiling($vals.Count*0.95)-1))
  $p95=$vals[$p95Index]
  [pscustomobject]@{median=$mid;p95=$p95;max=$vals[-1]}
}

$critical=Run-Smoke '--critical-leak-smoke' 'CRITICAL_LEAK_AUDIT navigations=' $CriticalRepeats
$nav=Run-Smoke '--nav500-smoke' 'NAV500_AUDIT navigations=' $NavRepeats
$summary=[ordered]@{
 generatedAt=(Get-Date).ToString('o')
 critical=[ordered]@{
   runs=$critical
   ws=Stats $critical 'ws'
   private=Stats $critical 'private'
   managed=Stats $critical 'managed'
   handles=Stats $critical 'handles'
 }
 nav500=[ordered]@{
   runs=$nav
   ws=Stats $nav 'ws'
   private=Stats $nav 'private'
   managed=Stats $nav 'managed'
   handles=Stats $nav 'handles'
 }
}
$out=Join-Path $root 'artifacts\quality\memory-retention-latest.json'
New-Item -ItemType Directory -Force -Path (Split-Path $out)|Out-Null
$summary|ConvertTo-Json -Depth 8|Set-Content $out -Encoding UTF8

$fail=@()
if($summary.critical.private.p95 -gt 48){$fail+='critical-private'}
if($summary.critical.managed.p95 -gt 16){$fail+='critical-managed'}
if($summary.critical.handles.p95 -gt 80){$fail+='critical-handles'}
if($summary.nav500.private.p95 -gt 40){$fail+='nav500-private'}
if($summary.nav500.managed.p95 -gt 8){$fail+='nav500-managed'}
if($summary.nav500.handles.p95 -gt 100){$fail+='nav500-handles'}

Write-Output ('MEMORY_RETENTION critical privateP95='+$summary.critical.private.p95+'MB managedP95='+$summary.critical.managed.p95+'MB handlesP95='+$summary.critical.handles.p95)
Write-Output ('MEMORY_RETENTION nav500 privateP95='+$summary.nav500.private.p95+'MB managedP95='+$summary.nav500.managed.p95+'MB handlesP95='+$summary.nav500.handles.p95)
if($fail.Count){throw ('MEMORY_RETENTION_FAIL '+($fail -join ','))}
Write-Output ('MEMORY_RETENTION_OK report='+$out)
