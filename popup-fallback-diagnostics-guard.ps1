$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$text=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\DesktopUpdateNotificationWindow.cs'))
$start=$text.IndexOf('public void ShowWithoutActivation()')
$end=$text.IndexOf('private void RunEntranceAnimation()',$start)
if($start -lt 0 -or $end -lt $start){throw 'ShowWithoutActivation section unavailable'}
$body=$text.Substring($start,$end-$start)
if(-not $body.Contains('AppWindow appWindow = AppWindow.GetFromWindowId(id);')){throw 'Native no-activate path lost'}
if(-not $body.Contains('ShowWindow(hwnd, SwShowNoActivate);')){throw 'No-activate fallback missing'}
if(-not $body.Contains('catch (Exception fallbackEx)')){throw 'Unobservable native fallback failure'}
if(-not $body.Contains('LogService.Error("update", "No-activate popup fallback failed')){throw 'Fallback errors not logged'}
if($body -match 'catch\s*\{\s*\}') {throw 'Silent fallback exception remains'}
Write-Output 'DESKTOP_POPUP_FALLBACK_DIAGNOSTIC_GUARD_OK'
