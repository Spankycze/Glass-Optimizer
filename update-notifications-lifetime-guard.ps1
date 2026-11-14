$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$s=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.UpdateNotifications.cs'))
$start=$s.IndexOf('private async Task CheckDesktopUpdateNotificationsAsync()')
$end=$s.IndexOf('private void ShowAppUpdateDesktopNotification(',$start)
if($start -lt 0 -or $end -lt $start){throw 'Missing notification checker or app popup factory'}
$b=$s.Substring($start,$end-$start)
$needs=@(
 'CancellationToken windowToken = _windowLifetimeCts.Token;',
 'Task.Delay(2200, windowToken)',
 'AppUpdateService.CheckAsync(windowToken)',
 'Task.Delay(350, windowToken)',
 'GpuDriverService.DetectPrimaryGpuAsync(windowToken)',
 'GpuDriverService.CheckExostenzaAsync(windowToken)',
 'catch (OperationCanceledException) when (windowToken.IsCancellationRequested)'
)
foreach($need in $needs){if(-not $b.Contains($need)){throw ('MISSING_LIFETIME '+$need)}}
if(([regex]::Matches($b,'windowToken\.ThrowIfCancellationRequested\(\);')).Count -lt 2){throw 'Missing pre-popup window lifetime gates'}
Write-Output 'UPDATE_NOTIFICATION_LIFETIME_GUARD_OK delayed=2 services=3 beforeShow>=2'
