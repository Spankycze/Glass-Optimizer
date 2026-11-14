using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private readonly List<DesktopUpdateNotificationWindow> _desktopUpdateNotifications = new();

    private async Task CheckDesktopUpdateNotificationsAsync()
    {
        if (Environment.GetCommandLineArgs().Any(x =>
                x.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("benchmark", StringComparison.OrdinalIgnoreCase)))
            return;

        CancellationToken windowToken = _windowLifetimeCts.Token;
        try
        {
            await Task.Delay(2200, windowToken);

            int stackIndex = 0;
            AppUpdateCheck appUpdate = await AppUpdateService.CheckAsync(windowToken);
            windowToken.ThrowIfCancellationRequested();
            if (appUpdate.Available && appUpdate.Manifest != null)
            {
                ShowAppUpdateDesktopNotification(appUpdate.Manifest, stackIndex++);
            }

            await Task.Delay(350, windowToken);

            GpuDriverInfo? gpu = await GpuDriverService.DetectPrimaryGpuAsync(windowToken);
            if (!GpuDriverService.IsExostenzaCompatible(gpu))
                return;

            DriverReleaseInfo? exostenza = await GpuDriverService.CheckExostenzaAsync(windowToken);
            windowToken.ThrowIfCancellationRequested();
            if (exostenza == null ||
                string.IsNullOrWhiteSpace(exostenza.Version) ||
                exostenza.Version == "?" ||
                gpu == null ||
                !GpuDriverService.IsNewer(exostenza.Version, gpu.DisplayVersion))
                return;

            ShowExostenzaDesktopNotification(gpu, exostenza, stackIndex);
        }
        catch (OperationCanceledException) when (windowToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("update", "Desktop update notification check failed", ex);
        }
    }

    private void ShowAppUpdateDesktopNotification(AppUpdateManifest manifest, int stackIndex)
        => SafeTaskService.Forget(RunAutomaticAppUpdatePopupAsync(manifest, stackIndex), "update", "Automatic application update");

    internal static async Task<bool> RunAutomaticAppUpdatePopupAsync(AppUpdateManifest manifest, int stackIndex = 0)
    {
        using var updateMutex = new Mutex(false, @"Local\GlassOptimizerAutomaticUpdatePopup");
        bool ownsMutex;
        try { ownsMutex = updateMutex.WaitOne(TimeSpan.Zero); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) return false;
        try
        {
        var window = new DesktopUpdateNotificationWindow(
            "Spanky Update",
            "Je k dispozici nová aktualizace",
            "Nová verze " + manifest.Version,
            string.IsNullOrWhiteSpace(manifest.Changelog)
                ? "Je dostupná nová verze Optimizeru. Installer se před spuštěním kryptograficky ověří."
                : manifest.Changelog!,
            async (uiProgress, token) =>
            {
                var progress = new Progress<AppUpdateDownloadProgress>(p =>
                {
                    int percent = p.Percent < 0 ? 3 : Math.Clamp(p.Percent, 0, 100) * 48 / 100;
                    string detail = p.Total.HasValue
                        ? $"{p.Received / 1048576d:0.0} / {p.Total.Value / 1048576d:0.0} MB"
                        : $"{p.Received / 1048576d:0.0} MB";
                    uiProgress.Report(new DesktopUpdateProgress(
                        percent,
                        $"Stahuji Glass Optimizer {RuntimeVersionInfo.PublicVersion(manifest.Version)}…",
                        detail));
                });

                string path = await AppUpdateService.DownloadAsync(manifest, progress, token);
                uiProgress.Report(new DesktopUpdateProgress(50, "Installer je stažený a ověřený.", Path.GetFileName(path)));

                return new DesktopUpdateCompletion(
                    "Aktualizace je připravená k instalaci.",
                    "Nainstalovat",
                     null,
                     "SHA-256 + podpis manifestu ověřen.",
                      async (installProgress, installToken) =>
                      {
                           installToken.ThrowIfCancellationRequested();
                           installProgress.Report(new DesktopUpdateProgress(50, "Spouštím tichou instalaci…"));
                           using var installer = AppUpdateService.LaunchInstallerForUpdate(path, manifest.Sha256, silent: true);
                           using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                           try
                           {
                               using var wait = CancellationTokenSource.CreateLinkedTokenSource(installToken, timeout.Token);
                               await installer.WaitForExitAsync(wait.Token);
                            }
                            catch (OperationCanceledException) when (installToken.IsCancellationRequested)
                            {
                               try { await installer.WaitForExitAsync(timeout.Token); }
                               catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                               {
                                   try { installer.Kill(entireProcessTree: true); } catch { }
                                   throw new TimeoutException("Po zrušení popupu instalátor nedokončil práci do 30 minut.");
                               }
                               throw;
                            }
                           catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                           {
                               try { installer.Kill(entireProcessTree: true); } catch { }
                               throw new TimeoutException("Instalátor nedokončil práci do 30 minut.");
                           }
                           if (!AppUpdateService.IsSuccessfulInstallerExitCode(installer.ExitCode))
                               throw new InvalidOperationException($"Instalace aktualizace selhala ({installer.ExitCode}).");
                      });
            },
            stackIndex);

        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(window.CompletedSuccessfully);
        await window.StartAutomaticUpdateAsync();
        return await closed.Task;
        }
        finally { updateMutex.ReleaseMutex(); }
    }

    private void ShowExostenzaDesktopNotification(GpuDriverInfo gpu, DriverReleaseInfo release, int stackIndex)
    {
        var window = new DesktopUpdateNotificationWindow(
            "GPU Driver",
            "Exostenza Edition",
            $"RTX {release.Version} • aktuálně {gpu.DisplayVersion}",
            "Je dostupná novější community NVIDIA Exostenza verze. Optimizer balík stáhne, rozbalí a ověří setup.",
            async (uiProgress, token) =>
            {
                DriverReleaseInfo resolved = release;
                GpuDriverService.CleanupExostenzaCache(release.Version);
                uiProgress.Report(new DesktopUpdateProgress(6, "Hledám přímý Exostenza download…"));

                resolved = await GpuDriverService.ResolveExostenzaDownloadAsync(resolved, token);
                if (string.IsNullOrWhiteSpace(resolved.DownloadUrl))
                    throw new InvalidOperationException("Přímý Exostenza download nebyl nalezen.");

                string? package = GpuDriverService.TryGetCachedPackage(resolved);
                if (string.IsNullOrWhiteSpace(package))
                {
                    var progress = new Progress<DriverDownloadProgress>(p =>
                    {
                        int sourcePercent = p.Percent < 0 ? 0 : Math.Clamp(p.Percent, 0, 100);
                        int percent = 10 + (int)Math.Round(sourcePercent * 0.65);
                        string detail = p.Total.HasValue
                            ? $"{p.Received / 1048576d:0.0} / {p.Total.Value / 1048576d:0.0} MB"
                            : $"{p.Received / 1048576d:0.0} MB";
                        uiProgress.Report(new DesktopUpdateProgress(
                            percent,
                            $"Stahuji Exostenza {release.Version}…",
                            detail));
                    });
                    package = await GpuDriverService.DownloadAsync(resolved, progress, token);
                }
                else
                {
                    uiProgress.Report(new DesktopUpdateProgress(75, "Používám už stažený Exostenza balík.", Path.GetFileName(package)));
                }

                uiProgress.Report(new DesktopUpdateProgress(82, "Rozbaluji driver balík…", Path.GetFileName(package)));
                string installer = await GpuDriverService.ExtractArchiveAndFindSetupAsync(package, token);

                uiProgress.Report(new DesktopUpdateProgress(91, "Ověřuji NVIDIA setup…", Path.GetFileName(installer)));
                var security = await FileSecurityService.InspectAsync(installer, token);
                token.ThrowIfCancellationRequested();
                if (!security.IsTrustedSignature)
                    throw new InvalidOperationException("Setup uvnitř Exostenza balíku nemá platný digitální podpis.");

                uiProgress.Report(new DesktopUpdateProgress(96, "Připravuji bezpečnou instalaci…", security.Signer));
                await WindowsActions.CreateRestorePointAsync("Before Exostenza GPU driver");
                token.ThrowIfCancellationRequested();

                uiProgress.Report(new DesktopUpdateProgress(100, "Exostenza je připravená.", $"Verze {release.Version}"));

                return new DesktopUpdateCompletion(
                    "Driver je stažený, rozbalený a ověřený.",
                    "Nainstalovat",
                     null,
                     "Před instalací byl vytvořen bod obnovení.",
                     async (installProgress, installToken) =>
                     {
                         installToken.ThrowIfCancellationRequested();
                         installProgress.Report(new DesktopUpdateProgress(97, "Spouštím instalaci driveru…", release.Version));
                         GpuDriverService.LaunchInstaller(installer);
                         GpuDriverService.MarkExostenzaCleanupPending(release.Version);
                         for (int attempt = 0; attempt < 36; attempt++)
                         {
                             installToken.ThrowIfCancellationRequested();
                              if (GpuDriverService.IsKnownGpuInstallerRunning())
                              {
                                  installProgress.Report(new DesktopUpdateProgress(
                                      97, "Instaluji Exostenza driver…", release.Version));
                             }
                             else
                             {
                                 string? state = await GpuDriverService.ReconcilePendingExostenzaAsync(installToken);
                                 if (state?.Contains("nainstalována a ověřena", StringComparison.OrdinalIgnoreCase) == true)
                                 {
                                     installProgress.Report(new DesktopUpdateProgress(100, "Driver je nainstalovaný a ověřený.", release.Version));
                                     return;
                                 }
                                 installProgress.Report(new DesktopUpdateProgress(99, "Ověřuji výsledek instalace driveru…", state ?? release.Version));
                             }
                             await Task.Delay(TimeSpan.FromSeconds(5), installToken);
                         }
                         throw new TimeoutException("Instalaci driveru se nepodařilo včas ověřit.");
                     });
            },
            stackIndex);

        TrackDesktopUpdateWindow(window);
        window.ShowWithoutActivation();
    }

    private void TrackDesktopUpdateWindow(DesktopUpdateNotificationWindow window)
    {
        _desktopUpdateNotifications.Add(window);
        window.Closed += (_, _) => _desktopUpdateNotifications.Remove(window);
    }

    internal async Task RunDesktopStatusNotificationSmokeAsync(bool visualPreview = false)
    {
        var window = new DesktopStatusNotificationWindow(
            "Hotovo \u2022 ov\u011b\u0159eno",
            "Stavov\u00e1 notifikace v oznamovac\u00ed oblasti. Nic se neaktivuje ani nem\u011bn\u00ed.",
            ActionFeedbackKind.Verified,
            visualPreview: visualPreview);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(true);
        window.ShowWithoutActivation();
        if (visualPreview)
        {
            // Render the popup itself for visual QA, then let the 7 s auto-hide close it.
            await Task.Delay(450);
            string dir = Path.Combine(LogService.Root, "visual-current");
            Directory.CreateDirectory(dir);
            await SaveVisualSnapshotAsync(window.PreviewElement, Path.Combine(dir, "status-popup.png"));
            LogService.Info("visual-qa", "VISUAL_STATUS_POPUP_CAPTURE_OK");
            await closed.Task;
            return;
        }
        await Task.Delay(250);
        if (!window.IsShownWithoutActivation())
            throw new InvalidOperationException("Status popup is not visible or lost its no-activate style.");
        if (window.PopupWidth <= 0 || window.PopupHeight <= 0)
            throw new InvalidOperationException($"Status popup has no measurable size ({window.PopupWidth}x{window.PopupHeight}).");
        if (window.HoldsForeground())
            throw new InvalidOperationException("Status popup took the foreground window.");
        await Task.Delay(300);
        if (window.HoldsForeground())
            throw new InvalidOperationException("Status popup took the foreground window after showing.");
        window.Close();
        if (await Task.WhenAny(closed.Task, Task.Delay(3000)) != closed.Task)
            throw new InvalidOperationException("Status popup did not close.");
        LogService.Info("ui", $"DESKTOP_STATUS_NOTIFICATION_SMOKE_OK size={window.PopupWidth}x{window.PopupHeight}");
    }

    public async Task RunDesktopUpdateNotificationSmokeAsync(bool visualPreview = false, bool exostenzaPreview = false)
    {
        var window = new DesktopUpdateNotificationWindow(
            exostenzaPreview ? "Exostenza Edition" : "Spanky Update",
            exostenzaPreview ? "NVIDIA ovladač — simulace" : visualPreview ? "Nová aktualizace — simulace" : "Desktop update popup",
            visualPreview ? "SIMULACE" : "Smoke 15.4",
            visualPreview ? "Pouze náhled. Nic se nestahuje ani neinstaluje." : "Glass upozornění s reálným průběhem a tlačítky Stáhnout / Později.",
            async (progress, token) =>
            {
                for (int i = 0; i <= (visualPreview ? 48 : 100); i += (visualPreview ? 2 : 20))
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(new DesktopUpdateProgress(i, visualPreview ? "Stahuji aktualizaci…" : $"Testuji progress… {i} %", visualPreview ? $"{i / 48d * 61.4:0.0} / 61,4 MB — simulace" : $"{i * 1.7:0.0} MB"));
                    await Task.Delay(visualPreview ? 250 : 80, token);
                }
                if (visualPreview)
                {
                    progress.Report(new DesktopUpdateProgress(49, "Ověřuji stažený installer…", "Simulace kontroly SHA-256"));
                    await Task.Delay(1000, token);
                    return new DesktopUpdateCompletion("Aktualizace je připravená k instalaci.", "Nainstalovat", null,
                        "Pouze simulace — instalátor se nespustí.",
                        async (installProgress, installToken) =>
                        {
                            for (int percent = 50; percent <= 100; percent += 2)
                            {
                                installToken.ThrowIfCancellationRequested();
                                installProgress.Report(new DesktopUpdateProgress(percent, "Náhled instalace…", "SIMULACE — žádné změny systému"));
                                await Task.Delay(200, installToken);
                            }
                            await Task.Delay(1000, installToken);
                        });
                }
                return new DesktopUpdateCompletion("Smoke hotový.", "Zavřít", () => Task.CompletedTask, "Desktop glass updater OK");
            }, visualPreview: visualPreview);

        TrackDesktopUpdateWindow(window);
        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(true);
        IntPtr foregroundBefore = GetForegroundWindow();
        window.ShowWithoutActivation();
        if (visualPreview)
        {
            // Keep the interactive preview visible until the user closes it.
            SafeTaskService.Forget(window.StartAutomaticUpdateAsync(), "update", "Automatic update preview");
            await closed.Task;
            return;
        }
        await Task.Delay(180);
        IntPtr foregroundAfter = GetForegroundWindow();
        if (foregroundBefore != IntPtr.Zero && foregroundAfter != foregroundBefore)
            throw new InvalidOperationException("Updater popup changed the foreground window.");

        if (UseGlassChrome)
        {
            static void RequireTransparent(Microsoft.UI.Xaml.Media.Brush? brush, string name)
            {
                if (brush is not Microsoft.UI.Xaml.Media.SolidColorBrush solid || solid.Color.A != 0)
                    throw new InvalidOperationException($"{name} became opaque while updater popup was visible.");
            }

            RequireTransparent(RootGrid.Background, "RootGrid");
            RequireTransparent(_pageScroll.Background, "PageScroll");
            RequireTransparent(PageHost.Background, "PageHost");
            if (SystemBackdrop is not OptimizerGlassBackdrop backdrop || !backdrop.IsControllerReady)
                throw new InvalidOperationException("Main Optimizer acrylic controller dropped while updater popup was visible.");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            var inactiveTitle = appWindow.TitleBar.InactiveBackgroundColor;
            var inactiveButton = appWindow.TitleBar.ButtonInactiveBackgroundColor;
            if ((inactiveTitle.HasValue && inactiveTitle.Value.A != 0) ||
                (inactiveButton.HasValue && inactiveButton.Value.A != 0))
                throw new InvalidOperationException("Inactive titlebar became opaque while updater popup was visible.");
        }

        await Task.Delay(470);
        window.Close();
    }
}
