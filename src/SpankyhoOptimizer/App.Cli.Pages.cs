using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchPagesAsync(string[] cli)
    {
        if (HasArg(cli, "--exostenza-ui-smoke") && _window is MainWindow exMw)
        {
            bool keepOpen = HasArg(cli, "--keep-open");
            if (keepOpen)
            {
                exMw.Title = "Glass Optimizer — NÁHLED";
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(exMw);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
                appWindow.IsShownInSwitchers = true;
                if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter previewPresenter)
                    previewPresenter.IsAlwaysOnTop = true;
                SetWindowPos(hwnd, IntPtr.Zero, 80, 60, 1320, 860, SwpNoZOrder);
                exMw.Activate();
            }
            await Task.Delay(500);
            if (keepOpen && HasArg(cli, "--tweak-confirm-preview"))
                await exMw.RunTweakConfirmationPreviewAsync();
            else
                await exMw.RunExostenzaUiSmokeAsync();
            if (!keepOpen) Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--glass-stability-smoke") && _window is MainWindow glassStabilityMw)
        {
            try { await Task.Delay(350); await glassStabilityMw.RunGlassStabilitySmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("GLASS_STABILITY_SMOKE_FAIL", ex); Environment.Exit(55); }
            return true;
        }
        if (HasArg(cli, "--desktop-status-notification-smoke") && _window is MainWindow statusPopupMw)
        {
            await Task.Delay(350);
            await statusPopupMw.RunDesktopStatusNotificationSmokeAsync(HasArg(cli, "--show-status-popup"));
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--desktop-update-notification-smoke") && _window is MainWindow updatePopupMw)
        {
            await Task.Delay(350);
            await updatePopupMw.RunDesktopUpdateNotificationSmokeAsync(HasArg(cli, "--show-update-popup"), HasArg(cli, "--exostenza-preview"));
            LogService.Info("DESKTOP_UPDATE_NOTIFICATION_SMOKE_OK");
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--dialog-lifecycle-smoke") && _window is MainWindow dialogMw)
        {
            await Task.Delay(250);
            LogService.Info(dialogMw.RunDialogLifecycleSmoke());
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--software-updater-smoke") && _window is MainWindow updaterMw)
        {
            try
            {
                await Task.Delay(350);
                await updaterMw.RunSoftwareUpdaterSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "SOFTWARE_UPDATER_SMOKE_FAIL", ex);
                Environment.Exit(53);
            }
            return true;
        }
        if (HasArg(cli, "--software-updater-release-smoke") && _window is MainWindow updaterReleaseMw)
        {
            try
            {
                await Task.Delay(350);
                var before = WingetUpdateService.GetUpgradeCacheDiagnostics();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await updaterReleaseMw.RunSoftwareUpdaterInlineSmokeAsync();
                long updaterMs = sw.ElapsedMilliseconds;
                var afterUpdater = WingetUpdateService.GetUpgradeCacheDiagnostics();
                int updaterScans = afterUpdater.ScanCount - before.ScanCount;
                if (updaterScans < 0 || updaterScans > 1)
                    throw new InvalidOperationException($"Software Updater expected persistent-cache reuse or one fresh Winget scan, scans={updaterScans}.");

                WingetUpdateService.InvalidateStatusMetadataOnly();
                var status = await WingetUpdateService.GetStatusAsync();
                long totalMs = sw.ElapsedMilliseconds;
                var afterStatus = WingetUpdateService.GetUpgradeCacheDiagnostics();
                int totalScans = afterStatus.ScanCount - before.ScanCount;
                if (totalScans < 0 || totalScans > 1)
                    throw new InvalidOperationException($"Winget status/updater path repeated the upgrade scan, scans={totalScans}.");
                if (!status.Available || string.IsNullOrWhiteSpace(status.Version) ||
                    status.Version.Equals("neznámá", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bundled Winget status did not return a valid fresh version.");

                LogService.Info("winget", $"SOFTWARE_UPDATER_RELEASE_SMOKE_OK version={status.Version} upgrades={status.UpgradeCount} updaterMs={updaterMs} totalMs={totalMs} scans={afterStatus.ScanCount - before.ScanCount}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "SOFTWARE_UPDATER_RELEASE_SMOKE_FAIL", ex);
                Environment.Exit(74);
            }
            return true;
        }

        if (HasArg(cli, "--software-updater-inline-smoke") && _window is MainWindow updaterInlineMw)
        {
            try
            {
                await Task.Delay(350);
                await updaterInlineMw.RunSoftwareUpdaterInlineSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "SOFTWARE_UPDATER_INLINE_SMOKE_FAIL", ex);
                Environment.Exit(54);
            }
            return true;
        }

        if (HasArg(cli, "--winget-status-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                WingetUpdateService.InvalidateStatus();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var status = await WingetUpdateService.GetStatusAsync(cts.Token);
                sw.Stop();
                long freshMs = sw.ElapsedMilliseconds;
                if (!status.Available)
                    throw new InvalidOperationException("Fresh Winget status reported unavailable: " + status.Message);
                if (string.IsNullOrWhiteSpace(status.Version) ||
                    status.Version.Equals("neznámá", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fresh Winget version probe did not return a valid version.");

                int scansAfterFresh = WingetUpdateService.GetUpgradeCacheDiagnostics().ScanCount;
                WingetUpdateService.ClearMemoryStatusCacheForSmoke();
                sw.Restart();
                var persistedStatus = await WingetUpdateService.GetStatusAsync(cts.Token);
                long persistentMs = sw.ElapsedMilliseconds;
                int scansAfterPersistent = WingetUpdateService.GetUpgradeCacheDiagnostics().ScanCount;
                if (scansAfterPersistent != scansAfterFresh)
                    throw new InvalidOperationException("Persistent Winget status unexpectedly started an upgrade scan.");
                if (persistedStatus.Version != status.Version || persistedStatus.UpgradeCount != status.UpgradeCount || persistedStatus.Available != status.Available)
                    throw new InvalidOperationException("Persistent Winget status mismatch.");
                if (persistentMs > 100)
                    throw new InvalidOperationException($"Persistent Winget status too slow: {persistentMs} ms.");

                LogService.Info("winget", $"WINGET_STATUS_SMOKE_OK version={status.Version} upgrades={status.UpgradeCount} freshMs={freshMs} persistentMs={persistentMs}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "WINGET_STATUS_SMOKE_FAIL", ex);
                Environment.Exit(59);
            }
            return true;
        }

        if (HasArg(cli, "--winget-cache-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                WingetUpdateService.InvalidateStatus();
                var before = WingetUpdateService.GetUpgradeCacheDiagnostics();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var a = WingetUpdateService.GetUpgradesAsync(cts.Token);
                var b = WingetUpdateService.GetUpgradesAsync(cts.Token);
                var c = WingetUpdateService.GetUpgradesAsync(cts.Token);
                var coldResults = await Task.WhenAll(a, b, c);
                long coldMs = sw.ElapsedMilliseconds;
                var afterCold = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (afterCold.ScanCount - before.ScanCount != 1)
                    throw new InvalidOperationException($"Winget in-flight dedupe failed scans={afterCold.ScanCount - before.ScanCount}.");
                if (coldResults[0].Count != coldResults[1].Count || coldResults[0].Count != coldResults[2].Count)
                    throw new InvalidOperationException("Winget in-flight callers returned inconsistent counts.");

                sw.Restart();
                var warm = await WingetUpdateService.GetUpgradesAsync(cts.Token);
                long warmMs = sw.ElapsedMilliseconds;
                var afterWarm = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (afterWarm.ScanCount != afterCold.ScanCount)
                    throw new InvalidOperationException("Winget warm cache unexpectedly started a new scan.");
                if (warm.Count != coldResults[0].Count)
                    throw new InvalidOperationException("Winget warm cache count mismatch.");

                UserScopedCacheService.BackdateForSmoke("winget-upgrades-v1", TimeSpan.FromMinutes(5));
                WingetUpdateService.ClearMemoryUpgradesCacheForSmoke();
                var beforeStaleDisplay = WingetUpdateService.GetUpgradeCacheDiagnostics();
                sw.Restart();
                var staleDisplay = WingetUpdateService.TryGetDisplayUpgrades()
                    ?? throw new InvalidOperationException("Winget stale display snapshot was not available.");
                long staleDisplayMs = sw.ElapsedMilliseconds;
                var afterStaleDisplay = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (!staleDisplay.NeedsRefresh)
                    throw new InvalidOperationException("Winget stale display snapshot did not request refresh.");
                if (afterStaleDisplay.ScanCount != beforeStaleDisplay.ScanCount)
                    throw new InvalidOperationException("Winget stale display snapshot unexpectedly started a scan.");
                if (staleDisplay.Items.Count != warm.Count)
                    throw new InvalidOperationException("Winget stale display count mismatch.");
                if (staleDisplayMs > 100)
                    throw new InvalidOperationException($"Winget stale display too slow: {staleDisplayMs} ms.");

                WingetUpdateService.InvalidateStatus();
                var beforeForce = WingetUpdateService.GetUpgradeCacheDiagnostics();
                sw.Restart();
                var forceA = WingetUpdateService.GetUpgradesAsync(cts.Token, forceRefresh: true);
                var forceB = WingetUpdateService.GetUpgradesAsync(cts.Token, forceRefresh: true);
                var forceC = WingetUpdateService.GetUpgradesAsync(cts.Token, forceRefresh: true);
                var forceResults = await Task.WhenAll(forceA, forceB, forceC);
                long forceMs = sw.ElapsedMilliseconds;
                var afterForce = WingetUpdateService.GetUpgradeCacheDiagnostics();
                if (afterForce.ScanCount - beforeForce.ScanCount != 1)
                    throw new InvalidOperationException($"Winget force-refresh in-flight dedupe failed scans={afterForce.ScanCount - beforeForce.ScanCount}.");
                if (forceResults[0].Count != forceResults[1].Count || forceResults[0].Count != forceResults[2].Count)
                    throw new InvalidOperationException("Winget force-refresh callers returned inconsistent counts.");

                LogService.Info("winget", $"WINGET_CACHE_SMOKE_OK count={warm.Count} coldMs={coldMs} warmMs={warmMs} staleDisplayMs={staleDisplayMs} forceMs={forceMs} scans={afterForce.ScanCount - before.ScanCount} forceScans={afterForce.ScanCount - beforeForce.ScanCount}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "WINGET_CACHE_SMOKE_FAIL", ex);
                Environment.Exit(58);
            }
            return true;
        }

        if (HasArg(cli, "--winget-user-context-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var groups = await UnelevatedProcessRunner.RunAsync("whoami.exe", "/groups", TimeSpan.FromSeconds(10), cts.Token);
                if (!groups.Success || !groups.StdOut.Contains("S-1-16-8192", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unelevated runner nepotvrdil Medium Integrity user token.");

                var version = await UnelevatedProcessRunner.RunAsync("winget.exe", "--version", TimeSpan.FromSeconds(15), cts.Token);
                if (!version.Success || string.IsNullOrWhiteSpace(version.StdOut))
                    throw new InvalidOperationException("Winget se v unelevated user contextu nepodařilo spustit.");

                LogService.Info("winget", $"WINGET_USER_CONTEXT_SMOKE_OK version={version.StdOut.Trim()} integrity=medium");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "WINGET_USER_CONTEXT_SMOKE_FAIL", ex);
                Environment.Exit(59);
            }
            return true;
        }

        if (HasArg(cli, "--section-smoke") && _window is MainWindow sectionMw)
        {
            await Task.Delay(500);
            await sectionMw.RunSectionSmokeTestAsync();
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--lazy-actionbar-smoke") && _window is MainWindow lazyActionBarMw)
        {
            try
            {
                await lazyActionBarMw.RunLazyActionBarSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("ACTIONBAR_LAZY_SMOKE_FAIL", ex);
                Environment.Exit(67);
            }
            return true;
        }

        if (HasArg(cli, "--hero-action-deferred-smoke") && _window is MainWindow heroActionMw)
        {
            try
            {
                await heroActionMw.RunHeroActionDeferredSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("HERO_ACTION_DEFERRED_SMOKE_FAIL", ex);
                Environment.Exit(68);
            }
            return true;
        }

        if (HasArg(cli, "--deferred-titlebar-smoke") && _window is MainWindow deferredTitlebarMw)
        {
            try
            {
                await deferredTitlebarMw.RunDeferredTitlebarSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("DEFERRED_TITLEBAR_SMOKE_FAIL", ex);
                Environment.Exit(76);
            }
            return true;
        }

        if (HasArg(cli, "--background-activity-cache-smoke") && _window is MainWindow backgroundCacheMw)
        {
            try
            {
                await Task.Delay(500);
                await backgroundCacheMw.RunBackgroundActivityCacheSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("BACKGROUND_ACTIVITY_CACHE_SMOKE_FAIL", ex);
                Environment.Exit(94);
            }
            return true;
        }
        if (HasArg(cli, "--startup-render-smoke") && _window is MainWindow startupRenderMw)
        {
            try
            {
                await Task.Delay(500);
                await startupRenderMw.RunStartupRenderSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("STARTUP_RENDER_SMOKE_FAIL", ex);
                Environment.Exit(59);
            }
            return true;
        }

        if (HasArg(cli, "--power-render-smoke") && _window is MainWindow powerRenderMw)
        {
            try
            {
                await Task.Delay(500);
                await powerRenderMw.RunPowerRenderSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWER_RENDER_SMOKE_FAIL", ex);
                Environment.Exit(58);
            }
            return true;
        }

        if (HasArg(cli, "--power-ui-smoke") && _window is MainWindow powerUiMw)
        {
            try
            {
                await Task.Delay(500);
                await powerUiMw.RunPowerUiSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWER_UI_SMOKE_FAIL", ex);
                Environment.Exit(55);
            }
            return true;
        }

        if (HasArg(cli, "--power-window-backend-smoke"))
        {
            try
            {
                await Task.Delay(500);
                LogService.Info("POWER_WINDOW_BACKEND_SMOKE_START");
                string result = await PerformanceProfilesService.ApplyPerformancePowerProfileAsync();
                LogService.Info("POWER_WINDOW_BACKEND_SMOKE_BACKEND_OK | " + result.Replace(Environment.NewLine, " | "));
                await Task.Delay(1500);
                LogService.Info("POWER_WINDOW_BACKEND_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWER_WINDOW_BACKEND_SMOKE_FAIL", ex);
                Environment.Exit(56);
            }
            return true;
        }

        if (HasArg(cli, "--drivers-ui-smoke") && _window is MainWindow driversMw)
        {
            await Task.Delay(500);
            await driversMw.RunDriversUiSmokeAsync();
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--task-scheduler-ui-smoke") && _window is MainWindow taskMw)
        {
            await Task.Delay(500);
            await taskMw.RunTaskSchedulerUiSmokeAsync();
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--regression-smoke") && _window is MainWindow regressionMw)
        {
            await Task.Delay(500);
            await regressionMw.RunRegressionSmokeTestAsync();
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--soak-smoke") && _window is MainWindow soakMw)
        {
            await Task.Delay(500);
            try { await soakMw.RunSoakAuditAsync(settleStartup: true); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("SOAK_SMOKE_FAIL", ex); Environment.Exit(51); }
            return true;
        }

        if (HasArg(cli, "--critical-leak-smoke") && _window is MainWindow criticalLeakMw)
        {
            await Task.Delay(500);
            try { await criticalLeakMw.RunCriticalSectionLeakAuditAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("CRITICAL_LEAK_SMOKE_FAIL", ex); Environment.Exit(52); }
            return true;
        }

        if (HasArg(cli, "--ui-cache-residency-smoke") && _window is MainWindow cacheResidencyMw)
        {
            await Task.Delay(500);
            try { await cacheResidencyMw.RunUiCacheResidencyAuditAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("UI_CACHE_RESIDENCY_SMOKE_FAIL", ex); Environment.Exit(55); }
            return true;
        }

        if (HasArg(cli, "--nav500-smoke") && _window is MainWindow nav500Mw)
        {
            await Task.Delay(500);
            try { await nav500Mw.RunNavigation500SoakAuditAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("NAV500_SMOKE_FAIL", ex); Environment.Exit(54); }
            return true;
        }

        if (HasArg(cli, "--navigation-tail-smoke") && _window is MainWindow navTailMw)
        {
            await Task.Delay(500);
            try { await navTailMw.RunNavigationTailLatencySmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("NAV_TAIL_SMOKE_FAIL", ex); Environment.Exit(52); }
            return true;
        }

        return false;
    }
}
