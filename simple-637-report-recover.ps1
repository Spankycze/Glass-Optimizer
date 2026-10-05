$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\simple-637'
foreach($label in @('before','after')){
 $f=Join-Path $dir ($label+'-performance.json')
 $o=ConvertFrom-Json ([IO.File]::ReadAllText($f))
 foreach($sample in $o.samples){$sample.lines=@($sample.lines|ForEach-Object {if($_ -is [string]){$_}else{[string]$_.value}})}
 [IO.File]::WriteAllText($f,($o|ConvertTo-Json -Depth 8))
}
$f=Join-Path $PWD 'simple-637-measure.ps1'
$t=[IO.File]::ReadAllText($f).Replace('$rows+=@{repeat=$i;flag=$flag;lines=$lines}','$rows+=@{repeat=$i;flag=$flag;lines=@($lines|ForEach-Object {$_.ToString()})}')
[IO.File]::WriteAllText($f,$t)
& .\ai-workflow.ps1 -Action checkpoint -TaskId 'simple-637' -Summary 'Installed637 validated. Report parser failed because PS5 Get-Content strings serialized provider metadata; recovered original line.value into plain strings, no rerun or fabricated timings.' -NextAction 'Complete final verified comparison and save handoff.' -Done @('Release637 installed','Fan agent healthy','Six suites','Before/after3samples') -Pending @('Final comparison summary') -Evidence @('artifacts\simple-637\before-performance.json','artifacts\simple-637\after-performance.json')
& .\simple-637-final.ps1
