param([int]$Repeats=4)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$log='C:\ProgramData\SpankyhoOptimizer\performance.log'
if(-not(Test-Path $exe)){throw 'Debug EXE missing'}
$start=if(Test-Path $log){(Get-Item $log).Length}else{0}
1..$Repeats|ForEach-Object{
  $p=Start-Process $exe -ArgumentList '--performance-smoke' -Wait -PassThru
  if($p.ExitCode -ne 0){throw ('performance-smoke exit='+$p.ExitCode)}
}
$fs=[IO.File]::Open($log,'Open','Read','ReadWrite')
try{[void]$fs.Seek($start,'Begin');$sr=[IO.StreamReader]::new($fs);$text=$sr.ReadToEnd();$sr.Dispose()}finally{$fs.Dispose()}
$bad=New-Object Collections.Generic.List[string]
foreach($block in ($text -split '(?=\d{4}-\d{2}-\d{2})')){
  if($block -match 'STORAGE_DEFERRED_PHASE' -and $block -match 'section=gpu|section=startup|section=components'){$bad.Add('storage-deferred-overlap')}
  if($block -match 'GPU_DEFERRED' -and $block -match 'section=startup|section=components'){$bad.Add('gpu-deferred-overlap')}
}
$perf=@($text -split "\r?\n"|Where-Object{$_ -match 'PERF_COLD '})
if($perf.Count -lt $Repeats){throw 'Missing PERF_COLD samples'}
$vals=@{network=@();storage=@();gpu=@();startup=@();services=@()}
foreach($line in $perf){
  foreach($k in @($vals.Keys)){if($line -match ($k+'Ms=(\d+)')){$vals[$k]+=[int]$matches[1]}}
}
$limits=@{network=30;storage=20;gpu=25;startup=20;services=20}
foreach($k in @($vals.Keys)){if(($vals[$k]|Measure-Object -Maximum).Maximum -gt $limits[$k]){$bad.Add($k+'-spike')}}
if($bad.Count){throw ('BACKGROUND_INTERFERENCE_FAIL '+(($bad|Sort-Object -Unique)-join ','))}
Write-Output ('BACKGROUND_INTERFERENCE_OK repeats='+$Repeats+' networkMax='+(($vals.network|Measure-Object -Maximum).Maximum)+' storageMax='+(($vals.storage|Measure-Object -Maximum).Maximum)+' gpuMax='+(($vals.gpu|Measure-Object -Maximum).Maximum))
