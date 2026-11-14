param([Parameter(Mandatory)][string]$InputGlob,[string]$Output='')
$ErrorActionPreference='Stop'
$files=@(Get-ChildItem -Path $InputGlob -File | Sort-Object FullName)
if($files.Count -lt 1){throw "No input JSON files: $InputGlob"}
$results=@()
foreach($f in $files){
 $r=Get-Content -LiteralPath $f.FullName -Raw|ConvertFrom-Json
 $samples=@($r.samples)
 if($samples.Count -lt 8){throw "At least 8 cycles required: $($f.Name)"}
 $width=[math]::Max(2,[int][math]::Floor($samples.Count/4))
 $early=@($samples|Select-Object -First $width)
 $late=@($samples|Select-Object -Last $width)
 $middle=@($samples|Select-Object -Skip ([int][math]::Floor($samples.Count/2)) -First $width)
 # Compare the third and fourth quarters separately; a growing first quarter
 # can hide an eventual plateau in the last half of a long navigation soak.
 $avg={param($rows,$field) [double]($rows|Measure-Object -Property $field -Average).Average}
 $earlyWs=& $avg $early 'workingSetMiB';$lateWs=& $avg $late 'workingSetMiB'
 $earlyPriv=& $avg $early 'privateMiB';$latePriv=& $avg $late 'privateMiB'
 $earlyHandles=& $avg $early 'handles';$lateHandles=& $avg $late 'handles'
 $rows=[ordered]@{
  file=$f.Name; cycles=$samples.Count;actions=[int]$r.actions;quarterWidth=$width
  wsEarlyMiB=[math]::Round($earlyWs,2);wsLateMiB=[math]::Round($lateWs,2)
  wsLateMinusEarlyMiB=[math]::Round($lateWs-$earlyWs,2)
  wsLastQuarterMinusThirdMiB=[math]::Round($lateWs-(& $avg $middle 'workingSetMiB'),2)
  privateLastQuarterMinusThirdMiB=[math]::Round((& $avg $late 'privateMiB')-(& $avg $middle 'privateMiB'),2)
  handlesLastQuarterMinusThird=[math]::Round((& $avg $late 'handles')-(& $avg $middle 'handles'),2)
  privateLateMinusEarlyMiB=[math]::Round($latePriv-$earlyPriv,2)
  handlesLateMinusEarly=[math]::Round($lateHandles-$earlyHandles,2)
  wsEndMiB=[double]$r.finalWsMiB;idleWsEndMiB=$r.idleFinalWsMiB
  idleWsReleasedMiB=if($null -ne $r.idleFinalWsMiB){[math]::Round([double]$r.finalWsMiB-[double]$r.idleFinalWsMiB,2)}else{$null}
  idleHandleCount=$r.idleHandleCount
 }
 $results += [pscustomobject]$rows
}
$summary=[ordered]@{status='DESCRIPTIVE_NOT_LEAK_PROOF';runs=$results.Count;detail=$results}
if($Output){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Output))|Out-Null;[IO.File]::WriteAllText($Output,($summary|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))}
Write-Output ('SOAK_TREND '+($summary|ConvertTo-Json -Compress -Depth 5))
