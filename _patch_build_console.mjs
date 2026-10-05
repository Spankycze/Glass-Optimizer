import fs from 'fs';
const root='C:/Users/spnka/Desktop/ChatGPT Projects/Spankyho Optimizer';
const fast=root+'/build-fast.ps1', test=root+'/build-test.ps1';
const stamp=new Date().toISOString().replace(/[:.]/g,'-');
for(const p of [fast,test]) fs.copyFileSync(p,p+'.console-backup-'+stamp);
const helper=`$ProgressPreference='SilentlyContinue'
function Write-BuildStatus([string]$Kind,[string]$Text){
  $icon=if($Kind -eq 'OK'){'✓'}elseif($Kind -eq 'WARN'){'!'}elseif($Kind -eq 'FAIL'){'×'}else{'•'}
  $color=if($Kind -eq 'OK'){'Green'}elseif($Kind -eq 'WARN'){'Yellow'}elseif($Kind -eq 'FAIL'){'Red'}else{'Cyan'}
  Write-Host '  [' -NoNewline -ForegroundColor DarkGray
  Write-Host 'BUILD' -NoNewline -ForegroundColor Cyan
  Write-Host '] ' -NoNewline -ForegroundColor DarkGray
  Write-Host "$icon " -NoNewline -ForegroundColor $color
  Write-Host $Text -ForegroundColor White
}
`;
function addHelper(s){
 if(s.includes('function Write-BuildStatus')) return s;
 return s.replace("$ErrorActionPreference='Stop'", "$ErrorActionPreference='Stop'\r\n"+helper);
}
let s=addHelper(fs.readFileSync(fast,'utf8')).replaceAll('-v:minimal','-v:q');
const reps=[
 ["Write-Host 'NATIVE_CACHE_HIT'",""],
 ["Write-Host 'FAST_BUILD_CACHE_HIT runnable Debug build is fresh'","Write-BuildStatus 'OK' 'cache-hit'"],
 ["Write-Host 'FAST_BUILD_STAGED_CACHE_HIT'","Write-BuildStatus 'OK' 'staged cache-hit'"],
 ["Write-Host 'FAST_BUILD_STAGED_CACHE_HIT early'","Write-BuildStatus 'OK' 'staged cache-hit'"],
 ["Write-Host 'FAST_BUILD_RETRY_XAML_LOCK'","Write-BuildStatus 'WARN' 'XAML lock, retrying'"],
];
for(const [a,b] of reps) s=s.replaceAll(a,b);
s=s.replace("Write-Host ('FAST_FALLBACK_DONE {0:n2}s exit=0' -f $watch.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('full fallback {0:n2}s' -f $watch.Elapsed.TotalSeconds)");
s=s.replace("Write-Host ('CORE_FAST_BUILD_OK {0:n2}s publicApiUnchanged=true' -f $sw.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('core {0:n2}s' -f $sw.Elapsed.TotalSeconds)");
s=s.replace("Write-Host ('FAST_BUILD_STAGED_LOCK_OK {0:n2}s' -f $sw.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('staged-lock {0:n2}s' -f $sw.Elapsed.TotalSeconds)");
s=s.replace("Write-Host ('FAST_BUILD_OK {0:n2}s' -f $sw.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('{0:n2}s' -f $sw.Elapsed.TotalSeconds)");
s=s.replace("Write-Host ('FAST_BUILD_PROJECT_REFS ' + $(if($skipProjectRefs){'skip'}else{'build'}))","Write-BuildStatus 'INFO' ('project refs: ' + $(if($skipProjectRefs){'skip'}else{'build'}))");
fs.writeFileSync(fast,s,'utf8');

let t=addHelper(fs.readFileSync(test,'utf8'));
t=t.replace("else {Write-Host 'TEST_BUILD_CACHE_HIT runnable Debug build is fresh'}","else {Write-BuildStatus 'OK' 'test build cache-hit'}");
t=t.replace('Write-Host "$arg OK $([math]::Round($sw.Elapsed.TotalSeconds,2))s"','Write-BuildStatus \'OK\' ("$arg  $([math]::Round($sw.Elapsed.TotalSeconds,2))s")');
t=t.replace("$total.Stop(); Write-Host ('BUILD_TEST_OK total={0:n2}s' -f $total.Elapsed.TotalSeconds)","$total.Stop(); Write-BuildStatus 'OK' ('tests total {0:n2}s' -f $total.Elapsed.TotalSeconds)");
fs.writeFileSync(test,t,'utf8');
console.log('BUILD_CONSOLE_PATCH_OK');