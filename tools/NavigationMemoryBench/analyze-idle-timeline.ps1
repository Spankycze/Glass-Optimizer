param(
 [Parameter(Mandatory)][string]$BaselineGlob,
 [Parameter(Mandatory)][string]$CandidateGlob,
 [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
function Load-Runs([string]$pattern) {
 $paths = @(Get-ChildItem -Path $pattern -File | Sort-Object FullName)
 if ($paths.Count -lt 2) { throw "Need >=2 matching runs: $pattern" }
 $runs = @()
 foreach ($path in $paths) {
  $r=Get-Content -LiteralPath $path.FullName -Raw | ConvertFrom-Json
  if (@($r.idleSamples).Count -lt 2 -or [int]$r.idleTailMs -le 0) {
   throw "Missing idle timeline: $($path.Name)"
  }
  $signature=@([int]$r.cycles,[int]$r.warmupCycles,[int]$r.warmupIdleMs,[int]$r.actions,[int]$r.pauseMs,[int]$r.idleTailMs,[int]$r.idleSampleMs,[bool]$r.profileSections,($r.sections -join '|')) -join ';'
  $runs += [pscustomobject]@{path=$path.Name;run=$r;signature=$signature}
 }
 if (@($runs.signature | Select-Object -Unique).Count -ne 1) {throw "Inconsistent methodology inside $pattern"}
 return ,$runs
}
function Summarize($runs) {
 $rows=@()
 foreach($entry in $runs) {
  $r=$entry.run
  $timeline=@($r.idleSamples)
  $lastWindow=@($timeline | Where-Object { [int]$_.elapsedMs -gt ([int]$r.idleTailMs - 5000) })
  $idleCpu5=($lastWindow | Measure-Object cpuDeltaMs -Sum).Sum
  $peakWs=($timeline | Measure-Object workingSetMiB -Maximum).Maximum
  $peakHandles=($timeline | Measure-Object handles -Maximum).Maximum
  $firstCpu=($timeline | Where-Object {[int]$_.elapsedMs -le 5000} | Measure-Object cpuDeltaMs -Sum).Sum
  $rows += [pscustomobject]@{
    file=$entry.path
    navigationCpuMs=[double]$r.processCpuMs
    navigationWsMiB=[double]$r.finalWsMiB
    idleTotalCpuMs=[double]$r.idleCpuMs
    idleCpuFirst5sMs=[double]$firstCpu
    idleCpuLast5sMs=[double]$idleCpu5
    idlePeakWsMiB=[double]$peakWs
    idleFinalWsMiB=[double]$r.idleFinalWsMiB
    idleWsReleasedFromPeakMiB=[math]::Round($peakWs-[double]$r.idleFinalWsMiB,2)
    idlePeakHandles=[double]$peakHandles
    idleFinalHandles=[double]$r.idleHandleCount
    idleHandlesReleasedFromPeak=[double]$peakHandles-[double]$r.idleHandleCount
  }
 }
 $fields=@('navigationCpuMs','navigationWsMiB','idleTotalCpuMs','idleCpuFirst5sMs','idleCpuLast5sMs','idlePeakWsMiB','idleFinalWsMiB','idleWsReleasedFromPeakMiB','idlePeakHandles','idleFinalHandles','idleHandlesReleasedFromPeak')
 $mean=[ordered]@{}
 foreach($f in $fields) { $mean[$f]=[math]::Round([double]($rows|Measure-Object -Property $f -Average).Average,2) }
 return [ordered]@{runs=$rows.Count;mean=$mean;details=$rows}
}
$baseline=Load-Runs $BaselineGlob;$candidate=Load-Runs $CandidateGlob
if($baseline[0].signature -ne $candidate[0].signature) {throw 'Mixed methodology between groups'}
$a=Summarize $baseline;$b=Summarize $candidate
$delta=[ordered]@{}
foreach($field in @($a.mean.Keys)) {$delta[$field]=[math]::Round([double]$b.mean[$field]-[double]$a.mean[$field],2)}
$result=[ordered]@{status='READ_ONLY_DESCRIPTIVE_NOT_RELEASE_GATE';methodology=$baseline[0].signature;baseline=$a;candidate=$b;candidateMinusBaseline=$delta}
if($Output) {
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Output))|Out-Null
 [IO.File]::WriteAllText($Output,($result|ConvertTo-Json -Depth 9),[Text.UTF8Encoding]::new($false))
}
Write-Output ('IDLE_TIMELINE '+($result|ConvertTo-Json -Compress -Depth 7))
