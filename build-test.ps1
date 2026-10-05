param([switch]$Full,[switch]$NoBuild,[switch]$Force,[switch]$ForceBuild,[switch]$ForceTests,[string]$ExePath)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
function Write-BuildStatus([string]$Kind,[string]$Text){
  $icon=if($Kind -eq 'OK'){[char]0x2713}elseif($Kind -eq 'WARN'){'!'}elseif($Kind -eq 'FAIL'){'X'}else{'>'}
  $color=if($Kind -eq 'OK'){'Green'}elseif($Kind -eq 'WARN'){'Yellow'}elseif($Kind -eq 'FAIL'){'Red'}else{'Cyan'}
  Write-Host '  [' -NoNewline -ForegroundColor DarkGray
  Write-Host 'BUILD' -NoNewline -ForegroundColor Cyan
  Write-Host '] ' -NoNewline -ForegroundColor DarkGray
  Write-Host "$icon " -NoNewline -ForegroundColor $color
  Write-Host $Text -ForegroundColor White
}

$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build-state.ps1')
$dev=Join-Path $root 'build-dev.ps1'
$exe=if([string]::IsNullOrWhiteSpace($ExePath)){
  Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
}else{$ExePath}
$forceBuildNow=$Force -or $ForceBuild
$forceTestsNow=$Force -or $ForceTests

if(-not $NoBuild){
  if($forceBuildNow -or -not(Test-SpankyRunnableFresh)){
    Write-Host 'TEST_BUILD refresh runnable Debug build'
    & $dev
  } else {Write-BuildStatus 'OK' 'test build cache-hit'}
}
if(!(Test-Path $exe)){throw 'Debug EXE nebyl nalezen.'}
$fresh=Test-SpankyTestFresh -Full:$Full
if(-not $forceTestsNow -and $fresh){
  Write-Host $(if($Full){'BUILD_TEST_FULL_CACHE_HIT'}else{'BUILD_TEST_CACHE_HIT'})
  return
}

function Run-Smoke([string]$arg,[int]$timeoutSec){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $p=Start-Process -FilePath $exe -ArgumentList $arg -WorkingDirectory (Split-Path $exe) -PassThru
  if(-not $p.WaitForExit($timeoutSec*1000)){
    try{Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}catch{}
    throw "$arg timeout po $timeoutSec s"
  }
  $sw.Stop()
  if($p.ExitCode -ne 0){throw "$arg selhal, exit=$($p.ExitCode)"}
  Write-BuildStatus 'OK' ("$arg  $([math]::Round($sw.Elapsed.TotalSeconds,2))s")
}

function Test-PowerShellProfilePerformance {
  $pwsh='C:\\Program Files\\PowerShell\\7\\pwsh.exe'
  if(-not(Test-Path $pwsh)){Write-Host 'POWERSHELL_PROFILE_PERF_SKIP pwsh missing';return}
  $oldBanner=$env:TERMINAL_BANNER
  $env:TERMINAL_BANNER='0'
  try{
    $profile=@();$clean=@()
    1..5|ForEach-Object{
      $sw=[Diagnostics.Stopwatch]::StartNew()
      & $pwsh -NoLogo -NonInteractive -Command exit *> $null
      $sw.Stop();$profile+=$sw.Elapsed.TotalMilliseconds
      $sw=[Diagnostics.Stopwatch]::StartNew()
      & $pwsh -NoLogo -NoProfile -NonInteractive -Command exit *> $null
      $sw.Stop();$clean+=$sw.Elapsed.TotalMilliseconds
    }
    $p=($profile|Sort-Object)[2];$n=($clean|Sort-Object)[2]
    $over=[math]::Round(($p-$n),1)
    Write-Host ('POWERSHELL_PROFILE_PERF_OK profile={0}ms noProfile={1}ms overhead={2}ms limit=300ms' -f [math]::Round($p,1),[math]::Round($n,1),$over)
    if($over -gt 300){throw ('PowerShell profile overhead regression: {0}ms > 300ms' -f $over)}
  } finally {
    $env:TERMINAL_BANNER=$oldBanner
  }
}

$total=[Diagnostics.Stopwatch]::StartNew()
if($Full){
  Test-PowerShellProfilePerformance
  Run-Smoke '--full-smoke' 420
  Run-Smoke '--theme-render-smoke' 60
  Run-Smoke '--hardware-identity-smoke' 30
  Run-Smoke '--lazy-actionbar-smoke' 30
  Run-Smoke '--startup-render-smoke' 30
  Run-Smoke '--power-render-smoke' 30
  Run-Smoke '--system-cold-render-smoke' 30
  Run-Smoke '--virtual-resource-prewarm-smoke' 30
  Run-Smoke '--nav500-smoke' 120
  Run-Smoke '--apply-profile-smoke' 30
  Run-Smoke '--preflight-warm-smoke' 30
  Run-Smoke '--startup-warm-apply-smoke' 30
  Run-Smoke '--bcd-cache-smoke' 30
  Run-Smoke '--bcd-persistent-read-smoke' 30
  Run-Smoke '--process-callback-smoke' 30
  Run-Smoke '--process-timeout-smoke' 30
  Run-Smoke '--process-cancel-smoke' 30
  Run-Smoke '--single-instance-smoke' 30
  Run-Smoke '--winget-status-smoke' 30
  Run-Smoke '--ai-runtime-hardening-smoke' 30
  Run-Smoke '--ai-progress-ui-smoke' 30
  Run-Smoke '--shortcut-overlay-smoke' 30
  Run-Smoke '--drivers-ui-smoke' 30
  Run-Smoke '--task-scheduler-ui-smoke' 30
  Run-Smoke '--gpu-deferred-render-smoke' 30
  Run-Smoke '--exostenza-cache-reuse-smoke' 30
  Run-Smoke '--shell-setup-idempotency-smoke' 30
  Run-Smoke '--startup-delta-smoke' 30
  Run-Smoke '--catalog-audit-smoke' 60
  Run-Smoke '--dns-readback-guard-smoke' 30
  Run-Smoke '--dialog-lifecycle-smoke' 30
  Run-Smoke '--visual-layout-smoke' 120
  Run-Smoke '--visual-size-smoke' 120
  Run-Smoke '--page-scroll-input-smoke' 120
  Run-Smoke '--keyboard-accessibility-smoke' 120
  Run-Smoke '--keyboard-activation-smoke' 120
  Run-Smoke '--section-tab-smoke' 120
  Run-Smoke '--navigation-race-smoke' 120
  Run-Smoke '--debloat-cache-smoke' 120
  Run-Smoke '--spicetify-status-smoke' 30
  Run-Smoke '--power-plan-guard-smoke' 30
} else {
  Run-Smoke '--startup-smoke' 20
  Run-Smoke '--section-smoke' 120
}
Save-SpankyTestState -Full:$Full
$total.Stop(); Write-BuildStatus 'OK' ('tests total {0:n2}s' -f $total.Elapsed.TotalSeconds)
