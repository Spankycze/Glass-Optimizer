$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'tools\source-set.ps1')
$main=Get-SpankyMainWindowSource $root
$notify=(Get-Content (Join-Path $root 'src\SpankyhoOptimizer\DesktopUpdateNotificationWindow.cs') -Raw) + (Get-Content (Join-Path $root 'src\SpankyhoOptimizer\DesktopPopupWindowFrame.cs') -Raw)
$notifyMain=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.UpdateNotifications.cs') -Raw
$appUpdate=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\AppUpdateService.cs') -Raw
$background=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\BackgroundUpdateService.cs') -Raw
$themes=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Themes.cs') -Raw
$wall=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\WallpaperThemeService.cs') -Raw
$proj=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj') -Raw

if($main.Contains('BuildWindowsModernizerUiPanel(token);')){throw 'DESKTOP_UPDATE_GUARD old Windows Modernizer UI panel returned'}
if($main.Contains('BuildWinhanceUiActions();')){throw 'DESKTOP_UPDATE_GUARD old Winhance UI panel returned'}
if(-not $main.Contains('CheckDesktopUpdateNotificationsAsync()')){throw 'DESKTOP_UPDATE_GUARD startup notification check missing'}
foreach($token in @('Stáhnout','Později','Nainstalovat','DesktopUpdateProgress','IsAlwaysOnTop','work.Height - actualSize.Height','_stackIndex * (actualSize.Height + gap)')){
  if(-not $notify.Contains($token) -and -not $notifyMain.Contains($token)){throw "DESKTOP_UPDATE_GUARD token missing: $token"}
}
$glass=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Glass21.cs') -Raw
if(-not $glass.Contains('configuration.IsInputActive = true')){throw 'DESKTOP_UPDATE_GUARD main acrylic can still fall back when popup takes focus'}
if(-not $glass.Contains('FallbackColor = Windows.UI.Color.FromArgb(0, 0, 0, 0)')){throw 'DESKTOP_UPDATE_GUARD opaque acrylic fallback returned'}
if($notifyMain.Contains('window.Activate()')){throw 'DESKTOP_UPDATE_GUARD updater notification steals activation'}
if(-not $notify.Contains('ShowWithoutActivation()') -or -not $notify.Contains('appWindow.Show(false)')){throw 'DESKTOP_UPDATE_GUARD no-activate popup path missing'}
if(-not $notify.Contains('Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 34, 44))')){throw 'DESKTOP_UPDATE_GUARD popup opaque card surface missing'}
if(-not $notify.Contains('Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 22, 29))')){throw 'DESKTOP_UPDATE_GUARD popup base surface missing'}
if(-not $notify.Contains('App.IsSmokeUi') -or -not $notify.Contains('appWindow.Move(new PointInt32(-32000, -32000))')){throw 'DESKTOP_UPDATE_GUARD popup smoke window can still flash on desktop'}
if($notify.Contains('Activate();')){throw 'DESKTOP_UPDATE_GUARD popup can still steal focus'}
if(-not $notify.Contains('ShowWindow(hwnd, SwShowNoActivate)')){throw 'DESKTOP_UPDATE_GUARD native no-activate fallback missing'}
if(-not $notify.Contains('appWindow.IsShownInSwitchers = false')){throw 'DESKTOP_UPDATE_GUARD popup still appears in Alt-Tab'}
if(-not $notify.Contains('WsExNoActivate') -or -not $notify.Contains('SetWindowLongPtr')){throw 'DESKTOP_UPDATE_GUARD native WS_EX_NOACTIVATE hardening missing'}
if(-not $notify.Contains('RunEntranceAnimation()') -or -not $notify.Contains('CreateScalarKeyFrameAnimation')){throw 'DESKTOP_UPDATE_GUARD lightweight popup entrance animation missing'}
if(-not $notify.Contains('var card = new Border') -or -not $notify.Contains('root.Children.Add(card)')){throw 'DESKTOP_UPDATE_GUARD popup card surface missing'}
if(-not $notify.Contains('RequestedTheme = ElementTheme.Dark')){throw 'DESKTOP_UPDATE_GUARD popup first-frame dark theme missing'}
$app=Get-SpankyAppSource $root
$mainGlassOwner=Get-SpankyMainWindowSource $root
# Focus changes still synchronously restore native border/caption policy.
# Repeated same-state focus notifications must not force a second DWM region
# rewrite or invalidate every sidebar/card visual on the WinUI input thread.
if(-not $mainGlassOwner.Contains('if (UseGlassChrome && (activationChanged || !_customTitleBarPrimed))') -or
   -not $mainGlassOwner.Contains('ApplyWindowTitleBarTheme();') -or
   -not $mainGlassOwner.Contains('QueueGlassActivationRepair();') -or
   -not $mainGlassOwner.Contains('ApplyNativeGlassWindowShape();')){throw 'DESKTOP_UPDATE_GUARD main window immediate inactive glass repair missing'}
if(-not $glass.Contains('private void QueueGlassActivationRepair()') -or
   -not $glass.Contains('UpdateGlassShell();') -or
   -not $glass.Contains('if (rebound)') -or
   -not $glass.Contains('ApplyNativeGlassWindowShape();')){throw 'DESKTOP_UPDATE_GUARD activation glass recovery contract missing'}
