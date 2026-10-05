$root='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer'
$fast=Join-Path $root 'build-fast.ps1'
$test=Join-Path $root 'build-test.ps1'
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss'
Copy-Item $fast ($fast+'.console-backup-'+$stamp) -Force
Copy-Item $test ($test+'.console-backup-'+$stamp) -Force

function Add-ConsoleHelpers([string]$text){
  if($text -match 'function Write-BuildStatus'){ return $text }
  $needle="$ErrorActionPreference='Stop'"
  $helper=@'
$ProgressPreference='SilentlyContinue'
function Write-BuildStatus([string]$Kind,[string]$Text){
  $icon=if($Kind -eq 'OK'){'✓'}elseif($Kind -eq 'WARN'){'!'}elseif($Kind -eq 'FAIL'){'×'}else{'•'}
  $color=if($Kind -eq 'OK'){'Green'}elseif($Kind -eq 'WARN'){'Yellow'}elseif($Kind -eq 'FAIL'){'Red'}else{'Cyan'}
  Write-Host '  [' -NoNewline -ForegroundColor DarkGray
  Write-Host 'BUILD' -NoNewline -ForegroundColor Cyan
  Write-Host '] ' -NoNewline -ForegroundColor DarkGray
  Write-Host "$icon " -NoNewline -ForegroundColor $color
  Write-Host $Text -ForegroundColor White
}
'@
  return $text.Replace($needle,$needle+[Environment]::NewLine+$helper)
}
$t=[IO.File]::ReadAllText($fast)
$t=Add-ConsoleHelpers $t
$t=$t.Replace('-v:minimal','-v:q')$t=$t.Replace("Write-Host 'FAST_BUILD_CACHE_HIT runnable Debug build is fresh'","Write-BuildStatus 'OK' 'cache-hit'")
$t=$t.Replace("Write-Host 'NATIVE_CACHE_HIT'","")
$t=$t.Replace("Write-Host 'FAST_BUILD_STAGED_CACHE_HIT'","Write-BuildStatus 'OK' 'staged cache-hit'")
$t=$t.Replace("Write-Host 'FAST_BUILD_STAGED_CACHE_HIT early'","Write-BuildStatus 'OK' 'staged cache-hit'")
$t=$t.Replace("Write-Host ('FAST_FALLBACK_DONE {0:n2}s exit=0' -f $watch.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('full fallback {0:n2}s' -f $watch.Elapsed.TotalSeconds)")
$t=$t.Replace("Write-Host ('CORE_FAST_BUILD_OK {0:n2}s publicApiUnchanged=true' -f $sw.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('core {0:n2}s' -f $sw.Elapsed.TotalSeconds)")
$t=$t.Replace("Write-Host ('FAST_BUILD_STAGED_LOCK_OK {0:n2}s' -f $sw.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('staged-lock {0:n2}s' -f $sw.Elapsed.TotalSeconds)")
$t=$t.Replace("Write-Host ('FAST_BUILD_OK {0:n2}s' -f $sw.Elapsed.TotalSeconds)","Write-BuildStatus 'OK' ('{0:n2}s' -f $sw.Elapsed.TotalSeconds)")
$t=$t.Replace("Write-Host ('FAST_BUILD_PROJECT_REFS ' + $(if($skipProjectRefs){'skip'}else{'build'}))","if($VerboseOutput){Write-BuildStatus 'INFO' ('project refs: ' + $(if($skipProjectRefs){'skip'}else{'build'}))}")
$t=$t.Replace("Write-Host ('FAST_BUILD_STOP_DEBUG pid={0}' -f $processId)","if($VerboseOutput){Write-BuildStatus 'INFO' ('stop debug pid={0}' -f $processId)}")
$t=$t.Replace("Write-Host 'FAST_BUILD_RETRY_XAML_LOCK'","Write-BuildStatus 'WARN' 'XAML lock, retrying'")
[IO.File]::WriteAllText($fast,$t,[Text.UTF8Encoding]::new($false))

$u=[IO.File]::ReadAllText($test)
$u=Add-ConsoleHelpers $u
$u=$u.Replace("Write-Host 'TEST_BUILD refresh runnable Debug build'","Write-BuildStatus 'INFO' 'refreshing Debug build'")
$u=$u.Replace("else {Write-Host 'TEST_BUILD_CACHE_HIT runnable Debug build is fresh'}","else {Write-BuildStatus 'OK' 'test build cache-hit'}")
$u=$u.Replace("Write-Host \"$arg OK $([math]::Round($sw.Elapsed.TotalSeconds,2))s\"","Write-BuildStatus 'OK' (\"$arg  $([math]::Round($sw.Elapsed.TotalSeconds,2))s\")")
$u=$u.Replace("$total.Stop(); Write-Host ('BUILD_TEST_OK total={0:n2}s' -f $total.Elapsed.TotalSeconds)","$total.Stop(); Write-BuildStatus 'OK' ('tests total {0:n2}s' -f $total.Elapsed.TotalSeconds)")
[IO.File]::WriteAllText($test,$u,[Text.UTF8Encoding]::new($false))$errs=@()
foreach($p in @($fast,$test)){
  $tok=$null;$e=$null
  [Management.Automation.Language.Parser]::ParseFile($p,[ref]$tok,[ref]$e)|Out-Null
  if($e.Count){$errs += $e | ForEach-Object { "$p :: $($_.Message)" }}
}
if($errs.Count){$errs|ForEach-Object{Write-Host $_ -ForegroundColor Red}; exit 1}
Write-Host 'BUILD_CONSOLE_PATCH_OK' -ForegroundColor Green