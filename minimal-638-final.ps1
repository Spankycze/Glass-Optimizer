$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\minimal-638'
$d=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'deployment-result.json')))
$r=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $dir 'release-result.json')))
if($d.installedVersion -notlike '15.4.638*' -or !$d.payloadVerified -or !$d.healthy -or !$d.hidOk){throw 'Deployment verification missing'}
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
if($b.version -ne '15.4.637' -or $a.version -ne '15.4.638'){throw 'Performance version mismatch'}
$report=[ordered]@{beforeVersion=$b.version;afterVersion=$a.version;repeats=3;beforeMedian=(Metrics $b);afterMedian=(Metrics $a);scope='Installed Release builds, three samples each; build/navigation timing, not FPS or render-to-display latency.';structural='Open workspace sections with one bottom separator; no large fill/outer frame or gradient brush; compact navigation and headings. Immediate page switching from637 retained.'}
$report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $dir 'comparison.json') -Encoding UTF8
Write-Output ($report|ConvertTo-Json -Depth 10)
& .\ai-workflow.ps1 -Action complete-task -TaskId 'minimal-638' -Summary 'Installed638: large workspace cards removed, open black sections with single separators, title26/section16, compact sidebar, redundant DNS banner removed. Six suites and gated release/install passed; fan agent healthy/HID verified; installed Release637/638 three-sample comparison saved.' -NextAction 'Minimal638 UI correction complete. Source639 automatic bump untested. Next user-directed design iteration, preserve Overview hardware/header. Do not claim overall speedup from small noisy measurements.' -Done @('Large outer workspace cards removed','Compact headings and sidebar','Redundant network banner removed','Six suites and gated release installed','Release timings compared') -Evidence @('artifacts\minimal-638\validation-result.json','artifacts\minimal-638\deployment-result.json','artifacts\minimal-638\release-result.json','artifacts\minimal-638\comparison.json','artifacts\minimal-638\screens\dark-network.png')
$entry=[Environment]::NewLine+'CURRENT638: User rejected637 as nearly identical; now structural visual correction. Shared workspace no fill/outer framed card/radius: only bottom1px separator, padding0/10/0/18 margin8. Title26 section16, sidebar padding8/3 andmin32; colored icons readableSemiBold retained. Removed redundant DNS&MTU flat banner. Network capture visibly open black sections, full MTU input/actions visible, sidebar PowerShell/Cursor visible. Overview hardware/header preserved. Removed unused workspace gradient brush cache and reset. Six capture/theme/startup/size/navigation/dialog suites + build-test + full gated release passed. Installed638 payload/HID/fanagent verified PID'+$d.processId+'. Desktop only638 SHA256 '+$r.sha256+'. Source639 auto bump untested. Installed Release637/638 three samples comparison.json; no broad speed claim, view simplification retained for explicit user request. minimal-638-edit non-idempotent. Task complete.'+[Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $PWD 'SESSION_HISTORY.md'),$entry)
$handoff=Join-Path $PWD 'SESSION_HANDOFF.md'
$v=[IO.File]::ReadAllText($handoff).Replace('Source advanced automatically to 15.4.638','Source advanced automatically to 15.4.639').Replace('latest verified app release is 15.4.637','latest verified app release is 15.4.638')
[IO.File]::WriteAllText($handoff,$v)
& .\update-project-state.ps1 -Reason 'minimal-638-installed-measured'
& .\resume-state.ps1 -Action refresh
Write-Output 'MINIMAL638_FINAL_VERIFIED'