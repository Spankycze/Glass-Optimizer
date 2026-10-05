$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\simple-637'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.637*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
if(!(Get-Process -Id $d.processId -ErrorAction SilentlyContinue)){throw 'Fan agent no longer alive'}
if((Get-FileHash $r.desktopInstaller).Hash -ne $r.sha256){throw 'Desktop readback mismatch'}
if(@(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File).Count -ne 1){throw 'Multiple Desktop installers'}
function Med($v){$a=@($v|Sort-Object);if($a.Count -ne 3){throw 'Expected three fresh samples'};return $a[1]}
function Metrics($o){
 $all=@($o.samples|ForEach-Object {$_.lines})
 $out=[ordered]@{}
 foreach($kind in @('PERF_COLD','PERF_WARM','PERF_MEMORY','STARTUP_HOTPATH_SMOKE_OK')){
  $lines=@($all|Where-Object {$_ -match $kind})
  $bucket=@{}
  foreach($line in $lines){foreach($m in [regex]::Matches($line,'(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?\d+(?:[.,]\d+)?)')){$key=$m.Groups[1].Value;$value=[double]::Parse($m.Groups[2].Value.Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture);if($bucket.ContainsKey($key)){$bucket[$key]=@($bucket[$key])+$value}else{$bucket[$key]=@($value)}}}
  $result=[ordered]@{}
  foreach($key in @($bucket.Keys|Sort-Object)){$result[$key]=Med $bucket[$key]}
  $out[$kind]=$result
 }
 return $out
}
$b=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'before-performance.json')))
$a=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'after-performance.json')))
if($b.version -ne '15.4.636' -or $a.version -ne '15.4.637'){throw 'Performance version mismatch'}
$report=[ordered]@{beforeVersion=$b.version;afterVersion=$a.version;repeats=3;beforeMedian=(Metrics $b);afterMedian=(Metrics $a);scope='Installed Release builds, three samples each; build/navigation timing, not FPS or render-to-display latency.';structural='Three fewer always-visible UI nodes per workspace surface; no page/navigation composition animations or their dispatcher callbacks.'}
$report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $dir 'comparison.json') -Encoding UTF8
Write-Output ($report|ConvertTo-Json -Depth 10)
& .\ai-workflow.ps1 -Action complete-task -TaskId 'simple-637' -Summary 'Installed637: simpler compact panels, accessible tooltip descriptions, immediate navigation without page/selection motion. Fresh six suites, gated release and verified healthy HID fan agent. Release636/637 three-sample timings saved in comparison.json; no sweeping speed claim.' -NextAction 'Simplification complete. Source638 automatic bump untested. Further optimization should target a measured UI-thread spike from comparison, preserve Overview header/hardware.' -Done @('Compact accessible panels','Page and navigation animation overhead removed','Six suites and gated release/install','Three installed Release samples before/after') -Evidence @('artifacts\simple-637\validation-result.json','artifacts\simple-637\deployment-result.json','artifacts\simple-637\release-result.json','artifacts\simple-637\comparison.json')
$entry=[Environment]::NewLine+'CURRENT637: User requested simpler fastest UI. Shared workspace now one title18SemiBold + content; previous nested heading StackPanel/subtitle/separator removed (3 fewer always-visible UI nodes per panel). Contextual subtitle remains tooltip and Automation HelpText. Padding16/14/16/16 radius10 spacing12 margin10, smaller page intro gaps. Removed page motion fields/prewarm/animation factories, navigation dispatcher callback and selected-menu pulse callback/factory. Dialog and button feedback remains. Overview header/hardware preserved. Fresh637 build-test + six expanded capture/theme/startup/size/navigation/dialog suites passed. Actual Network screenshot more content visible (Connection/DNS/MTU), no clipping. Release/install payload verified, healthy HID fan agent PID'+$d.processId+'. Desktop only637 SHA256 '+$r.sha256+'. Source638 auto bump untested. Installed Release636 baseline and637 after three samples each; reports comparison.json and before/after-performance.json. Do not claim wall-clock/renderer speedup from these small integer-ms samples. Task complete. Next optimize only fresh measured UI-thread spike. Helper simple-637-edit non-idempotent.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$t=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.637','Source advanced automatically to 15.4.638').Replace('latest verified app release is 15.4.636','latest verified app release is 15.4.637')
[IO.File]::WriteAllText($handoff,$t)
& .\update-project-state.ps1 -Reason 'simple-637-installed-measured'
& .\resume-state.ps1 -Action refresh
Write-Output 'SIMPLE637_FINAL_VERIFIED'