if($glass.Contains('Repair the visible XAML/DWM surfaces synchronously') -or
   $glass.Contains('_glassShellAppliedRoot = null;' + "`n" + '        UpdateGlassShell();' + "`n" + '        ApplyNativeGlassWindowShape();')){throw 'DESKTOP_UPDATE_GUARD redundant native activation repair reappeared'}
if(-not $app.Contains('SmokeWindowX = -32000') -or -not $app.Contains('PrepareSmokeWindow(window)')){throw 'DESKTOP_UPDATE_GUARD off-screen smoke positioning missing'}
if(-not $app.Contains('ShowSmokeWindowWithoutActivation(_window)') -or -not $app.Contains('appWindow.Show(false)')){throw 'DESKTOP_UPDATE_GUARD main smoke window can still steal focus'}
foreach($token in @('ShowAppUpdateDesktopNotification','ShowExostenzaDesktopNotification','ResolveExostenzaDownloadAsync','AppUpdateService.DownloadAsync')){
  if(-not $notifyMain.Contains($token)){throw "DESKTOP_UPDATE_GUARD updater integration missing: $token"}
}
if(-not $notifyMain.Contains('WaitForExitAsync') -or -not $notifyMain.Contains('IsSuccessfulInstallerExitCode')){throw 'DESKTOP_UPDATE_GUARD installer completion policy missing'}
if(-not $notifyMain.Contains('CreateLinkedTokenSource') -or -not $notifyMain.Contains('TimeSpan.FromMinutes(30)')){throw 'DESKTOP_UPDATE_GUARD installer wait is not bounded'}
if(-not $background.Contains('WaitForExitAsync') -or -not $background.Contains('BACKGROUND_UPDATE_INSTALLED')){throw 'DESKTOP_UPDATE_GUARD background completion wait missing'}
if(-not $background.Contains('--automatic-update-popup-manifest=')){throw 'DESKTOP_UPDATE_GUARD verified manifest handoff missing'}
if(-not $background.Contains('new Semaphore(1, 1, GateName)')){throw 'DESKTOP_UPDATE_GUARD async background gate must be thread agnostic'}
if($background.Contains('mutex.ReleaseMutex()')){throw 'DESKTOP_UPDATE_GUARD async background updater uses thread-affine mutex release'}
if($background.Contains('Local\\GlassOptimizerAutomaticUpdatePopup')){throw 'DESKTOP_UPDATE_GUARD background owns popup mutex'}
if(-not $notifyMain.Contains('Updater popup changed the foreground window.') -or -not $notifyMain.Contains('Main Optimizer acrylic controller dropped while updater popup was visible.') -or -not $notifyMain.Contains('Inactive titlebar became opaque while updater popup was visible.')){throw 'DESKTOP_UPDATE_GUARD popup smoke no longer verifies focus and main-window glass stability'}
if(-not $appUpdate.Contains('IProgress<AppUpdateDownloadProgress>? progress')){throw 'DESKTOP_UPDATE_GUARD app update progress integration missing'}
foreach($token in @('LOCAL-SHA256','LocalBridgeManifestPath','LocalBridgeRoot','IsLocalBridgeManifest')){
  if(-not $appUpdate.Contains($token)){throw "DESKTOP_UPDATE_GUARD local update bridge token missing: $token"}
}
if(-not $appUpdate.Contains('SerializePopupManifest') -or -not $appUpdate.Contains('DeserializePopupManifest')){throw 'DESKTOP_UPDATE_GUARD popup manifest serialization missing'}
$releaseScript=Get-Content (Join-Path $root 'build-release.ps1') -Raw
if(-not $releaseScript.Contains('LOCAL_UPDATE_BRIDGE_OK') -or
   -not $releaseScript.Contains('if($PublishLocalUpdateBridge){') -or
   -not $releaseScript.Contains('LOCAL_UPDATE_BRIDGE_SKIPPED optInRequired=true activeSettingsPreserved=true')){
  throw 'DESKTOP_UPDATE_GUARD local bridge must require explicit publish opt-in'
}
if(-not $proj.Contains('Assets\Wallpapers\*.jpg')){throw 'DESKTOP_UPDATE_GUARD packaged wallpapers missing'}
if($themes.Contains('LinearGradientBrush')){throw 'DESKTOP_UPDATE_GUARD wallpaper preview regressed to gradient'}
if($wall.Contains('GenerateWallpaper')){throw 'DESKTOP_UPDATE_GUARD generated color wallpaper returned'}
foreach($file in @('Windows-Neon-Wave.jpg','Windows-Purple-Valley.jpg')){
  $path=Join-Path $root ('src\SpankyhoOptimizer\Assets\Wallpapers\'+$file)
  if(-not (Test-Path $path)){throw "DESKTOP_UPDATE_GUARD wallpaper missing: $file"}
  if((Get-Item $path).Length -lt 100KB){throw "DESKTOP_UPDATE_GUARD wallpaper suspiciously small: $file"}
}
Write-Host 'DESKTOP_UPDATE_GUARD_OK glassPopup=true appUpdate=true exostenza=true progress=true realWallpapers=true legacyUiExtras=false'

if(-not $notify.Contains('appWindow.ResizeClient') -or -not $notify.Contains('GetDpiForWindow')){throw 'DESKTOP_UPDATE_GUARD DPI client sizing missing'}
if(-not $notify.Contains('UiMotionSettingsService.EffectiveEnabled')){throw 'DESKTOP_UPDATE_GUARD reduced motion missing'}
