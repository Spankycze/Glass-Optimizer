param([int]$Tail=2500)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$log='C:\ProgramData\SpankyhoOptimizer\performance.log'
$benchPath=Join-Path $root '.perf\latest\bench.json'
if(-not(Test-Path $log)){throw 'performance.log missing'}

$lines=@()
$useBench=$false
$bench=$null
if(Test-Path $benchPath){
  try{
    $bench=Get-Content $benchPath -Raw|ConvertFrom-Json
    if($null -ne $bench.generated){
      $benchAge=((Get-Date)-([datetimeoffset]$bench.generated).LocalDateTime).TotalMinutes
      $useBench=($benchAge -ge 0 -and $benchAge -le 30)
    }
  }catch{$useBench=$false}
}

if($useBench){
  $start=0L
  if($null -ne $bench.performanceLogStartBytes){$start=[int64]$bench.performanceLogStartBytes}
  $fs=[IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
  try{
    if($start -ge 0 -and $start -le $fs.Length){[void]$fs.Seek($start,[IO.SeekOrigin]::Begin)}
    $sr=[IO.StreamReader]::new($fs,[Text.Encoding]::UTF8,$true,4096,$true)
    try{$text=$sr.ReadToEnd()}finally{$sr.Dispose()}
  }finally{$fs.Dispose()}
  $lines=@($text -split "\r?\n"|Where-Object{$_})
  $benchEnd=[datetimeoffset]$bench.generated
  $lines=@($lines|Where-Object{
    if($_ -match '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) '){
      try { ([datetimeoffset]::Parse($matches.ts)).ToUniversalTime() -le $benchEnd.ToUniversalTime() } catch { $true }
    } else { $true }
  })
}else{
  $lines=Get-Content $log -Tail $Tail
  try{
    $exe=& (Join-Path $root 'resolve-debug-exe.ps1')
    if(Test-Path $exe){
      $buildStart=(Get-Item $exe).LastWriteTime.AddSeconds(-5)
      $lines=@($lines|Where-Object{
        if($_ -match '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) '){
          try { [datetime]::Parse($matches.ts) -ge $buildStart } catch { $true }
        } else { $true }
      })
    }
  }catch{}
}

$budgets=@{
 'NETWORK_BUILD'=15;'STORAGE_FOREGROUND_PHASE'=10;'STORAGE_BUILD'=10;'GPU_UI_PHASE'=15;
 'STARTUP_NAV_PHASE'=15;'COMPONENT_NAV_PHASE'=18;'DEFENDER_UI_PHASE'=15;'RESTORE_UI_PHASE'=15;'APPS_NAV_PHASE'=10
}
$rows=@();$fail=@();$noData=@()
foreach($name in $budgets.Keys){
  $hits=@($lines|Where-Object{$_ -match [regex]::Escape($name)})
  $max=0
  foreach($line in $hits){if($line -match 'totalMs=(\d+)'){$max=[math]::Max($max,[int]$matches[1])}}
  $status=if($hits.Count -eq 0){'NO_DATA'}elseif($max -gt $budgets[$name]){'FAIL'}else{'OK'}
  $rows+=[pscustomobject]@{phase=$name;maxMs=$max;budgetMs=$budgets[$name];samples=$hits.Count;status=$status}
  if($status -eq 'FAIL'){$fail+=$name}
  elseif($status -eq 'NO_DATA'){$noData+=$name}
}
$rows|Format-Table -AutoSize|Out-String|Write-Output
if($noData.Count){throw ('UI_THREAD_BUDGET_NO_SAMPLES '+($noData -join ','))}
if($fail.Count){throw ('UI_THREAD_BUDGET_FAIL '+($fail -join ','))}
Write-Output ('UI_THREAD_BUDGET_OK lines='+$lines.Count)
